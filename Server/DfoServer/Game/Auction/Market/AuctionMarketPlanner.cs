using System;
using System.Collections.Generic;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction.Market
{
    internal sealed class AuctionMarketPlanner
    {
        private readonly AuctionItemCodec codec;
        private readonly AuctionMarketItemTransform itemTransform;
        private readonly bool allowFixtureQuotes;

        internal AuctionMarketPlanner(AuctionItemCodec codec, AuctionMarketItemTransform itemTransform,
            bool allowFixtureQuotes = false)
        { this.codec = codec ?? throw new ArgumentNullException(nameof(codec)); this.itemTransform = itemTransform ?? throw new ArgumentNullException(nameof(itemTransform)); this.allowFixtureQuotes = allowFixtureQuotes; }

        internal AuctionMarketPlanningResult Plan(AuctionMarketRequest request, AuctionOrderSnapshot before,
            AuctionItemEnvelope source, AuctionVerifiedQuote quote, long nowUnix)
        {
            try
            {
                ValidateRequest(request);
                if (quote == null || quote.Availability == AuctionQuoteAvailability.Unavailable)
                    return new(AuctionMarketStatus.Unavailable, "EconomicQuoteUnavailable", null);
                if (quote.Availability == AuctionQuoteAvailability.FixtureOnly && !allowFixtureQuotes)
                    return new(AuctionMarketStatus.Unavailable, "FixturePolicyNotEnabled", null);
                if (!Enum.IsDefined(quote.Availability) || quote.Binding != request.Binding()
                    || quote.ValidUntilUnix < nowUnix || string.IsNullOrWhiteSpace(quote.EvidenceFingerprint)
                    || string.IsNullOrWhiteSpace(quote.PolicyVersion)
                    || (quote.Availability == AuctionQuoteAvailability.ApprovedProductPolicy && quote.PolicyVersion != AuctionEconomyPolicy.PolicyVersion))
                    throw new ArgumentException("Quote binding, provenance or validity mismatch.");
                Nonnegative(quote.GrossGold, quote.RegistrationFeeGold, quote.DepositDebitGold,
                    quote.SettlementFeeGold, quote.DepositRefundGold, quote.DepositForfeitGold);
                var legs = new List<AuctionDeliveryLeg>();
                AuctionOrderSnapshot after;
                long debit;
                if (request.Action == AuctionOperationAction.List)
                {
                    if (before != null || request.ExpectedVersion != 0 || source == null || request.Quantity <= 0
                        || request.RequestedExpiryUnix <= nowUnix || quote.MaxActiveListings <= 0 || string.IsNullOrWhiteSpace(quote.QuotaKey)
                        || (!request.OpeningBidGold.HasValue && !request.BuyoutGold.HasValue)
                        || request.OpeningBidGold <= 0 || request.BuyoutGold <= 0
                        || request.OpeningBidGold > request.BuyoutGold || (quote.FeeRatePercent != 3 && quote.FeeRatePercent != 5))
                        throw new ArgumentException("Invalid listing terms.");
                    var escrow = Transform(source, request.Quantity, false);
                    after = new(request.OrderId, 1, AuctionOrderState.Active, request.Actor,
                        codec.Serialize(source), codec.Serialize(escrow), request.Quantity, request.Quantity,
                        request.OpeningBidGold, request.BuyoutGold, null, 0, quote.DepositDebitGold,
                        nowUnix, request.RequestedExpiryUnix, null, quote.QuotaKey, quote.PolicyVersion,
                        quote.FeeRatePercent, 0, 0, quote.UnitPriceGold);
                    debit = checked(quote.RegistrationFeeGold + quote.DepositDebitGold);
                }
                else
                {
                    if (before == null || before.OrderId != request.OrderId || before.Version != request.ExpectedVersion
                        || before.State != AuctionOrderState.Active || before.PendingOperationId.HasValue
                        || before.RemainingQuantity <= 0 || before.FundedBidGold < 0 || before.FundedDepositGold < 0
                        || (before.HighestBidder != null) != (before.FundedBidGold > 0))
                        throw new ArgumentException("Order is not available at the expected version.");
                    if (request.Action == AuctionOperationAction.Expire ? nowUnix < before.ExpiresAtUnix : nowUnix >= before.ExpiresAtUnix)
                        throw new ArgumentException("Order deadline does not permit this action.");
                    if (quote.RegistrationFeeGold != 0 || quote.DepositDebitGold != 0)
                        throw new ArgumentException("Existing-order action cannot acquire another listing deposit.");
                    after = before with { Version = checked(before.Version + 1), PendingOperationId = null };
                    debit = 0;
                    switch (request.Action)
                    {
                        case AuctionOperationAction.Bid:
                            RequireBuyer(request.Actor, before.Seller);
                            if (!before.OpeningBidGold.HasValue || request.OfferedGold != quote.GrossGold || request.OfferedGold <= before.FundedBidGold
                                || (before.BuyoutGold.HasValue && request.OfferedGold >= before.BuyoutGold.Value)
                                || quote.SettlementFeeGold != 0 || quote.DepositRefundGold != 0 || quote.DepositForfeitGold != 0)
                                throw new ArgumentException("Invalid funded bid plan.");
                            debit = request.OfferedGold;
                            // Character/account identity, not a mutable display name, controls supplements.
                            if (SameParty(request.Actor, before.HighestBidder)) debit = checked(request.OfferedGold - before.FundedBidGold);
                            else GoldLeg(legs, "bid-refund", AuctionDeliveryKind.BidRefund, before.HighestBidder, before.FundedBidGold);
                            after = after with { HighestBidder = request.Actor, FundedBidGold = request.OfferedGold };
                            break;
                        case AuctionOperationAction.Buyout:
                        case AuctionOperationAction.BuyPiece:
                        case AuctionOperationAction.Expire when before.HighestBidder != null:
                            bool expiry = request.Action == AuctionOperationAction.Expire;
                            var buyer = expiry ? before.HighestBidder : request.Actor;
                            RequireBuyer(buyer, before.Seller);
                            int quantity = request.Action == AuctionOperationAction.BuyPiece ? request.Quantity : before.RemainingQuantity;
                            if (quantity <= 0 || quantity > before.RemainingQuantity || quote.GrossGold <= 0
                                || quote.SettlementFeeGold > quote.GrossGold) throw new ArgumentException("Invalid fill quantity or proceeds.");
                            if (request.Action == AuctionOperationAction.Buyout && (before.BuyoutGold != quote.GrossGold || request.OfferedGold != quote.GrossGold))
                                throw new ArgumentException("Buyout does not match the frozen total.");
                            if (request.Action == AuctionOperationAction.BuyPiece && (before.OpeningBidGold.HasValue || before.HighestBidder != null
                                || !before.UnitPriceGold.HasValue || checked(before.UnitPriceGold.Value * quantity) != quote.GrossGold || request.OfferedGold != quote.GrossGold))
                                throw new ArgumentException("Piece purchase requires a consistent fixed unit price without bids.");
                            if (expiry && quote.GrossGold != before.FundedBidGold) throw new ArgumentException("Expiry must consume the funded bid exactly.");
                            debit = expiry ? 0 : SameParty(buyer, before.HighestBidder) ? checked(quote.GrossGold - before.FundedBidGold) : quote.GrossGold;
                            if (debit < 0) throw new ArgumentException("Funded bid exceeds purchase gross.");
                            if (!expiry && !SameParty(buyer, before.HighestBidder))
                                GoldLeg(legs, "bid-refund", AuctionDeliveryKind.BidRefund, before.HighestBidder, before.FundedBidGold);
                            var delivered = Transform(codec.DeserializeStrict(before.EscrowEnvelopeJson), quantity, false);
                            legs.Add(new("item-delivery", AuctionDeliveryKind.ItemDelivery, buyer, 0, codec.Serialize(delivered), quantity));
                            GoldLeg(legs, "seller-proceeds", AuctionDeliveryKind.SellerProceeds, before.Seller, checked(quote.GrossGold - quote.SettlementFeeGold));
                            GoldLeg(legs, "deposit-refund", AuctionDeliveryKind.DepositRefund, before.Seller, quote.DepositRefundGold);
                            int remaining = before.RemainingQuantity - quantity;
                            long deposit = checked(before.FundedDepositGold - quote.DepositRefundGold - quote.DepositForfeitGold);
                            if (deposit < 0 || (remaining == 0 && deposit != 0)) throw new ArgumentException("Unbalanced closing deposit.");
                            after = after with { State = remaining == 0 ? AuctionOrderState.Sold : AuctionOrderState.Active,
                                RemainingQuantity = remaining, HighestBidder = null, FundedBidGold = 0, FundedDepositGold = deposit,
                                CumulativeGrossGold = checked(before.CumulativeGrossGold + quote.GrossGold),
                                CumulativeFeeGold = checked(before.CumulativeFeeGold + quote.SettlementFeeGold),
                                EscrowEnvelopeJson = remaining == 0 ? before.EscrowEnvelopeJson : codec.Serialize(Transform(codec.DeserializeStrict(before.EscrowEnvelopeJson), remaining, true)),
                                BuyoutGold = remaining == 0 ? before.BuyoutGold : checked(before.UnitPriceGold.Value * remaining) };
                            break;
                        case AuctionOperationAction.Cancel:
                        case AuctionOperationAction.Expire:
                            if (!SameParty(request.Actor, before.Seller) || before.HighestBidder != null || quote.GrossGold != 0 || quote.SettlementFeeGold != 0
                                || checked(quote.DepositRefundGold + quote.DepositForfeitGold) != before.FundedDepositGold)
                                throw new ArgumentException("Invalid cancellation or unsold expiry.");
                            var returned = Transform(codec.DeserializeStrict(before.EscrowEnvelopeJson), before.RemainingQuantity, true);
                            legs.Add(new("item-return", AuctionDeliveryKind.ItemReturn, before.Seller, 0, codec.Serialize(returned), before.RemainingQuantity));
                            GoldLeg(legs, "deposit-refund", AuctionDeliveryKind.DepositRefund, before.Seller, quote.DepositRefundGold);
                            after = after with { State = request.Action == AuctionOperationAction.Cancel ? AuctionOrderState.Cancelled : AuctionOrderState.ExpiredUnsold,
                                RemainingQuantity = 0, FundedDepositGold = 0, HighestBidder = null, FundedBidGold = 0 };
                            break;
                        default: throw new ArgumentException("Unsupported auction action.");
                    }
                }
                Cash(debit);
                foreach (var leg in legs) Cash(leg.Gold);
                return new(AuctionMarketStatus.Planned, "", new(new(before, after, quote, legs), debit));
            }
            catch (AuctionMarketUnavailableException ex) { return new(AuctionMarketStatus.Unavailable, ex.Message, null); }
            catch (AuctionItemCodecException ex) { return new(AuctionMarketStatus.Unavailable, ex.Error.ToString(), null); }
            catch (Exception ex) when (ex is ArgumentException || ex is OverflowException)
            { return new(AuctionMarketStatus.Rejected, ex.Message, null); }
        }

        private AuctionItemEnvelope Transform(AuctionItemEnvelope source, int quantity, bool returning)
        {
            var original = codec.DecodeStrict(source);
            if (quantity <= 0 || (InventoryStackRuleService.IsStackable(original) ? quantity > original.Count : quantity != 1))
                throw new ArgumentException("Invalid escrow quantity.");
            var result = itemTransform(source, quantity, returning) ?? throw new AuctionMarketUnavailableException("Item transform unavailable.");
            var core = codec.DecodeStrict(result);
            if (core.ItemId != original.ItemId || core.ItemKind != original.ItemKind
                || (InventoryStackRuleService.IsStackable(core) ? core.Count != quantity : quantity != 1))
                throw new ArgumentException("Item transform changed template/kind/quantity.");
            // Independently ensure the transform preserved opaque instance state.
            var verified = codec.CreateTransferSnapshot(source, core);
            if (verified.ContentHashHex != result.ContentHashHex) throw new ArgumentException("Item transform extension mismatch.");
            return result;
        }
        private static void ValidateRequest(AuctionMarketRequest r)
        {
            if (r == null || r.OperationId == Guid.Empty || r.OrderId <= 0 || r.ExpectedVersion < 0
                || r.Actor == null || r.Actor.CharacterId <= 0 || r.Actor.AccountId <= 0 || !Enum.IsDefined(r.Action))
                throw new ArgumentException("Invalid auction request identity.");
        }
        private static bool SameParty(AuctionParty a, AuctionParty b) => a != null && b != null && a.CharacterId == b.CharacterId && a.AccountId == b.AccountId;
        private static void RequireBuyer(AuctionParty buyer, AuctionParty seller)
        { if (buyer == null || seller == null || buyer.AccountId == seller.AccountId) throw new ArgumentException("Seller account cannot buy or bid."); }
        private static void Nonnegative(params long[] values) { foreach (long value in values) if (value < 0) throw new ArgumentException("Negative economic quote."); }
        private static void Cash(long value) { if (value < 0 || value > int.MaxValue) throw new ArgumentException("Amount exceeds one public asset leg."); }
        private static void GoldLeg(List<AuctionDeliveryLeg> legs, string key, AuctionDeliveryKind kind, AuctionParty recipient, long gold)
        { if (gold > 0) { if (recipient == null) throw new ArgumentException("Funded leg is missing its owner."); legs.Add(new(key, kind, recipient, gold, null, 0)); } }
    }
}
