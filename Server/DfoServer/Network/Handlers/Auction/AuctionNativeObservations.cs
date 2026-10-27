using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Handlers.Auction
{
    // Per authenticated owner. Runtime calls Record only after successful row
    // send, and Acknowledge only after the committed operation's ACK was sent.
    // Query snapshots remain immutable and are never themselves write authority.
    internal sealed class AuctionNativeObservations
    {
        private readonly AuctionQueryPrincipal principal;
        private readonly AuctionItemCodec codec;
        private readonly object sync = new();
        private readonly Dictionary<AuctionQueryView, ViewState> views = new();
        // Prevent an older already-frozen page from resurrecting a row cleared
        // or superseded by our own ACK. This is only a denial floor, never a row.
        private readonly Dictionary<long, AckFloor> ackFloors = new();

        internal AuctionNativeObservations(AuctionQueryPrincipal principal, AuctionItemCodec codec)
        {
            if (principal == null || principal.SessionId == Guid.Empty || principal.Generation <= 0
                || principal.AccountId <= 0 || principal.CharacterId <= 0)
                throw new ArgumentException("An authenticated query owner is required.", nameof(principal));
            this.principal = principal;
            this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }

        internal bool RecordSentRows(AuctionQueryView view, AuctionQuerySnapshot snapshot,
            IReadOnlyList<AuctionQueryRow> actuallySentRows, long nowUnix, out string reason)
        {
            lock (sync)
            {
                Prune(nowUnix);
                if (!Enum.IsDefined(view) || actuallySentRows == null || nowUnix < 0)
                    return Fail("InvalidSentRows", out reason);
                if (!ValidSnapshot(snapshot, nowUnix, out reason)) return false;
                if (view == AuctionQueryView.Search && actuallySentRows.Count > AuctionQueryEngine.BatchSize)
                    return Fail("SearchSendExceedsPage", out reason);
                var original = new Dictionary<long, AuctionQueryRow>();
                foreach (var row in snapshot.Rows)
                    if (row == null || row.OrderId <= 0 || row.OrderVersion <= 0 || !original.TryAdd(row.OrderId, row))
                        return Fail("InvalidOrAmbiguousSnapshotRow", out reason);
                var sent = new HashSet<long>();
                foreach (var row in actuallySentRows)
                    if (row == null || !sent.Add(row.OrderId) || !original.TryGetValue(row.OrderId, out var frozen) || frozen != row)
                        return Fail("SentRowsDifferFromSnapshot", out reason);
                if (!views.TryGetValue(view, out var state) || state.Source.SnapshotId != snapshot.SnapshotId)
                {
                    state = new ViewState(snapshot);
                    views[view] = state; // A valid new query replaces this view, even if empty.
                }
                foreach (var row in actuallySentRows)
                {
                    if (row.ExpiresAtUnix <= nowUnix || state.Removed.Contains(row.OrderId)) continue;
                    if (ackFloors.TryGetValue(row.OrderId, out var floor)
                        && (row.OrderVersion < floor.Version || (floor.Terminal && row.OrderVersion <= floor.Version))) continue;
                    if (!state.Rows.TryGetValue(row.OrderId, out var prior) || prior.OrderVersion <= row.OrderVersion)
                        state.Rows[row.OrderId] = row;
                }
                reason = ""; return true;
            }
        }

        internal bool TryGetObservedOrderSnapshot(long orderId, long nowUnix,
            out AuctionQuerySnapshot observed, out string reason)
        {
            lock (sync)
            {
                observed = null; Prune(nowUnix);
                if (orderId <= 0 || nowUnix < 0) return Fail("InvalidObservationLookup", out reason);
                if (!Find(orderId, nowUnix, out var selected, out var row, out reason)) return false;
                observed = new AuctionQuerySnapshot(principal, selected.Source.CriteriaFingerprint,
                    selected.Source.CatalogPvfSha256, selected.Source.CapturedAtUnix, selected.Source.ValidUntilUnix,
                    ImmutableArray.Create(row));
                return true;
            }
        }

        internal bool AcknowledgeCommitted(AuctionMarketRequest request, AuctionOrderSnapshot after,
            long nowUnix, out string reason)
        {
            lock (sync)
            {
                Prune(nowUnix);
                if (request?.Actor == null || request.OperationId == Guid.Empty || after == null || nowUnix < 0
                    || request.Actor.CharacterId != principal.CharacterId || request.Actor.AccountId != principal.AccountId
                    || request.OrderId != after.OrderId || request.ExpectedVersion <= 0 || request.ExpectedVersion == long.MaxValue
                    || after.Version != request.ExpectedVersion + 1 || after.PendingOperationId.HasValue)
                    return Fail("InvalidAcknowledgedIdentityOrVersion", out reason);
                if (!Find(request.OrderId, nowUnix, out _, out var before, out reason)) return false;
                if (before.OrderVersion != request.ExpectedVersion) return Fail("AcknowledgmentDoesNotMatchObservation", out reason);
                if (after.Seller == null || after.Seller.CharacterId != before.SellerCharacterId
                    || after.Seller.AccountId != before.SellerAccountId || after.Seller.FrozenName != before.SellerName
                    || after.CreatedAtUnix != before.CreatedAtUnix || after.ExpiresAtUnix != before.ExpiresAtUnix)
                    return Fail("AcknowledgedOrderFactsDiffer", out reason);
                AuctionItemEnvelope envelope;
                ItemCore core;
                try { envelope = codec.DeserializeStrict(after.EscrowEnvelopeJson); core = codec.DecodeStrict(envelope); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is System.IO.IOException
                    || ex is System.Text.Json.JsonException || ex is FormatException || ex is OverflowException)
                { return Fail("AcknowledgedEscrowInvalid", out reason); }
                if (core.ItemId != before.TemplateId) return Fail("AcknowledgedTemplateMismatch", out reason);
                bool partial = false;
                switch (request.Action)
                {
                    case AuctionOperationAction.BuyPiece:
                        if (before.HighestBidderCharacterId.HasValue || before.CurrentOrOpeningBidGold.HasValue
                            || request.Actor.AccountId == before.SellerAccountId || !before.ExactUnitPriceGold.HasValue
                            || request.Quantity <= 0 || request.Quantity > before.RemainingQuantity
                            || after.RemainingQuantity != before.RemainingQuantity - request.Quantity)
                            return Fail("AcknowledgedPieceQuantityMismatch", out reason);
                        try
                        {
                            if (request.OfferedGold != checked(before.ExactUnitPriceGold.Value * request.Quantity))
                                return Fail("AcknowledgedPieceAmountMismatch", out reason);
                            partial = after.RemainingQuantity > 0;
                            if (partial)
                            {
                                if (after.State != AuctionOrderState.Active || after.UnitPriceGold != before.ExactUnitPriceGold
                                    || after.OpeningBidGold.HasValue || after.HighestBidder != null || after.FundedBidGold != 0
                                    || after.BuyoutGold != checked(before.ExactUnitPriceGold.Value * after.RemainingQuantity)
                                    || !InventoryStackRuleService.IsStackable(core) || core.Count != after.RemainingQuantity)
                                    return Fail("AcknowledgedPartialFactsMismatch", out reason);
                                var old = codec.DeserializeStrict(before.EscrowEnvelopeJson);
                                if (old.ContentHashHex != before.EscrowContentHash) return Fail("ObservedEscrowHashMismatch", out reason);
                                var expected = codec.DecodeStrict(old); expected.Count = after.RemainingQuantity;
                                if (codec.CreateTransferSnapshot(old, expected).ContentHashHex != envelope.ContentHashHex)
                                    return Fail("AcknowledgedPartialLineageMismatch", out reason);
                            }
                            else if (after.State != AuctionOrderState.Sold) return Fail("AcknowledgedPieceTerminalMismatch", out reason);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is OverflowException)
                        { return Fail("AcknowledgedPieceInvalid", out reason); }
                        break;
                    case AuctionOperationAction.Bid:
                        if (after.State != AuctionOrderState.Active || after.RemainingQuantity != before.RemainingQuantity
                            || after.HighestBidder?.CharacterId != principal.CharacterId || after.HighestBidder?.AccountId != principal.AccountId
                            || after.FundedBidGold != request.OfferedGold || after.FundedBidGold <= 0
                            || request.Actor.AccountId == before.SellerAccountId)
                            return Fail("AcknowledgedBidFactsMismatch", out reason);
                        break;
                    case AuctionOperationAction.Cancel:
                        if (after.State != AuctionOrderState.Cancelled || after.RemainingQuantity != 0
                            || request.Actor.CharacterId != before.SellerCharacterId || request.Actor.AccountId != before.SellerAccountId)
                            return Fail("AcknowledgedCancelFactsMismatch", out reason);
                        break;
                    case AuctionOperationAction.Buyout:
                        if (after.State != AuctionOrderState.Sold || after.RemainingQuantity != 0
                            || before.BuyoutGold != request.OfferedGold || request.Actor.AccountId == before.SellerAccountId)
                            return Fail("AcknowledgedBuyoutFactsMismatch", out reason);
                        break;
                    default: return Fail("UnsupportedNativeAcknowledgment", out reason);
                }
                var updated = before with { OrderVersion = after.Version, RemainingQuantity = after.RemainingQuantity,
                    CurrentOrOpeningBidGold = after.FundedBidGold > 0 ? after.FundedBidGold : after.OpeningBidGold,
                    BuyoutGold = after.BuyoutGold, ExactUnitPriceGold = after.UnitPriceGold,
                    HighestBidderCharacterId = after.HighestBidder?.CharacterId, HighestBidderAccountId = after.HighestBidder?.AccountId,
                    EscrowEnvelopeJson = after.EscrowEnvelopeJson, EscrowContentHash = envelope.ContentHashHex };
                foreach (var state in views.Values)
                {
                    if (!state.Rows.TryGetValue(request.OrderId, out var row)) continue;
                    if (partial && row.OrderVersion == request.ExpectedVersion) state.Rows[request.OrderId] = updated;
                    else { state.Rows.Remove(request.OrderId); state.Removed.Add(request.OrderId); }
                }
                // Every still-valid pre-ACK query expires within the policy TTL.
                // Saturation avoids a clock overflow and never authorizes a row.
                var retainUntil = nowUnix > long.MaxValue - AuctionQueryEngine.SnapshotSeconds
                    ? long.MaxValue : nowUnix + AuctionQueryEngine.SnapshotSeconds;
                ackFloors[request.OrderId] = new AckFloor(after.Version, after.State != AuctionOrderState.Active, retainUntil);
                reason = ""; return true;
            }
        }

        private bool ValidSnapshot(AuctionQuerySnapshot snapshot, long nowUnix, out string reason)
        {
            if (snapshot == null || snapshot.Principal != principal) return Fail("SnapshotOwnerMismatch", out reason);
            if (snapshot.Rows.IsDefault || snapshot.Rows.Length > AuctionQueryEngine.MaximumSnapshotRows
                || snapshot.CapturedAtUnix < 0 || snapshot.ValidUntilUnix <= snapshot.CapturedAtUnix
                || snapshot.ValidUntilUnix - snapshot.CapturedAtUnix > AuctionQueryEngine.SnapshotSeconds)
                return Fail("InvalidQuerySnapshot", out reason);
            if (nowUnix < snapshot.CapturedAtUnix || nowUnix >= snapshot.ValidUntilUnix) return Fail("SnapshotExpiredOrNotYetCaptured", out reason);
            reason = ""; return true;
        }

        private bool Find(long orderId, long nowUnix, out ViewState selected, out AuctionQueryRow found, out string reason)
        {
            selected = null; found = null;
            foreach (var state in views.Values)
            {
                if (nowUnix < state.Source.CapturedAtUnix || nowUnix >= state.Source.ValidUntilUnix
                    || !state.Rows.TryGetValue(orderId, out var row) || nowUnix >= row.ExpiresAtUnix) continue;
                if (found != null && found.OrderVersion == row.OrderVersion && found != row)
                    return Fail("ConflictingObservedOrderFacts", out reason);
                if (found == null || row.OrderVersion > found.OrderVersion
                    || (row.OrderVersion == found.OrderVersion && state.Source.ValidUntilUnix > selected.Source.ValidUntilUnix))
                { selected = state; found = row; }
            }
            if (found == null) return Fail("OrderWasNotSentOrObservationExpired", out reason);
            reason = ""; return true;
        }

        private void Prune(long nowUnix)
        {
            if (nowUnix < 0) return;
            foreach (var view in views.Where(x => nowUnix >= x.Value.Source.ValidUntilUnix).Select(x => x.Key).ToArray()) views.Remove(view);
            foreach (var id in ackFloors.Where(x => nowUnix >= x.Value.RetainUntilUnix).Select(x => x.Key).ToArray()) ackFloors.Remove(id);
        }
        private static bool Fail(string value, out string reason) { reason = value; return false; }
        private sealed class ViewState
        {
            internal ViewState(AuctionQuerySnapshot source) => Source = source;
            internal AuctionQuerySnapshot Source { get; }
            internal Dictionary<long, AuctionQueryRow> Rows { get; } = new();
            internal HashSet<long> Removed { get; } = new();
        }
        private sealed record AckFloor(long Version, bool Terminal, long RetainUntilUnix);
    }
}
