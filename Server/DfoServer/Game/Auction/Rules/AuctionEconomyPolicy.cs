using System;

namespace DfoServer.Game.Auction.Rules
{
    // Product policy approved on 2026-10-23. Percent displays, duration selectors,
    // deposit display and native bid precheck have client evidence; fee accrual,
    // expiry/refund and entitlement locking below are explicit product decisions.
    // Pure quotes never reserve an order, mutate a wallet or prove idempotency.
    public static class AuctionEconomyPolicy
    {
        public const string PolicyVersion = "user-approved-20261023-v1";
        public const long DepositGold = 10000;
        public const int OrdinaryListingLimit = 5;
        public const int PremiumListingLimit = 30;
        public const int MaximumPieceRequestQuantity = 10000;

        // Adapters must check the persisted order version before quoting it.
        // Existing orders keep their locked terms; a later policy requires its
        // own implementation rather than silently applying this one.
        public static void RequirePolicyVersion(string policyVersion)
        {
            if (!string.Equals(policyVersion, PolicyVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("Auction order uses an unsupported economic policy version.");
        }

        public static AuctionEconomyListingQuote QuoteListing(AuctionEconomyListingInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            Positive(input.SellerAccountId, nameof(input.SellerAccountId));
            Positive(input.SellerCharacterId, nameof(input.SellerCharacterId));
            Nonnegative(input.NowUnix, nameof(input.NowUnix));
            if (input.PremiumExpiresAtUnix < 0) throw new ArgumentOutOfRangeException(nameof(input.PremiumExpiresAtUnix));
            if (input.ActiveAndReservedAccountListings < 0) throw new ArgumentOutOfRangeException(nameof(input.ActiveAndReservedAccountListings));
            Positive(input.Quantity, nameof(input.Quantity));
            Price(input.OpeningBidGold, nameof(input.OpeningBidGold));
            Price(input.BuyoutGold, nameof(input.BuyoutGold));
            if (!input.OpeningBidGold.HasValue && !input.BuyoutGold.HasValue)
                throw new ArgumentException("At least one auction price is required.");
            if (input.OpeningBidGold.HasValue && input.BuyoutGold.HasValue
                && input.OpeningBidGold.Value > input.BuyoutGold.Value)
                throw new ArgumentException("Opening bid cannot exceed buyout.");
            var premium = input.PremiumExpiresAtUnix.HasValue && input.PremiumExpiresAtUnix.Value > input.NowUnix;
            var limit = premium ? PremiumListingLimit : OrdinaryListingLimit;
            if (input.ActiveAndReservedAccountListings >= limit)
                throw new InvalidOperationException("Account listing quota is exhausted.");
            if (input.DurationHours != 24 && input.DurationHours != 48)
                throw new ArgumentOutOfRangeException(nameof(input.DurationHours));
            if (input.DurationHours == 48 && !premium)
                throw new InvalidOperationException("48 hours requires active auction premium at listing.");
            long? fixedUnit = input.NativePieceEligible && !input.OpeningBidGold.HasValue
                && input.BuyoutGold.HasValue && input.BuyoutGold.Value % input.Quantity == 0
                ? input.BuyoutGold.Value / input.Quantity : null;
            return new AuctionEconomyListingQuote(PolicyVersion, premium ? 3 : 5, limit,
                input.NowUnix, checked(input.NowUnix + input.DurationHours * 3600L),
                input.PremiumExpiresAtUnix, DepositGold, fixedUnit);
        }

        public static AuctionEconomyBidQuote QuoteBid(AuctionEconomyBidInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateParties(input.SellerAccountId, input.BidderAccountId, input.BidderCharacterId,
                input.HighestBidderAccountId, input.HighestBidderCharacterId, input.FundedBidGold);
            WirePrice(input.CurrentPriceGold, nameof(input.CurrentPriceGold));
            WirePrice(input.RequestedGold, nameof(input.RequestedGold));
            Price(input.BuyoutGold, nameof(input.BuyoutGold));
            if (input.FundedBidGold > 0 && input.FundedBidGold != input.CurrentPriceGold)
                throw new ArgumentException("Funded bid must equal the current price.");
            if (input.BuyoutGold.HasValue && input.RequestedGold >= input.BuyoutGold.Value)
                throw new InvalidOperationException("Buyout equality must use the buyout plan; bids cannot exceed buyout.");
            var minimum = Math.Max(checked(input.CurrentPriceGold + 1), NativeBidThreshold(input.CurrentPriceGold));
            if (input.RequestedGold < minimum)
                throw new InvalidOperationException("Bid must strictly exceed current price and meet the native threshold.");
            return Funding(input.BidderCharacterId, input.HighestBidderCharacterId,
                input.FundedBidGold, input.RequestedGold, minimum);
        }

        public static AuctionEconomyBidQuote QuoteBuyout(int sellerAccountId, int buyerAccountId,
            int buyerCharacterId, int? highestBidderAccountId, int? highestBidderCharacterId,
            long fundedBidGold, long buyoutGold)
        {
            ValidateParties(sellerAccountId, buyerAccountId, buyerCharacterId,
                highestBidderAccountId, highestBidderCharacterId, fundedBidGold);
            WirePrice(buyoutGold, nameof(buyoutGold));
            if (fundedBidGold > buyoutGold) throw new ArgumentException("Funded bid exceeds buyout.");
            return Funding(buyerCharacterId, highestBidderCharacterId, fundedBidGold, buyoutGold, buyoutGold);
        }

        public static AuctionEconomyPieceQuote QuotePiece(AuctionEconomyPieceInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            Positive(input.SellerAccountId, nameof(input.SellerAccountId));
            Positive(input.BuyerAccountId, nameof(input.BuyerAccountId));
            if (input.SellerAccountId == input.BuyerAccountId)
                throw new InvalidOperationException("An account cannot buy its own auction.");
            if (input.HasOpeningBid || input.HasFundedBid || !input.NativePieceEligible)
                throw new InvalidOperationException("Piece purchase requires a native eligible fixed-price listing without bids.");
            WirePrice(input.FixedUnitPriceGold, nameof(input.FixedUnitPriceGold));
            Positive(input.RemainingQuantity, nameof(input.RemainingQuantity));
            if (input.RequestedQuantity <= 0 || input.RequestedQuantity > input.RemainingQuantity
                || input.RequestedQuantity > MaximumPieceRequestQuantity)
                throw new ArgumentOutOfRangeException(nameof(input.RequestedQuantity));
            var total = checked(input.FixedUnitPriceGold * input.RequestedQuantity);
            WirePrice(total, nameof(input.RequestedGold));
            if (input.RequestedGold != total) throw new InvalidOperationException("Piece purchase amount does not match the frozen unit price.");
            return new AuctionEconomyPieceQuote(PolicyVersion, total,
                input.RemainingQuantity - input.RequestedQuantity);
        }

        public static AuctionEconomySettlementQuote QuoteFill(AuctionEconomyFillInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateRate(input.LockedFeePercent);
            Nonnegative(input.CumulativeGrossBefore, nameof(input.CumulativeGrossBefore));
            Positive(input.GrossGold, nameof(input.GrossGold));
            ValidateDeposit(input.RemainingDepositGold);
            var expectedBefore = CumulativeFee(input.CumulativeGrossBefore, input.LockedFeePercent);
            if (input.ChargedFeeBefore != expectedBefore)
                throw new ArgumentException("Persisted cumulative fee is inconsistent with locked rate and gross.");
            var grossAfter = checked(input.CumulativeGrossBefore + input.GrossGold);
            var feeAfter = CumulativeFee(grossAfter, input.LockedFeePercent);
            var feeDelta = feeAfter - expectedBefore;
            var refund = input.IsFullySold ? input.RemainingDepositGold : 0;
            return new AuctionEconomySettlementQuote(PolicyVersion, grossAfter, feeAfter, feeDelta,
                refund, 0, input.IsFullySold ? 0 : input.RemainingDepositGold,
                checked(input.GrossGold - feeDelta + refund));
        }

        public static AuctionEconomyCloseQuote QuoteClose(AuctionEconomyCloseInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateDeposit(input.RemainingDepositGold);
            Nonnegative(input.NowUnix, nameof(input.NowUnix));
            Nonnegative(input.ExpiresAtUnix, nameof(input.ExpiresAtUnix));
            if (!Enum.IsDefined(input.Kind)) throw new ArgumentOutOfRangeException(nameof(input.Kind));
            if (input.Kind == AuctionEconomyCloseKind.Cancel && input.HasFundedBid)
                throw new InvalidOperationException("A listing with a funded bid cannot be cancelled.");
            if (input.Kind == AuctionEconomyCloseKind.Expire && input.NowUnix < input.ExpiresAtUnix)
                throw new InvalidOperationException("Listing has not expired.");
            // Expiry with a bidder is a sale; its funded money and fee must use
            // QuoteFill, never an unsold return. All expiry deposits are refundable.
            if (input.Kind == AuctionEconomyCloseKind.Expire && input.HasFundedBid)
                throw new InvalidOperationException("Funded expiry requires the sale settlement plan.");
            return new AuctionEconomyCloseQuote(PolicyVersion,
                input.Kind == AuctionEconomyCloseKind.Expire ? input.RemainingDepositGold : 0,
                input.Kind == AuctionEconomyCloseKind.Cancel ? input.RemainingDepositGold : 0, 0);
        }

        // Exact native SSE constant is 0x3D4CCCCC, one ULP below float32 0.05.
        // Separate conversions force both native arithmetic rounding steps.
        public static long NativeBidThreshold(long currentPriceGold)
        {
            Positive(currentPriceGold, nameof(currentPriceGold));
            if (currentPriceGold > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(currentPriceGold));
            var current = (float)currentPriceGold;
            var rate = BitConverter.Int32BitsToSingle(0x3D4CCCCC);
            var increase = (float)(current * rate);
            var threshold = (float)(increase + current);
            // cvttss2si returns INT_MIN for overflow; reject rather than wrap.
            if (threshold >= 2147483648f) throw new InvalidOperationException("Native bid threshold exceeds signed wire range.");
            return (int)threshold;
        }

        public static long CumulativeFee(long grossGold, int lockedFeePercent)
        {
            Nonnegative(grossGold, nameof(grossGold));
            ValidateRate(lockedFeePercent);
            // floor(gross * rate / 100), without overflowing the intermediate.
            return checked(grossGold / 100 * lockedFeePercent + grossGold % 100 * lockedFeePercent / 100);
        }

        private static AuctionEconomyBidQuote Funding(int bidderCharacterId, int? highestBidderCharacterId,
            long fundedBidGold, long requestedGold, long minimum)
        {
            var same = highestBidderCharacterId == bidderCharacterId;
            return new AuctionEconomyBidQuote(PolicyVersion, minimum,
                same ? requestedGold - fundedBidGold : requestedGold,
                same ? 0 : fundedBidGold, requestedGold, false);
        }

        private static void ValidateParties(int sellerAccountId, int bidderAccountId, int bidderCharacterId,
            int? highestAccountId, int? highestCharacterId, long fundedGold)
        {
            Positive(sellerAccountId, nameof(sellerAccountId));
            Positive(bidderAccountId, nameof(bidderAccountId));
            Positive(bidderCharacterId, nameof(bidderCharacterId));
            Nonnegative(fundedGold, nameof(fundedGold));
            if (sellerAccountId == bidderAccountId) throw new InvalidOperationException("An account cannot bid on or buy its own auction.");
            if (highestAccountId.HasValue != highestCharacterId.HasValue
                || highestAccountId.HasValue != (fundedGold > 0))
                throw new ArgumentException("Highest bidder identity and funded money must agree.");
            if (highestAccountId.HasValue)
            {
                Positive(highestAccountId.Value, nameof(highestAccountId));
                Positive(highestCharacterId.Value, nameof(highestCharacterId));
                if (highestAccountId == sellerAccountId) throw new ArgumentException("Seller account cannot own the funded bid.");
                if (highestCharacterId == bidderCharacterId && highestAccountId != bidderAccountId)
                    throw new ArgumentException("A character cannot change account identity.");
            }
        }

        private static void ValidateRate(int rate)
        { if (rate < 0 || rate > 100) throw new ArgumentOutOfRangeException(nameof(rate), "Locked auction fee must be between 0% and 100%."); }
        private static void ValidateDeposit(long deposit)
        { if (deposit < 0 || deposit > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(deposit)); }
        private static void Price(long? price, string name)
        { if (price.HasValue) WirePrice(price.Value, name); }
        private static void WirePrice(long value, string name)
        { Positive(value, name); if (value > int.MaxValue) throw new ArgumentOutOfRangeException(name); }
        private static void Positive(long value, string name)
        { if (value <= 0) throw new ArgumentOutOfRangeException(name); }
        private static void Nonnegative(long value, string name)
        { if (value < 0) throw new ArgumentOutOfRangeException(name); }
    }

    public sealed record AuctionEconomyListingInput(int SellerAccountId, int SellerCharacterId,
        int ActiveAndReservedAccountListings, long NowUnix, long? PremiumExpiresAtUnix,
        int DurationHours, int Quantity, long? OpeningBidGold, long? BuyoutGold, bool NativePieceEligible);
    public sealed record AuctionEconomyListingQuote(string PolicyVersion, int LockedFeePercent,
        int MaximumAccountListings, long CreatedAtUnix, long ExpiresAtUnix,
        long? ObservedPremiumExpiresAtUnix, long DepositDebitGold, long? FixedUnitPriceGold);
    public sealed record AuctionEconomyBidInput(int SellerAccountId, int BidderAccountId,
        int BidderCharacterId, int? HighestBidderAccountId, int? HighestBidderCharacterId,
        long FundedBidGold, long CurrentPriceGold, long RequestedGold, long? BuyoutGold);
    public sealed record AuctionEconomyBidQuote(string PolicyVersion, long MinimumBidGold,
        long BidderDebitGold, long PreviousBidderRefundGold, long FundedBidAfterGold, bool ExtendDeadline);
    public sealed record AuctionEconomyPieceInput(int SellerAccountId, int BuyerAccountId,
        bool NativePieceEligible, bool HasOpeningBid, bool HasFundedBid, long FixedUnitPriceGold,
        int RemainingQuantity, int RequestedQuantity, long RequestedGold);
    public sealed record AuctionEconomyPieceQuote(string PolicyVersion, long GrossGold, int RemainingQuantity);
    public sealed record AuctionEconomyFillInput(int LockedFeePercent, long CumulativeGrossBefore,
        long ChargedFeeBefore, long GrossGold, long RemainingDepositGold, bool IsFullySold);
    public sealed record AuctionEconomySettlementQuote(string PolicyVersion, long CumulativeGrossAfter,
        long ChargedFeeAfter, long SettlementFeeGold, long DepositRefundGold,
        long DepositForfeitGold, long RemainingDepositGold, long SellerCreditGold);
    public enum AuctionEconomyCloseKind { Cancel, Expire }
    public sealed record AuctionEconomyCloseInput(AuctionEconomyCloseKind Kind, bool HasFundedBid,
        long RemainingDepositGold, long NowUnix, long ExpiresAtUnix);
    public sealed record AuctionEconomyCloseQuote(string PolicyVersion, long DepositRefundGold,
        long DepositForfeitGold, long RemainingDepositGold);
}
