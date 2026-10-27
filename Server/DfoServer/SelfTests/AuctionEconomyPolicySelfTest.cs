using System;
using DfoServer.Game.Auction.Rules;

namespace DfoServer.SelfTests
{
    public static class AuctionEconomyPolicySelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            { Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] auction economy: {name}"); if (condition) passed++; else failed++; }
            void Reject(string name, Action action)
            {
                try { action(); Check(name, false); }
                catch (ArgumentException) { Check(name, true); }
                catch (InvalidOperationException) { Check(name, true); }
                catch (OverflowException) { Check(name, true); }
            }
            try
            {
                AuctionEconomyPolicy.RequirePolicyVersion("user-approved-20261023-v1");
                Reject("other policy versions never silently inherit this implementation", () => AuctionEconomyPolicy.RequirePolicyVersion("future-v2"));
                Reject("missing policy version rejected", () => AuctionEconomyPolicy.RequirePolicyVersion(null));
                var ordinary = new AuctionEconomyListingInput(11, 111, 4, 1000, null, 24, 10, null, 330, true);
                var normal = AuctionEconomyPolicy.QuoteListing(ordinary);
                Check("production policy is explicitly approved and locks ordinary fee/duration", normal.PolicyVersion == "user-approved-20261023-v1"
                    && normal.LockedFeePercent == 5 && normal.MaximumAccountListings == 5
                    && normal.ExpiresAtUnix == 87400 && normal.DepositDebitGold == 10000 && normal.FixedUnitPriceGold == 33);
                Reject("reserved account slot counts towards ordinary cap", () => AuctionEconomyPolicy.QuoteListing(ordinary with { ActiveAndReservedAccountListings = 5 }));
                Reject("ordinary account cannot select 48h", () => AuctionEconomyPolicy.QuoteListing(ordinary with { DurationHours = 48 }));
                Reject("arbitrary shorter duration is not silently accepted", () => AuctionEconomyPolicy.QuoteListing(ordinary with { DurationHours = 12 }));
                var premiumInput = ordinary with { PremiumExpiresAtUnix = 1001, ActiveAndReservedAccountListings = 29, DurationHours = 48 };
                var premium = AuctionEconomyPolicy.QuoteListing(premiumInput);
                Check("one second premium locks 3 percent and full 48h", premium.LockedFeePercent == 3
                    && premium.MaximumAccountListings == 30 && premium.ExpiresAtUnix == 173800 && premium.ObservedPremiumExpiresAtUnix == 1001
                    && premium.DepositDebitGold == 10000);
                Reject("premium account cannot reserve slot 31", () => AuctionEconomyPolicy.QuoteListing(premiumInput with { ActiveAndReservedAccountListings = 30 }));
                Reject("premium expires exactly at quote time", () => AuctionEconomyPolicy.QuoteListing(premiumInput with { PremiumExpiresAtUnix = 1000 }));
                var newAfterExpiry = AuctionEconomyPolicy.QuoteListing(ordinary with { NowUnix = 1001, PremiumExpiresAtUnix = 1001 });
                Check("old premium terms survive while a new order returns to normal", premium.LockedFeePercent == 3
                    && premium.ExpiresAtUnix == 173800 && newAfterExpiry.LockedFeePercent == 5);
                Reject("missing both prices is rejected", () => AuctionEconomyPolicy.QuoteListing(ordinary with { BuyoutGold = null }));
                Reject("bid above buyout is rejected", () => AuctionEconomyPolicy.QuoteListing(ordinary with { OpeningBidGold = 331 }));
                Reject("negative wire sentinel is not a domain price", () => AuctionEconomyPolicy.QuoteListing(ordinary with { OpeningBidGold = -1 }));
                Reject("unrepresentable wire total is rejected", () => AuctionEconomyPolicy.QuoteListing(ordinary with { BuyoutGold = (long)int.MaxValue + 1 }));
                Reject("deadline overflow is rejected", () => AuctionEconomyPolicy.QuoteListing(ordinary with { NowUnix = long.MaxValue }));
                Check("indivisible total does not invent a piece unit", AuctionEconomyPolicy.QuoteListing(ordinary with { BuyoutGold = 331 }).FixedUnitPriceGold == null);
                Check("bid listing never gains piece mode", AuctionEconomyPolicy.QuoteListing(ordinary with { OpeningBidGold = 100 }).FixedUnitPriceGold == null);
                Check("native eligibility is required for piece mode", AuctionEconomyPolicy.QuoteListing(ordinary with { NativePieceEligible = false }).FixedUnitPriceGold == null);

                var first = AuctionEconomyPolicy.QuoteFill(new(5, 0, 0, 19, 10000, false));
                var last = AuctionEconomyPolicy.QuoteFill(new(5, first.CumulativeGrossAfter, first.ChargedFeeAfter, 1, first.RemainingDepositGold, true));
                Check("split fills pay cumulative floor difference rather than per-fill floor", first.SettlementFeeGold == 0 && last.SettlementFeeGold == 1
                    && last.CumulativeGrossAfter == 20 && last.ChargedFeeAfter == 1);
                Check("sale legs conserve gross plus one deposit", first.SellerCreditGold + last.SellerCreditGold + last.ChargedFeeAfter == 20 + 10000
                    && first.DepositRefundGold == 0 && last.DepositRefundGold == 10000 && last.RemainingDepositGold == 0);
                var lockedAfterExpiry = AuctionEconomyPolicy.QuoteFill(new(premium.LockedFeePercent, 0, 0, 100, 10000, true));
                Check("sale uses locked premium rate with no current entitlement resampling", lockedAfterExpiry.SettlementFeeGold == 3 && lockedAfterExpiry.SellerCreditGold == 10097);
                Reject("persisted cumulative fee mismatch is rejected", () => AuctionEconomyPolicy.QuoteFill(new(5, 20, 0, 1, 10000, false)));
                Check("explicitly managed locked zero rate settles with zero fee", AuctionEconomyPolicy.QuoteFill(new(0, 0, 0, 100, 10000, true)).SettlementFeeGold == 0);
                Check("explicitly managed locked four percent survives settlement", AuctionEconomyPolicy.QuoteFill(new(4, 0, 0, 100, 10000, true)).SettlementFeeGold == 4);
                var fullRate = AuctionEconomyPolicy.QuoteFill(new(100, 0, 0, 100, 0, true));
                Check("managed rate upper boundary charges gross without negative seller proceeds", fullRate.SettlementFeeGold == 100 && fullRate.SellerCreditGold == 0);
                Reject("negative locked rate rejected", () => AuctionEconomyPolicy.QuoteFill(new(-1, 0, 0, 100, 0, true)));
                Reject("locked rate above one hundred rejected", () => AuctionEconomyPolicy.QuoteFill(new(101, 0, 0, 100, 0, true)));
                Reject("cumulative gross overflow is rejected", () => AuctionEconomyPolicy.QuoteFill(new(5, long.MaxValue, AuctionEconomyPolicy.CumulativeFee(long.MaxValue, 5), 1, 0, true)));
                Check("large cumulative tax avoids intermediate multiply overflow", AuctionEconomyPolicy.CumulativeFee(long.MaxValue, 5) == 461168601842738790);
                long cumulative = 0, fees = 0, proceeds = 0, remainingDeposit = 10000;
                var fills = new long[] { 1, 33, 1, 9, 200, 7, 999, 10001 };
                for (int index = 0; index < fills.Length; index++)
                {
                    var step = AuctionEconomyPolicy.QuoteFill(new(3, cumulative, fees, fills[index], remainingDeposit, index == fills.Length - 1));
                    cumulative = step.CumulativeGrossAfter; fees = step.ChargedFeeAfter;
                    proceeds += step.SellerCreditGold; remainingDeposit = step.RemainingDepositGold;
                }
                Check("multi-fill partition matches one sale and conserves deposit", fees == 337 && cumulative == 11251
                    && fees + proceeds == cumulative + 10000 && remainingDeposit == 0);

                var expired = new AuctionEconomyCloseInput(AuctionEconomyCloseKind.Expire, false, 10000, 200, 200);
                var close = AuctionEconomyPolicy.QuoteClose(expired);
                Check("expiry boundary refunds remaining deposit exactly once in transition", close.DepositRefundGold == 10000 && close.DepositForfeitGold == 0 && close.RemainingDepositGold == 0
                    && AuctionEconomyPolicy.QuoteClose(expired with { RemainingDepositGold = close.RemainingDepositGold }).DepositRefundGold == 0);
                Reject("early expiry rejected", () => AuctionEconomyPolicy.QuoteClose(expired with { NowUnix = 199 }));
                var cancelled = AuctionEconomyPolicy.QuoteClose(expired with { Kind = AuctionEconomyCloseKind.Cancel, NowUnix = 100 });
                Check("active withdrawal forfeits all deposit", cancelled.DepositRefundGold == 0 && cancelled.DepositForfeitGold == 10000 && cancelled.RemainingDepositGold == 0);
                Reject("bid locks cancellation", () => AuctionEconomyPolicy.QuoteClose(expired with { Kind = AuctionEconomyCloseKind.Cancel, HasFundedBid = true }));
                Reject("funded expiry cannot masquerade as unsold return", () => AuctionEconomyPolicy.QuoteClose(expired with { HasFundedBid = true }));
                Check("managed one-gold deposit is refunded exactly", AuctionEconomyPolicy.QuoteClose(expired with { RemainingDepositGold = 1 }).DepositRefundGold == 1);
                Check("managed deposit upper boundary is refunded exactly", AuctionEconomyPolicy.QuoteClose(expired with { RemainingDepositGold = int.MaxValue }).DepositRefundGold == int.MaxValue);
                Reject("negative funded deposit rejected", () => AuctionEconomyPolicy.QuoteClose(expired with { RemainingDepositGold = -1 }));
                Reject("funded deposit above public gold range rejected", () => AuctionEconomyPolicy.QuoteClose(expired with { RemainingDepositGold = (long)int.MaxValue + 1 }));

                var bid = new AuctionEconomyBidInput(11, 22, 222, 33, 333, 100, 100, 105, 200);
                var next = AuctionEconomyPolicy.QuoteBid(bid);
                Check("new bidder funds full new bid and prior bidder gets full refund", next.BidderDebitGold == 105 && next.PreviousBidderRefundGold == 100
                    && next.FundedBidAfterGold == 105 && !next.ExtendDeadline);
                var selfRaise = AuctionEconomyPolicy.QuoteBid(bid with { BidderAccountId = 33, BidderCharacterId = 333 });
                Check("same character raises only the funded difference", selfRaise.BidderDebitGold == 5 && selfRaise.PreviousBidderRefundGold == 0);
                Reject("same-price bid is never allowed by small float truncation", () => AuctionEconomyPolicy.QuoteBid(bid with { CurrentPriceGold = 1, FundedBidGold = 1, RequestedGold = 1 }));
                Check("strict minimum fixes native small-price truncation", AuctionEconomyPolicy.QuoteBid(bid with { CurrentPriceGold = 1, FundedBidGold = 1, RequestedGold = 2 }).MinimumBidGold == 2);
                Reject("bid below native 5 percent threshold rejected", () => AuctionEconomyPolicy.QuoteBid(bid with { RequestedGold = 104 }));
                Reject("same seller account alternate character rejected", () => AuctionEconomyPolicy.QuoteBid(bid with { BidderAccountId = 11 }));
                Reject("funding identity mismatch rejected", () => AuctionEconomyPolicy.QuoteBid(bid with { HighestBidderCharacterId = null }));
                Reject("funded amount must match current bid", () => AuctionEconomyPolicy.QuoteBid(bid with { FundedBidGold = 99 }));
                Reject("buyout equality cannot be incorrectly planned as another bid", () => AuctionEconomyPolicy.QuoteBid(bid with { RequestedGold = 200 }));
                var ownBuyout = AuctionEconomyPolicy.QuoteBuyout(11, 33, 333, 33, 333, 100, 101);
                Check("highest bidder buyout pays delta and bypasses bid increment", ownBuyout.BidderDebitGold == 1 && ownBuyout.PreviousBidderRefundGold == 0);
                var otherBuyout = AuctionEconomyPolicy.QuoteBuyout(11, 22, 222, 33, 333, 100, 101);
                Check("another buyer buyout refunds escrow owner", otherBuyout.BidderDebitGold == 101 && otherBuyout.PreviousBidderRefundGold == 100);
                Reject("same account cannot buy out own listing", () => AuctionEconomyPolicy.QuoteBuyout(11, 11, 112, null, null, 0, 101));
                Check("native exact threshold samples", AuctionEconomyPolicy.NativeBidThreshold(1) == 1 && AuctionEconomyPolicy.NativeBidThreshold(100) == 105);
                Reject("native signed threshold overflow rejected", () => AuctionEconomyPolicy.NativeBidThreshold(int.MaxValue));

                var piece = new AuctionEconomyPieceInput(11, 22, true, false, false, 33, 10, 3, 99);
                var bought = AuctionEconomyPolicy.QuotePiece(piece);
                var boughtAgain = AuctionEconomyPolicy.QuotePiece(piece with { RemainingQuantity = bought.RemainingQuantity, RequestedQuantity = 7, RequestedGold = 231 });
                Check("piece purchase keeps frozen unit price across remainder", bought.GrossGold + boughtAgain.GrossGold == 330 && boughtAgain.RemainingQuantity == 0);
                Reject("forged piece total rejected", () => AuctionEconomyPolicy.QuotePiece(piece with { RequestedGold = 98 }));
                Reject("piece cannot consume more than escrow", () => AuctionEconomyPolicy.QuotePiece(piece with { RequestedQuantity = 11, RequestedGold = 363 }));
                Reject("piece request obeys native quantity cap", () => AuctionEconomyPolicy.QuotePiece(piece with { RemainingQuantity = 10001, RequestedQuantity = 10001, RequestedGold = 330033 }));
                Reject("opening bid disallows piece mode even without bidder", () => AuctionEconomyPolicy.QuotePiece(piece with { HasOpeningBid = true }));
                Reject("funded bidder disallows piece mode", () => AuctionEconomyPolicy.QuotePiece(piece with { HasFundedBid = true }));
                Reject("same account cannot piece-buy own listing", () => AuctionEconomyPolicy.QuotePiece(piece with { BuyerAccountId = 11 }));
            }
            catch (Exception exception)
            { Console.WriteLine("[FAIL] unexpected auction economy exception: " + exception); failed++; }
            Console.WriteLine($"Auction economy: {passed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
