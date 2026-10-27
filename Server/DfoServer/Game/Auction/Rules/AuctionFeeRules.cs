namespace DfoServer.Game.Auction.Rules
{
    public enum AuctionFeeRuleAvailability
    {
        Unavailable = 0
    }

    // PVF describes a discount but does not establish rates, bases or rounding.
    // Do not turn this evidence gap into a zero-fee quote or an executable default.
    public static class AuctionFeeRules
    {
        public static AuctionFeeRuleAvailability Availability => AuctionFeeRuleAvailability.Unavailable;
        public static string UnavailableReason => "A21 listing fees, settlement fees, bases and rounding require verified evidence.";
    }
}
