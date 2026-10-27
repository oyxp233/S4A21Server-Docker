using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Parsers.Auction;

namespace DfoServer.Network.Handlers.Auction
{
    internal enum AuctionNativeMutationStatus { Ready, Unavailable, Rejected }
    internal sealed record AuctionNativeMutationResult(AuctionNativeMutationStatus Status, string Reason, AuctionMarketRequest Request)
    {
        internal bool IsReady => Status == AuctionNativeMutationStatus.Ready;
    }

    // Pure mutation framing adapter. Its only version source is an authenticated
    // owner's previously observed immutable query row. No Store/ACK/asset access.
    internal static class AuctionNativeMutationAdapter
    {
        internal static AuctionNativeMutationResult NormalizeCancel(AuctionQueryPrincipal principal, AuctionParty actor,
            AuctionQuerySnapshot observed, Guid operationId, AuctionCancelRequest request, long nowUnix)
        {
            if (request == null) return Rejected("MissingCancelRequest");
            var binding = Resolve(principal, actor, observed, operationId, request.IdentityBits, nowUnix, out var row);
            if (binding != null) return binding;
            if (actor.CharacterId != row.SellerCharacterId || actor.AccountId != row.SellerAccountId)
                return Rejected("CancelSellerIdentityMismatch");
            if (row.HighestBidderCharacterId.HasValue) return Rejected("FundedBidCannotCancel");
            return Ready(operationId, AuctionOperationAction.Cancel, actor, row, row.RemainingQuantity, 0);
        }

        internal static AuctionNativeMutationResult NormalizeBidOrBuyout(AuctionQueryPrincipal principal, AuctionParty actor,
            AuctionQuerySnapshot observed, Guid operationId, AuctionBidRequest request, long nowUnix)
        {
            if (request == null) return Rejected("MissingBidRequest");
            var binding = Resolve(principal, actor, observed, operationId, request.IdentityBits, nowUnix, out var row);
            if (binding != null) return binding;
            if (!SellerTagMatches(request.SellerTag, row.SellerName)) return Rejected("SellerTagMismatch");
            if (request.Amount == 0 || request.Amount > int.MaxValue) return Rejected("InvalidBidAmount");
            if (actor.AccountId == row.SellerAccountId) return Rejected("SellerAccountCannotBuyOrBid");
            // Native023253F0/02325450: the exact displayed buyout total uses the
            // bid sender too. All other values remain Bid; Market owns the native
            // threshold, existing funded delta, over-buyout and final CAS checks.
            var action = row.BuyoutGold == request.Amount ? AuctionOperationAction.Buyout : AuctionOperationAction.Bid;
            return Ready(operationId, action, actor, row, row.RemainingQuantity, request.Amount);
        }

        internal static AuctionNativeMutationResult NormalizeBuyPiece(AuctionQueryPrincipal principal, AuctionParty actor,
            AuctionQuerySnapshot observed, Guid operationId, AuctionBuyApieceRequest request, long nowUnix)
        {
            if (request == null) return Rejected("MissingPieceRequest");
            var binding = Resolve(principal, actor, observed, operationId, request.IdentityBits, nowUnix, out var row);
            if (binding != null) return binding;
            if (!SellerTagMatches(request.SellerTag, row.SellerName)) return Rejected("SellerTagMismatch");
            if (request.ItemKey == 0 || request.ItemKey > int.MaxValue || request.ItemKey != row.TemplateId)
                return Rejected("PieceTemplateMismatch");
            if (actor.AccountId == row.SellerAccountId) return Rejected("SellerAccountCannotBuyOrBid");
            if (row.CurrentOrOpeningBidGold.HasValue || row.HighestBidderCharacterId.HasValue)
                return Rejected("PieceRequiresFixedPriceWithoutBid");
            if (!row.ExactUnitPriceGold.HasValue || !row.BuyoutGold.HasValue)
                return Unavailable("ExactFrozenUnitPriceRequired");
            if (request.Quantity == 0 || request.Quantity > AuctionEconomyPolicy.MaximumPieceRequestQuantity
                || request.Quantity > row.RemainingQuantity || request.Amount == 0 || request.Amount > int.MaxValue)
                return Rejected("InvalidPieceQuantityOrAmount");
            try
            {
                if (checked(row.ExactUnitPriceGold.Value * row.RemainingQuantity) != row.BuyoutGold.Value)
                    return Unavailable("InconsistentObservedFixedPrice");
                var total = checked(row.ExactUnitPriceGold.Value * request.Quantity);
                if (total != request.Amount || total > int.MaxValue) return Rejected("PieceAmountMismatch");
                return Ready(operationId, AuctionOperationAction.BuyPiece, actor, row, checked((int)request.Quantity), total);
            }
            catch (OverflowException) { return Rejected("PieceAmountOverflow"); }
        }

        private static AuctionNativeMutationResult Resolve(AuctionQueryPrincipal principal, AuctionParty actor,
            AuctionQuerySnapshot observed, Guid operationId, ulong identityBits, long nowUnix, out AuctionQueryRow row)
        {
            row = null;
            if (principal == null || principal.SessionId == Guid.Empty || principal.Generation <= 0
                || principal.AccountId <= 0 || principal.CharacterId <= 0 || actor == null
                || actor.AccountId != principal.AccountId || actor.CharacterId != principal.CharacterId
                || string.IsNullOrWhiteSpace(actor.FrozenName) || actor.FrozenName.IndexOf('\0') >= 0
                || operationId == Guid.Empty || nowUnix < 0)
                return Rejected("AuthenticatedActorAndOperationRequired");
            if (observed == null) return Unavailable("ObservedSnapshotRequired");
            if (observed.Principal != principal) return Rejected("ObservedSnapshotOwnerMismatch");
            if (observed.Rows.IsDefault || observed.Rows.Length > AuctionQueryEngine.MaximumSnapshotRows
                || observed.CapturedAtUnix < 0 || observed.ValidUntilUnix <= observed.CapturedAtUnix)
                return Unavailable("InvalidObservedSnapshot");
            if (nowUnix < observed.CapturedAtUnix) return Rejected("MutationTimeBeforeSnapshot");
            if (nowUnix >= observed.ValidUntilUnix) return Unavailable("ObservedSnapshotExpired");
            Span<byte> identity = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(identity, identityBits);
            if (!AuctionOrderWireIdentity.TryDecode(identity, out var orderId)) return Rejected("InvalidOpaqueOrderIdentity");
            foreach (var candidate in observed.Rows)
            {
                if (candidate == null) return Unavailable("InvalidObservedRow");
                if (candidate.OrderId != orderId) continue;
                if (row != null) return Unavailable("AmbiguousObservedOrder");
                row = candidate;
            }
            if (row == null) return Unavailable("OrderWasNotObserved");
            if (row.OrderId <= 0 || row.OrderVersion <= 0 || row.TemplateId <= 0
                || row.SellerAccountId <= 0 || row.SellerCharacterId <= 0 || row.RemainingQuantity <= 0
                || !AuctionSellerNameTag13.TryEncodeFrozenRoleName(row.SellerName, out _)
                || !ValidPrice(row.BuyoutGold) || !ValidPrice(row.CurrentOrOpeningBidGold) || !ValidPrice(row.ExactUnitPriceGold)
                || row.HighestBidderCharacterId.HasValue != row.HighestBidderAccountId.HasValue
                || row.HighestBidderCharacterId <= 0 || row.HighestBidderAccountId <= 0
                || (row.HighestBidderCharacterId.HasValue && !row.CurrentOrOpeningBidGold.HasValue))
                return Unavailable("InvalidObservedOrderFacts");
            if (nowUnix >= row.ExpiresAtUnix) return Unavailable("ObservedOrderExpired");
            return null;
        }

        private static bool SellerTagMatches(ImmutableArray<byte> supplied, string frozenSellerName)
        {
            if (supplied.IsDefaultOrEmpty || supplied.Length != 13
                || !AuctionSellerNameTag13.TryEncodeFrozenRoleName(frozenSellerName, out var expected))
                return false;
            // A21 0232E28F..E2C7 copies MyBids names through NUL without
            // clearing the remaining tag bytes; 0232549D..AC sends all13.
            // Bind every strict CP936 name byte and its terminating NUL.
            // Zero padding remains our outbound codec policy, not a native
            // input guarantee. Never accept a shortened/different name.
            var boundLength = expected.AsSpan().IndexOf((byte)0) + 1;
            return supplied.AsSpan().Slice(0, boundLength).SequenceEqual(expected.AsSpan(0, boundLength));
        }
        private static bool ValidPrice(long? value) => !value.HasValue || (value.Value > 0 && value.Value <= int.MaxValue);
        private static AuctionNativeMutationResult Ready(Guid operation, AuctionOperationAction action,
            AuctionParty actor, AuctionQueryRow observed, int quantity, long gold)
            => new(AuctionNativeMutationStatus.Ready, "", new(operation, action, observed.OrderId, observed.OrderVersion,
                actor, quantity, gold, 0));
        private static AuctionNativeMutationResult Rejected(string reason) => new(AuctionNativeMutationStatus.Rejected, reason, null);
        private static AuctionNativeMutationResult Unavailable(string reason) => new(AuctionNativeMutationStatus.Unavailable, reason, null);
    }
}
