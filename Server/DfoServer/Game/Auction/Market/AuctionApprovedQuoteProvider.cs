using System;
using System.Text.Json;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;

namespace DfoServer.Game.Auction.Market
{
    internal sealed record AuctionMarketEntitlements(long? PremiumExpiresAtUnix,
        int ActiveAndReservedAccountListings, bool NativePieceEligible);

    // Adapts the separately owned approved economic policy, without duplicating
    // its fee/rounding/deposit/native-float arithmetic. No runtime registration.
    internal sealed class AuctionApprovedQuoteProvider
    {
        private readonly Func<AuctionMarketRequest, AuctionOrderSnapshot, long, AuctionMarketEntitlements> entitlements;
        internal AuctionApprovedQuoteProvider(Func<AuctionMarketRequest, AuctionOrderSnapshot, long, AuctionMarketEntitlements> entitlements)
            => this.entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));

        internal AuctionVerifiedQuote Quote(AuctionMarketRequest request, AuctionOrderSnapshot before, long nowUnix)
        {
            // Existing funded obligations use locked terms; a current premium
            // or native catalog outage must not block their repayment.
            var context = request.Action is AuctionOperationAction.List or AuctionOperationAction.BuyPiece
                ? entitlements(request, before, nowUnix) ?? throw new AuctionMarketUnavailableException("EntitlementsUnavailable")
                : new AuctionMarketEntitlements(null, 0, false);
            long gross = 0, depositDebit = 0, fee = 0, refund = 0, forfeit = 0;
            string quota; int limit, rate; long? unit;
            if (request.Action == AuctionOperationAction.List)
            {
                long duration = checked(request.RequestedExpiryUnix - nowUnix);
                if (duration % 3600 != 0) throw new ArgumentException("Listing deadline is not an exact allowed duration.");
                var q = AuctionEconomyPolicy.QuoteListing(new(request.Actor.AccountId, request.Actor.CharacterId,
                    context.ActiveAndReservedAccountListings, nowUnix, context.PremiumExpiresAtUnix,
                    checked((int)(duration / 3600)), request.Quantity, request.OpeningBidGold, request.BuyoutGold, context.NativePieceEligible));
                depositDebit = q.DepositDebitGold; quota = "account:" + request.Actor.AccountId;
                limit = q.MaximumAccountListings; rate = q.LockedFeePercent; unit = q.FixedUnitPriceGold;
            }
            else
            {
                if (before == null || before.State != AuctionOrderState.Active || before.PendingOperationId != null
                    || before.OrderId != request.OrderId || before.Version != request.ExpectedVersion
                    || (request.Action == AuctionOperationAction.Expire ? nowUnix < before.ExpiresAtUnix : nowUnix >= before.ExpiresAtUnix))
                    throw new ArgumentException("Order is unavailable for the quote.");
                AuctionEconomyPolicy.RequirePolicyVersion(before.PolicyVersion);
                quota = before.QuotaKey; limit = AuctionEconomyPolicy.PremiumListingLimit; rate = before.FeeRatePercent; unit = before.UnitPriceGold;
                switch (request.Action)
                {
                    case AuctionOperationAction.Bid:
                        if (!before.OpeningBidGold.HasValue) throw new ArgumentException("Fixed-price order does not accept bids.");
                        AuctionEconomyPolicy.QuoteBid(new(before.Seller.AccountId, request.Actor.AccountId, request.Actor.CharacterId,
                            before.HighestBidder?.AccountId, before.HighestBidder?.CharacterId, before.FundedBidGold,
                            before.FundedBidGold > 0 ? before.FundedBidGold : before.OpeningBidGold.Value, request.OfferedGold, before.BuyoutGold));
                        gross = request.OfferedGold;
                        break;
                    case AuctionOperationAction.Buyout:
                        if (!before.BuyoutGold.HasValue || request.OfferedGold != before.BuyoutGold.Value) throw new ArgumentException("Buyout total mismatch.");
                        AuctionEconomyPolicy.QuoteBuyout(before.Seller.AccountId, request.Actor.AccountId, request.Actor.CharacterId,
                            before.HighestBidder?.AccountId, before.HighestBidder?.CharacterId, before.FundedBidGold, before.BuyoutGold.Value);
                        gross = before.BuyoutGold.Value;
                        break;
                    case AuctionOperationAction.BuyPiece:
                        if (!unit.HasValue) throw new ArgumentException("Order has no fixed unit price.");
                        gross = AuctionEconomyPolicy.QuotePiece(new(before.Seller.AccountId, request.Actor.AccountId,
                            context.NativePieceEligible, before.OpeningBidGold.HasValue, before.HighestBidder != null,
                            unit.Value, before.RemainingQuantity, request.Quantity, request.OfferedGold)).GrossGold;
                        break;
                    case AuctionOperationAction.Expire when before.HighestBidder != null:
                        gross = before.FundedBidGold;
                        break;
                    case AuctionOperationAction.Cancel:
                    case AuctionOperationAction.Expire:
                        var close = AuctionEconomyPolicy.QuoteClose(new(request.Action == AuctionOperationAction.Cancel
                            ? AuctionEconomyCloseKind.Cancel : AuctionEconomyCloseKind.Expire,
                            before.HighestBidder != null, before.FundedDepositGold, nowUnix, before.ExpiresAtUnix));
                        refund = close.DepositRefundGold; forfeit = close.DepositForfeitGold;
                        break;
                    default: throw new ArgumentException("Unsupported quote action.");
                }
                if (request.Action is AuctionOperationAction.Buyout or AuctionOperationAction.BuyPiece
                    || (request.Action == AuctionOperationAction.Expire && before.HighestBidder != null))
                {
                    bool complete = request.Action != AuctionOperationAction.BuyPiece || request.Quantity == before.RemainingQuantity;
                    var fill = AuctionEconomyPolicy.QuoteFill(new(rate, before.CumulativeGrossGold, before.CumulativeFeeGold,
                        gross, before.FundedDepositGold, complete));
                    fee = fill.SettlementFeeGold; refund = fill.DepositRefundGold; forfeit = fill.DepositForfeitGold;
                }
            }
            var basis = JsonSerializer.Serialize(new { policy = AuctionEconomyPolicy.PolicyVersion,
                evaluatedAtUnix = nowUnix, context.PremiumExpiresAtUnix, context.NativePieceEligible });
            return new(AuctionQuoteAvailability.ApprovedProductPolicy, basis, nowUnix, gross, 0,
                depositDebit, fee, refund, forfeit, quota, limit, request.Binding(),
                AuctionEconomyPolicy.PolicyVersion, rate, unit);
        }
    }
}
