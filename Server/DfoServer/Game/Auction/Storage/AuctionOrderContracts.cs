using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DfoServer.Game.Auction.Storage
{
    internal sealed record AuctionParty(int CharacterId, int AccountId, string FrozenName);
    internal enum AuctionOrderState { Funding, Active, Sold, Cancelled, ExpiredUnsold }

    // Domain IDs are private sequences, never an interpretation of the native 8-byte identity.
    // Source describes the complete pre-debit slot; Escrow describes only the listed remainder.
    internal sealed record AuctionOrderSnapshot(
        long OrderId, long Version, AuctionOrderState State, AuctionParty Seller,
        string SourceEnvelopeJson, string EscrowEnvelopeJson,
        int InitialQuantity, int RemainingQuantity, long? OpeningBidGold, long? BuyoutGold,
        AuctionParty HighestBidder, long FundedBidGold, long FundedDepositGold,
        long CreatedAtUnix, long ExpiresAtUnix, Guid? PendingOperationId,
        string QuotaKey, string PolicyVersion, int FeeRatePercent,
        long CumulativeGrossGold, long CumulativeFeeGold, long? UnitPriceGold);

    internal enum AuctionQuoteAvailability { Unavailable, FixtureOnly, VerifiedA21, ApprovedProductPolicy }
    internal sealed record AuctionQuoteBinding(AuctionOperationAction Action, long OrderId,
        long ExpectedVersion, int ActorCharacterId, int ActorAccountId, int Quantity,
        long OfferedGold, long RequestedExpiryUnix, string RequestFingerprint);
    internal sealed record AuctionVerifiedQuote(
        AuctionQuoteAvailability Availability, string EvidenceFingerprint, long ValidUntilUnix,
        long GrossGold, long RegistrationFeeGold, long DepositDebitGold, long SettlementFeeGold,
        long DepositRefundGold, long DepositForfeitGold, string QuotaKey, int MaxActiveListings,
        AuctionQuoteBinding Binding, string PolicyVersion = "", int FeeRatePercent = 0,
        long? UnitPriceGold = null);
    internal enum AuctionDeliveryKind { ItemDelivery, ItemReturn, SellerProceeds, BidRefund, DepositRefund }
    internal sealed record AuctionDeliveryLeg(string LegKey, AuctionDeliveryKind Kind,
        AuctionParty Recipient, long Gold, string ItemEnvelopeJson, int Quantity);

    internal sealed class AuctionOrderTransition
    {
        public AuctionOrderSnapshot Before { get; }
        public AuctionOrderSnapshot After { get; }
        public AuctionVerifiedQuote Quote { get; }
        public IReadOnlyList<AuctionDeliveryLeg> Legs { get; }

        [System.Text.Json.Serialization.JsonConstructor]
        public AuctionOrderTransition(AuctionOrderSnapshot before, AuctionOrderSnapshot after,
            AuctionVerifiedQuote quote, IReadOnlyList<AuctionDeliveryLeg> legs)
        {
            Before = before;
            After = after ?? throw new ArgumentNullException(nameof(after));
            Quote = quote ?? throw new ArgumentNullException(nameof(quote));
            if (legs == null || legs.Any(l => l == null)) throw new ArgumentException("Missing delivery legs.");
            Legs = Array.AsReadOnly(legs.ToArray());
        }

        internal string Serialize() => JsonSerializer.Serialize(this);
        internal static AuctionOrderTransition DeserializeStrict(string json)
        {
            var result = JsonSerializer.Deserialize<AuctionOrderTransition>(json)
                ?? throw new ArgumentException("Missing auction transition.");
            if (result.Serialize() != json) throw new ArgumentException("Noncanonical auction transition.");
            return result;
        }
    }

    internal sealed record AuctionOrderPreparation(bool Created, AuctionJournalEntry Operation);
}
