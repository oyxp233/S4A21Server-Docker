using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Auction;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class AuctionMarketSelfTest
    {
        public static int Run()
        {
            int pass = 0, fail = 0;
            void Check(string name, bool ok) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] auction market: {name}"); if (ok) pass++; else fail++; }
            const long timestamp = 1792700000;
            var seller = new AuctionParty(78201, 78201, "fixture seller");
            var buyer = new AuctionParty(78202, 78202, "fixture buyer");
            var third = new AuctionParty(78203, 78203, "fixture third");
            var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
            AuctionItemEnvelope Transform(AuctionItemEnvelope source, int quantity, bool returning)
            {
                var core = codec.DecodeStrict(source);
                if (InventoryStackRuleService.IsStackable(core)) core.Count = quantity;
                core.SortLockFlag = 0; core.EquipmentLockId = 0;
                return codec.CreateTransferSnapshot(source, core);
            }
            // Explicitly constructed test policy, never the production default.
            var approved = new AuctionApprovedQuoteProvider((r, b, n) => new(null, 0, true));
            AuctionVerifiedQuote Fixture(AuctionMarketRequest r, AuctionOrderSnapshot b, long n)
                => approved.Quote(r, b, n) with { Availability = AuctionQuoteAvailability.FixtureOnly };
            var planner = new AuctionMarketPlanner(codec, Transform, allowFixtureQuotes: true);
            AuctionMarketRequest Request(AuctionOperationAction action, long order, long version, AuctionParty actor,
                int qty = 1, long gold = 0, long expires = 0, long? opening = null, long? buyout = null)
                => new(Guid.NewGuid(), action, order, version, actor, qty, gold, expires, opening, buyout);
            AuctionMarketPlanningResult Plan(AuctionMarketRequest r, AuctionOrderSnapshot b, AuctionItemEnvelope source = null, long at = timestamp)
                => planner.Plan(r, b, source, Fixture(r, b, at), at);
            try
            {
                var inventory = new InventoryService(seller.CharacterId, seller.AccountId);
                inventory.SetItem(InventoryListType.Main, 65, new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3001, Count = 10 });
                var source = codec.Capture(inventory, InventoryListType.Main, 65);
                var list = Request(AuctionOperationAction.List, 1, 0, seller, 3, expires: timestamp + 86400, buyout: 300);
                var listed = Plan(list, null, source);
                Check("fixture listing plans only three out of a complete source stack of ten", listed.Status == AuctionMarketStatus.Planned
                    && listed.Plan.GoldDebit == 10000 && codec.DecodeStrict(codec.DeserializeStrict(listed.Plan.Transition.After.SourceEnvelopeJson)).Count == 10
                    && codec.DecodeStrict(codec.DeserializeStrict(listed.Plan.Transition.After.EscrowEnvelopeJson)).Count == 3);
                var order = listed.Plan.Transition.After;
                Check("production-default planner refuses test quote", new AuctionMarketPlanner(codec, Transform).Plan(list, null, source, Fixture(list, null, timestamp), timestamp).Status == AuctionMarketStatus.Unavailable);
                var unavailable = Fixture(list, null, timestamp) with { Availability = AuctionQuoteAvailability.Unavailable };
                Check("unavailable fee quote never becomes a zero-fee plan", planner.Plan(list, null, source, unavailable, timestamp).Status == AuctionMarketStatus.Unavailable);
                var expiredQuote = Fixture(list, null, timestamp) with { ValidUntilUnix = timestamp - 1 };
                Check("expired quote is rejected", planner.Plan(list, null, source, expiredQuote, timestamp).Status == AuctionMarketStatus.Rejected);

                var piece1 = Plan(Request(AuctionOperationAction.BuyPiece, 1, 1, buyer, 1, 100), order);
                Check("first piece accrues exact fee and keeps funded deposit", piece1.Plan.GoldDebit == 100
                    && piece1.Plan.Transition.After.RemainingQuantity == 2 && piece1.Plan.Transition.After.FundedDepositGold == 10000
                    && piece1.Plan.Transition.After.CumulativeFeeGold == 5 && piece1.Plan.Transition.Legs.Count == 2);
                var piece2 = Plan(Request(AuctionOperationAction.BuyPiece, 1, 2, third, 2, 200), piece1.Plan.Transition.After);
                Check("last pieces settle once and refund deposit separately", piece2.Plan.Transition.After.State == AuctionOrderState.Sold
                    && piece2.Plan.Transition.After.RemainingQuantity == 0 && piece2.Plan.Transition.After.FundedDepositGold == 0
                    && piece2.Plan.Transition.After.CumulativeGrossGold == 300 && piece2.Plan.Transition.After.CumulativeFeeGold == 15
                    && piece2.Plan.Transition.Legs.Single(l => l.Kind == AuctionDeliveryKind.DepositRefund).Gold == 10000);
                var cancel = Plan(Request(AuctionOperationAction.Cancel, 1, 1, seller), order);
                Check("cancel returns escrow three, never original stack ten, and forfeits deposit", cancel.Plan.Transition.After.State == AuctionOrderState.Cancelled
                    && cancel.Plan.Transition.Quote.DepositForfeitGold == 10000
                    && codec.DecodeStrict(codec.DeserializeStrict(cancel.Plan.Transition.Legs.Single().ItemEnvelopeJson)).Count == 3);
                var expire = Plan(Request(AuctionOperationAction.Expire, 1, 1, seller), order, at: timestamp + 86400);
                Check("unsold expiry returns item and deposit", expire.Plan.Transition.After.State == AuctionOrderState.ExpiredUnsold
                    && expire.Plan.Transition.Legs.Single(l => l.Kind == AuctionDeliveryKind.DepositRefund).Gold == 10000);

                var competitiveList = Request(AuctionOperationAction.List, 2, 0, seller, 2, expires: timestamp + 86400, opening: 100, buyout: 1000);
                var competitive = Plan(competitiveList, null, source).Plan.Transition.After;
                var bid = Plan(Request(AuctionOperationAction.Bid, 2, 1, buyer, gold: 110), competitive);
                var topup = Plan(Request(AuctionOperationAction.Bid, 2, 2, buyer with { FrozenName = "renamed fixture" }, gold: 120), bid.Plan.Transition.After);
                Check("highest bidder name change still only funds difference", topup.Plan.GoldDebit == 10 && topup.Plan.Transition.Legs.Count == 0);
                var overbid = Plan(Request(AuctionOperationAction.Bid, 2, 3, third, gold: 130), topup.Plan.Transition.After);
                Check("new bidder funds full bid and refunds previous bid once", overbid.Plan.GoldDebit == 130
                    && overbid.Plan.Transition.Legs.Single().Gold == 120 && overbid.Plan.Transition.After.FundedBidGold == 130);
                var buyout = Plan(Request(AuctionOperationAction.Buyout, 2, 4, third, gold: 1000), overbid.Plan.Transition.After);
                Check("highest bidder buyout only supplements and never refunds itself", buyout.Plan.GoldDebit == 870
                    && buyout.Plan.Transition.Legs.All(l => l.Kind != AuctionDeliveryKind.BidRefund) && buyout.Plan.Transition.After.State == AuctionOrderState.Sold);
                var fundedExpire = Plan(Request(AuctionOperationAction.Expire, 2, 4, seller), overbid.Plan.Transition.After, at: timestamp + 86400);
                Check("funded expiry consumes existing funds without buyer debit", fundedExpire.Plan.GoldDebit == 0
                    && fundedExpire.Plan.Transition.After.FundedBidGold == 0 && fundedExpire.Plan.Transition.Quote.GrossGold == 130);
                int entitlementReads = 0;
                var unavailableEntitlements = new AuctionApprovedQuoteProvider((r, b, n) => { entitlementReads++; return null; });
                var lockedBuyout = unavailableEntitlements.Quote(Request(AuctionOperationAction.Buyout, 1, 1, buyer, gold: 300), order, timestamp);
                var lockedExpiry = unavailableEntitlements.Quote(Request(AuctionOperationAction.Expire, 1, 1, seller), order, timestamp + 86400);
                Check("locked buyout and expiry do not query current entitlements", entitlementReads == 0
                    && lockedBuyout.GrossGold == 300 && lockedExpiry.DepositRefundGold == 10000);

                // Real private/public/private application over isolated test files,
                // without a game server, client, fake receipt or runtime registration.
                var root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-market-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                var sellerSession = Guid.NewGuid(); var buyerSession = Guid.NewGuid();
                try
                {
                    var database = new GameDatabase(Path.Combine(root, "inventory.db"), ServerPaths.SchemaFilePath);
                    database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                        cmd.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(78201,'auction-market-a',''),(78202,'auction-market-b','');"
                            + "INSERT INTO characters(character_id,account_id,name,level) VALUES(78201,78201,'auction-market-a',85),(78202,78202,'auction-market-b',85);"; cmd.ExecuteNonQuery(); });
                    var left = InventoryContext.Register(sellerSession, new InventoryService(seller.CharacterId, seller.AccountId, database));
                    var right = InventoryContext.Register(buyerSession, new InventoryService(buyer.CharacterId, buyer.AccountId, database));
                    left.Inventory.SetMainVirtualCount(0, 50000); right.Inventory.SetMainVirtualCount(0, 50000);
                    left.Inventory.SetItem(InventoryListType.Main, 65, new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3001, Count = 10 });
                    var store = new AuctionOrderStore(Path.Combine(root, "private.db"), codec, allowFixturePolicies: true);
                    var gate = new object(); var rail = new AuctionPublicAssetCommit(database, codec, gate);
                    var templateFiles = new Dictionary<string, string> {
                        ["equipment/equipment.lst"] = "101 `fixture.equ`",
                        ["equipment/fixture.equ"] = "[name]\n`fixture sword`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n",
                        ["stackable/stackable.lst"] = "3001 `fixture.stk`",
                        ["stackable/fixture.stk"] = "[name]\n`fixture material`\n[stackable type]\n`[material]` 0\n[attach type]\n`[free]`\n"
                    };
                    var catalog = AuctionTemplateCatalog.Load(path => templateFiles.TryGetValue(path, out var value) ? value : null, new string('a', 64));
                    AuctionNativeListingProof Native(AuctionNativeListingContext context)
                        => new(AuctionNativeProofKind.Fixture, AuctionListingEligibilityStatus.Allowed,
                            context.Template.TemplateId, context.SourceContentHash, context.Template.SourcePvfSha256,
                            AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount, false, "fixture/market-only-count", "");
                    Func<AuctionNativeListingContext, AuctionNativeListingProof> proof = Native;
                    var eligibility = new AuctionListingEligibility(catalog, codec, context => proof(context), allowFixtureProofs: true);
                    MailboxSendRequest Mail(AuctionDeliveryLeg leg, Guid operation)
                    {
                        var mail = new MailboxSendRequest { SenderCharacterId = leg.Recipient.CharacterId, SenderAccountId = leg.Recipient.AccountId,
                            SenderName = "fixture system", ReceiverCharacterId = leg.Recipient.CharacterId, ReceiverAccountId = leg.Recipient.AccountId,
                            ReceiverName = leg.Recipient.FrozenName, Gold = checked((int)leg.Gold), Text = "fixture auction leg", Unlimited = true,
                            IdempotencyKey = "auction:" + operation.ToString("N") + ":" + leg.LegKey };
                        if (leg.ItemEnvelopeJson != null)
                        {
                            var item = codec.DeserializeStrict(leg.ItemEnvelopeJson);
                            mail.Attachments = new[] { new MailboxSendAttachmentRequest { ItemId = codec.DecodeStrict(item).ItemId,
                                ItemCount = leg.Quantity, ItemCoreData = item.NativeCore99, DetailJson = item.DetailJson, ExtraJson = item.ExtensionsJson } };
                        }
                        return mail;
                    }
                    long currentTime = timestamp;
                    var application = new AuctionMarketApplication(store, codec, planner, eligibility, Fixture, rail, Mail, gate,
                        () => currentTime, allowFixtureListingProofs: true);
                    var registration = Request(AuctionOperationAction.List, 0, 0, seller, 2, expires: timestamp + 86400, buyout: 100);
                    var productionAuthority = new AuctionMarketApplication(store, codec, planner, eligibility, Fixture, rail, Mail, gate, () => currentTime);
                    var productionDenied = productionAuthority.Execute(registration, left, InventoryListType.Main, 65);
                    Check("application production default refuses fixture native capability before Prepare", productionDenied.Status == AuctionMarketStatus.Unavailable
                        && store.FindOperation(registration.OperationId) == null && left.Inventory.GetMainVirtualCount(0).Count == 50000);
                    // Reuse the real owned fixture and Allowed native proof, but
                    // deny final native display before any durable reservation.
                    // An explicit unrelated ID leaves the sequence/replay cases below intact.
                    var undisplayableRequest = registration with { OperationId = Guid.NewGuid(), OrderId = 900000001 };
                    AuctionItemEnvelope beforeDisplaySource;
                    lock (left.SyncRoot) beforeDisplaySource = codec.Capture(left.Inventory, InventoryListType.Main, 65);
                    int beforeDisplayGold = left.Inventory.GetMainVirtualCount(0).Count;
                    var undisplayableApp = new AuctionMarketApplication(store, codec, planner, eligibility, Fixture, rail, Mail, gate,
                        () => currentTime, allowFixtureListingProofs: true, canRepresentListing: _ => false);
                    var undisplayable = undisplayableApp.Execute(undisplayableRequest, left, InventoryListType.Main, 65);
                    AuctionItemEnvelope afterDisplaySource;
                    lock (left.SyncRoot) afterDisplaySource = codec.Capture(left.Inventory, InventoryListType.Main, 65);
                    long displayReceipts = database.Read(c => { using var cmd = c.CreateCommand();
                        cmd.CommandText = "SELECT count(*) FROM inventory_audit_log WHERE action_name=@action;";
                        cmd.Parameters.AddWithValue("@action", "auction_asset:" + undisplayableRequest.OperationId.ToString("N"));
                        return Convert.ToInt64(cmd.ExecuteScalar()); });
                    Check("unrepresentable native listing rejects before order journal receipt or asset debit",
                        undisplayable.Status == AuctionMarketStatus.Unavailable && undisplayable.Reason == "NativeListingDisplayUnavailable"
                        && store.FindOrder(undisplayableRequest.OrderId) == null && store.FindOperation(undisplayableRequest.OperationId) == null
                        && store.CountActiveAndReservedAccountListings(seller.AccountId) == 0 && displayReceipts == 0
                        && left.Inventory.GetMainVirtualCount(0).Count == beforeDisplayGold
                        && beforeDisplaySource.ContentHashHex == afterDisplaySource.ContentHashHex
                        && codec.Serialize(beforeDisplaySource) == codec.Serialize(afterDisplaySource)
                        && codec.DecodeStrict(afterDisplaySource).Count == 10);
                    proof = context => null;
                    Check("mandatory native eligibility cannot fall back to successful codec/catalog", application.Execute(registration, left, InventoryListType.Main, 65).Status == AuctionMarketStatus.Unavailable
                        && store.FindOperation(registration.OperationId) == null && left.Inventory.GetItem(InventoryListType.Main, 65).Count == 10);
                    int proofReads = 0;
                    proof = context => ++proofReads == 1 ? Native(context) : null;
                    var proofChanged = application.Execute(registration, left, InventoryListType.Main, 65);
                    Check("public transaction rechecks native capability before any debit and aborts reservation", proofChanged.Status == AuctionMarketStatus.Aborted
                        && proofReads == 2 && store.CountActiveAndReservedAccountListings(seller.AccountId) == 0
                        && left.Inventory.GetMainVirtualCount(0).Count == 50000 && left.Inventory.GetItem(InventoryListType.Main, 65).Count == 10);
                    Check("aborted eligibility operation cannot automatically reexecute", application.Execute(registration, left, InventoryListType.Main, 65).Status == AuctionMarketStatus.Aborted
                        && proofReads == 2 && left.Inventory.GetMainVirtualCount(0).Count == 50000);
                    proof = Native;
                    database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                        cmd.CommandText = "INSERT INTO account_premiums(account_id,premium_type,end_time) VALUES(78201,29,@expiry);";
                        cmd.Parameters.AddWithValue("@expiry", timestamp + 1); cmd.ExecuteNonQuery(); });
                    var premiumQuotes = new AuctionApprovedQuoteProvider((r, b, n) => new(timestamp + 1, 0, true));
                    AuctionVerifiedQuote PremiumFixture(AuctionMarketRequest r, AuctionOrderSnapshot b, long n)
                        => premiumQuotes.Quote(r, b, n) with { Availability = AuctionQuoteAvailability.FixtureOnly };
                    int timeReads = 0;
                    var premiumApp = new AuctionMarketApplication(store, codec, planner, eligibility, PremiumFixture, rail, Mail, gate,
                        () => ++timeReads == 1 ? timestamp : timestamp + 1, allowFixtureListingProofs: true);
                    var premiumRequest = registration with { OperationId = Guid.NewGuid() };
                    var expiredAtCommit = premiumApp.Execute(premiumRequest, left, InventoryListType.Main, 65);
                    Check("premium equal-to-expiry is re-read on the public transaction before debit", expiredAtCommit.Status == AuctionMarketStatus.Aborted
                        && timeReads == 2 && left.Inventory.GetMainVirtualCount(0).Count == 50000
                        && store.CountActiveAndReservedAccountListings(seller.AccountId) == 0);
                    database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                        cmd.CommandText = "DELETE FROM account_premiums WHERE account_id=78201 AND premium_type=29;"; cmd.ExecuteNonQuery(); });
                    var mismatchedPremium = new AuctionMarketApplication(store, codec, planner, eligibility, PremiumFixture, rail, Mail, gate,
                        () => currentTime, allowFixtureListingProofs: true);
                    var mismatchedRequest = registration with { OperationId = Guid.NewGuid() };
                    Check("injected quote cannot substitute unowned premium fee/quota", mismatchedPremium.Execute(mismatchedRequest, left, InventoryListType.Main, 65).Status == AuctionMarketStatus.Rejected
                        && store.FindOperation(mismatchedRequest.OperationId) == null && left.Inventory.GetMainVirtualCount(0).Count == 50000);
                    registration = registration with { OperationId = Guid.NewGuid() };
                    var registered = application.Execute(registration, left, InventoryListType.Main, 65);
                    Check("real application activates only public-funded listing", registered.Status == AuctionMarketStatus.Committed
                        && registered.Order.State == AuctionOrderState.Active && left.Inventory.GetItem(InventoryListType.Main, 65).Count == 8
                        && left.Inventory.GetMainVirtualCount(0).Count == 40000);
                    var purchased = application.Execute(Request(AuctionOperationAction.Buyout, registered.Order.OrderId, 1, buyer, 2, 100), right);
                    Check("real buyout atomically records mail legs and private sold state", purchased.Status == AuctionMarketStatus.Committed
                        && purchased.Order.State == AuctionOrderState.Sold && right.Inventory.GetMainVirtualCount(0).Count == 49900);
                    var replay = application.Execute(registration, left, InventoryListType.Main, 65);
                    Check("terminal listing replay returns frozen result without another debit", replay.Status == AuctionMarketStatus.Committed
                        && replay.Order.State == AuctionOrderState.Active && left.Inventory.GetMainVirtualCount(0).Count == 40000
                        && left.Inventory.GetItem(InventoryListType.Main, 65).Count == 8);
                    Check("same UUID changed request cannot reuse prior result", application.Execute(registration with { Quantity = 3 }, left, InventoryListType.Main, 65).Status == AuctionMarketStatus.Rejected);
                    Check("same UUID changed source slot cannot replay another source", application.Execute(registration, left, InventoryListType.Main, 66).Status == AuctionMarketStatus.Rejected);
                    Check("same UUID changed actor cannot see the prior order result", application.Execute(registration with { Actor = buyer }, right, InventoryListType.Main, 65).Status == AuctionMarketStatus.Rejected);

                    var pendingRequest = Request(AuctionOperationAction.List, store.AllocateOrderId(), 0, seller, 1, expires: timestamp + 86400, buyout: 50)
                        with { SourceList = InventoryListType.Main, SourceSlot = 65 };
                    var pendingSource = codec.Capture(left.Inventory, InventoryListType.Main, 65);
                    var pendingPlan = Plan(pendingRequest, null, pendingSource).Plan;
                    var assetPlan = AuctionPublicAssetPlan.Create(codec, pendingPlan.GoldDebit,
                        new[] { new AuctionItemDebit(InventoryListType.Main, 65, 1, pendingSource) }, Array.Empty<MailboxSendRequest>());
                    var pendingIntent = new AuctionOperationIntent(pendingRequest.OperationId, "order:" + pendingRequest.OrderId, pendingRequest.Action,
                        seller.CharacterId, seller.AccountId, 0, timestamp, JsonSerializer.Serialize(new { publicAssetPlan = assetPlan.Serialize(), orderTransition = pendingPlan.Transition.Serialize() }));
                    store.Prepare(pendingIntent, pendingPlan.Transition);
                    Check("existing Prepared is never automatically executed", application.Execute(pendingRequest, left, InventoryListType.Main, 65).Status == AuctionMarketStatus.Pending
                        && left.Inventory.GetMainVirtualCount(0).Count == 40000 && left.Inventory.GetItem(InventoryListType.Main, 65).Count == 8);
                    // Simulate the public-commit/private-complete crash window using
                    // the real rail, then let Resume finish only the private side.
                    var committedReceipt = rail.Execute(pendingIntent, assetPlan, left);
                    Check("fixture public crash gap actually committed once", committedReceipt.State == AuctionAssetCommitState.Committed
                        && left.Inventory.GetMainVirtualCount(0).Count == 30000 && left.Inventory.GetItem(InventoryListType.Main, 65).Count == 7);
                    var resumed = application.Resume(pendingRequest.OperationId);
                    Check("Resume completes private state without second public execution", resumed.Status == AuctionMarketStatus.Committed
                        && resumed.Order.State == AuctionOrderState.Active && left.Inventory.GetMainVirtualCount(0).Count == 30000
                        && left.Inventory.GetItem(InventoryListType.Main, 65).Count == 7);

                    currentTime += 10;
                    var durationRequest = Request(AuctionOperationAction.List, 0, 0, seller, 1, buyout: 50)
                        with { DurationHours = 24, SourceList = InventoryListType.Main, SourceSlot = 65 };
                    var durationListing = application.Execute(durationRequest, left);
                    Check("listing duration starts at application server time", durationListing.Status == AuctionMarketStatus.Committed
                        && durationListing.Order.CreatedAtUnix == currentTime && durationListing.Order.ExpiresAtUnix == currentTime + 86400);
                    currentTime += 3;
                    Check("duration/order-zero replay normalizes frozen deadline and source", application.Execute(durationRequest, left).Status == AuctionMarketStatus.Committed
                        && left.Inventory.GetMainVirtualCount(0).Count == 20000);

                    var competitionRequest = Request(AuctionOperationAction.List, 0, 0, seller, 1, opening: 100, buyout: 1000)
                        with { DurationHours = 24, SourceList = InventoryListType.Main, SourceSlot = 65 };
                    var competition = application.Execute(competitionRequest, left);
                    var actualBid = application.Execute(Request(AuctionOperationAction.Bid, competition.Order.OrderId, 1, buyer, gold: 110), right);
                    Check("real funded bid exists before offline expiry", actualBid.Status == AuctionMarketStatus.Committed
                        && actualBid.Order.FundedBidGold == 110 && right.Inventory.GetMainVirtualCount(0).Count == 49790);
                    InventoryContext.Unregister(sellerSession); InventoryContext.Unregister(buyerSession);
                    currentTime = competition.Order.ExpiresAtUnix;
                    AuctionVerifiedQuote LockedFixture(AuctionMarketRequest r, AuctionOrderSnapshot b, long n)
                        => unavailableEntitlements.Quote(r, b, n) with { Availability = AuctionQuoteAvailability.FixtureOnly };
                    var offline = new AuctionMarketApplication(store, codec, planner, eligibility, LockedFixture, rail, Mail, gate,
                        () => currentTime, allowFixtureListingProofs: true);
                    int readsBefore = entitlementReads;
                    var unsoldRequest = Request(AuctionOperationAction.Expire, durationListing.Order.OrderId, 1, seller);
                    var unsold = offline.ExecuteOfflineExpiry(unsoldRequest);
                    var fundedRequest = Request(AuctionOperationAction.Expire, actualBid.Order.OrderId, 2, seller);
                    var settled = offline.ExecuteOfflineExpiry(fundedRequest);
                    Check("offline unsold expiry returns funded item/deposit without lease", unsold.Status == AuctionMarketStatus.Committed
                        && unsold.Order.State == AuctionOrderState.ExpiredUnsold && !InventoryContext.TryGetLease(seller.CharacterId, out _));
                    Check("offline funded expiry sells once without debiting offline bidder again", settled.Status == AuctionMarketStatus.Committed
                        && settled.Order.State == AuctionOrderState.Sold && settled.Order.FundedBidGold == 0
                        && right.Inventory.GetMainVirtualCount(0).Count == 49790 && entitlementReads == readsBefore);
                    Check("offline expiry replay returns terminal without extra mail", offline.ExecuteOfflineExpiry(fundedRequest).Status == AuctionMarketStatus.Committed
                        && settled.Operation.ResultJson == offline.Resume(fundedRequest.OperationId).Operation.ResultJson);
                }
                finally
                {
                    InventoryContext.Unregister(sellerSession); InventoryContext.Unregister(buyerSession);
                    SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
                }
                RunApplicationMoneyCases(Check, timestamp);
            }
            catch (Exception ex) { Console.WriteLine(ex); fail++; }
            Console.WriteLine($"AuctionMarketSelfTest: passed={pass} failed={fail}; fixture/test files only, no native UI exercised.");
            return fail == 0 ? 0 : 1;
        }

        // These independent offline files keep the original assertions/wallets
        // unchanged. They use the production application/rail/store, not a
        // planner substitute, fake receipt, mail stub or game-server instance.
        private static void RunApplicationMoneyCases(Action<string, bool> check, long timestamp)
        {
            using (var f = new FinancialFixture(check, timestamp, "bid-chain"))
            {
                var order = f.List();
                var bid = f.Request(AuctionOperationAction.Bid, order.OrderId, 1, f.First, 200);
                f.Committed("real first bid", bid, f.Application.Execute(bid, f.FirstLease), 200);
                f.Balance("first bid", order.OrderId, 2, 49800, 50000, 200, 0, highest: f.First);
                var topup = f.Request(AuctionOperationAction.Bid, order.OrderId, 2, f.First, 400);
                f.Committed("real same-role topup", topup, f.Application.Execute(topup, f.FirstLease), 200);
                f.Balance("topup", order.OrderId, 3, 49600, 50000, 400, 0, highest: f.First);
                var overbid = f.Request(AuctionOperationAction.Bid, order.OrderId, 3, f.Alternate, 600);
                f.Committed("real same-account other-role overbid", overbid, f.Application.Execute(overbid, f.AlternateLease), 600,
                    new ExpectedMail("bid-refund", f.First, 400));
                f.Balance("overbid", order.OrderId, 4, 49600, 49400, 600, 1, highest: f.Alternate);
                var buyout = f.Request(AuctionOperationAction.Buyout, order.OrderId, 4, f.Alternate, 1000);
                f.Committed("real highest-role buyout supplement", buyout, f.Application.Execute(buyout, f.AlternateLease), 400,
                    f.SaleMail(f.Alternate));
                f.Balance("closed bid chain", order.OrderId, 5, 49600, 49000, 0, 4, sold: true);
                f.Require("bid chain has exactly five real permanent receipts", f.ReceiptTotal() == 5);
            }
            using (var f = new FinancialFixture(check, timestamp, "version-race"))
            {
                var order = f.List();
                var bid = f.Request(AuctionOperationAction.Bid, order.OrderId, 1, f.First, 200);
                var buyout = f.Request(AuctionOperationAction.Buyout, order.OrderId, 1, f.Alternate, 1000);
                var results = f.Concurrent(() => f.Application.Execute(bid, f.FirstLease),
                    () => f.Application.Execute(buyout, f.AlternateLease));
                f.Require("same-version actual application race commits exactly one and rejects the other",
                    results.Count(r => r.Status == AuctionMarketStatus.Committed) == 1
                    && results.Count(r => r.Status == AuctionMarketStatus.Rejected) == 1);
                bool bidWon = results[0].Status == AuctionMarketStatus.Committed;
                Console.WriteLine("[INFO] auction market version-race actual winner: " + (bidWon ? "Bid" : "Buyout")
                    + "; the other scheduling branch was not exercised by this race.");
                f.NoEffects("same-version loser", bidWon ? buyout : bid, bidWon ? f.Alternate : f.First, 50000);
                if (bidWon)
                {
                    f.Committed("racing bid winner", bid, results[0], 200);
                    f.Balance("race bid funded", order.OrderId, 2, 49800, 50000, 200, 0, highest: f.First);
                    var settle = f.Request(AuctionOperationAction.Buyout, order.OrderId, 2, f.First, 1000);
                    f.Committed("racing bid winner later settles normally", settle, f.Application.Execute(settle, f.FirstLease), 800,
                        f.SaleMail(f.First));
                    f.Balance("race bid winner closed", order.OrderId, 3, 49000, 50000, 0, 3, sold: true);
                }
                else
                {
                    f.Committed("racing buyout winner", buyout, results[1], 1000, f.SaleMail(f.Alternate));
                    f.Balance("race buyout winner closed", order.OrderId, 2, 50000, 49000, 0, 3, sold: true);
                }
                f.NoEffects("loser remains absent after settlement", bidWon ? buyout : bid, bidWon ? f.Alternate : f.First, 50000);
                f.Require("version race has only winner and optional normal settlement receipts", f.ReceiptTotal() == (bidWon ? 3 : 2));
            }
            using (var f = new FinancialFixture(check, timestamp, "same-operation"))
            {
                var order = f.List();
                var buyout = f.Request(AuctionOperationAction.Buyout, order.OrderId, 1, f.First, 1000);
                var results = f.Concurrent(() => f.Application.Execute(buyout, f.FirstLease),
                    () => f.Application.Execute(buyout, f.FirstLease));
                f.Require("same UUID concurrent application calls return the same committed result",
                    results.All(r => r.Status == AuctionMarketStatus.Committed && r.Order.Version == 2)
                    && results[0].Operation.Intent.IntentHash == results[1].Operation.Intent.IntentHash
                    && results[0].Operation.ResultJson == results[1].Operation.ResultJson);
                f.Committed("same UUID actual buyout", buyout, results[0], 1000, f.SaleMail(f.First));
                f.Balance("same UUID closed once", order.OrderId, 2, 49000, 50000, 0, 3, sold: true);
                f.Require("same UUID has one public receipt and only listing plus buyout receipts", f.ReceiptCount(buyout.OperationId) == 1
                    && f.ReceiptTotal() == 2);
            }
            using (var f = new FinancialFixture(check, timestamp, "offline-refund"))
            {
                var order = f.List();
                var firstBid = f.Request(AuctionOperationAction.Bid, order.OrderId, 1, f.First, 400);
                f.Committed("real old highest bid", firstBid, f.Application.Execute(firstBid, f.FirstLease), 400);
                f.Balance("before disconnect", order.OrderId, 2, 49600, 50000, 400, 0, highest: f.First);
                f.DisconnectFirstBidder();
                f.Require("new bidder still owns its current lease while old highest is offline",
                    InventoryContext.IsCurrentLease(f.AlternateLease, f.AlternateLease.SessionId, f.Alternate.CharacterId));
                var nextBid = f.Request(AuctionOperationAction.Bid, order.OrderId, 2, f.Alternate, 600);
                var paid = f.Application.Execute(nextBid, f.AlternateLease);
                f.Committed("real overbid refunds offline old highest", nextBid, paid, 600,
                    new ExpectedMail("bid-refund", f.First, 400));
                f.Balance("offline refund committed", order.OrderId, 3, 49600, 49400, 600, 1, highest: f.Alternate);
                f.RequireFirstBidderOffline("refund publication did not register the old recipient");
                f.Require("listing and two bids have exactly three real permanent receipts", f.ReceiptTotal() == 3);
                var replay = f.Application.Execute(nextBid, f.AlternateLease);
                f.Require("same UUID replay retains exact terminal intent result and order version",
                    replay.Status == AuctionMarketStatus.Committed && replay.Order.Version == 3
                    && replay.Operation.Intent.IntentHash == paid.Operation.Intent.IntentHash
                    && replay.Operation.ResultJson == paid.Operation.ResultJson);
                f.Committed("offline refund replay keeps the same unique unclaimed mail", nextBid, replay, 600,
                    new ExpectedMail("bid-refund", f.First, 400));
                f.Balance("offline refund replay unchanged", order.OrderId, 3, 49600, 49400, 600, 1, highest: f.Alternate);
                f.RequireFirstBidderOffline("replay did not register the old recipient");
                f.Require("replay creates no fourth receipt or additional refund", f.ReceiptTotal() == 3
                    && f.ReceiptCount(firstBid.OperationId) == 1 && f.ReceiptCount(nextBid.OperationId) == 1);
            }
        }

        private sealed record ExpectedMail(string Leg, AuctionParty Receiver, int Gold, int ItemCount = 0);
        private sealed record FinancialMail(long Id, int CharacterId, int AccountId, int Gold, int Type, int Unlimited, string Key);

        private sealed class FinancialFixture : IDisposable
        {
            private readonly Action<string, bool> check;
            private readonly string label;
            private readonly string root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-market-risk-" + Guid.NewGuid().ToString("N"));
            private readonly Guid[] sessions = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            private readonly InventoryLease[] leases = new InventoryLease[3];
            private readonly AuctionItemCodec codec = new(new NativeAuctionInstanceExtension());
            private readonly long timestamp;
            private bool abandonCleanup;
            internal AuctionParty Seller { get; } = new(78301, 78301, "risk seller");
            internal AuctionParty First { get; } = new(78302, 78302, "risk first");
            internal AuctionParty Alternate { get; } = new(78303, 78302, "risk alternate");
            internal InventoryLease FirstLease => leases[1];
            internal InventoryLease AlternateLease => leases[2];
            internal GameDatabase Database { get; }
            internal AuctionOrderStore Store { get; }
            internal AuctionMarketApplication Application { get; }
            private AuctionItemEnvelope source;

            internal FinancialFixture(Action<string, bool> check, long timestamp, string label)
            {
                this.check = check; this.timestamp = timestamp; this.label = label;
                Directory.CreateDirectory(root);
                try
                {
                    Database = new GameDatabase(Path.Combine(root, "inventory.db"), ServerPaths.SchemaFilePath);
                    Database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                        cmd.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(78301,'risk-seller',''),(78302,'risk-bidders','');"
                            + "INSERT INTO characters(character_id,account_id,name,level) VALUES(78301,78301,'risk seller',85),"
                            + "(78302,78302,'risk first',85),(78303,78302,'risk alternate',85);"; cmd.ExecuteNonQuery(); });
                    var parties = new[] { Seller, First, Alternate };
                    for (int i = 0; i < parties.Length; i++)
                    {
                        leases[i] = InventoryContext.Register(sessions[i], new InventoryService(parties[i].CharacterId, parties[i].AccountId, Database));
                        leases[i].Inventory.SetMainVirtualCount(0, 50000);
                    }
                    leases[0].Inventory.SetItem(InventoryListType.Main, 65, new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3001, Count = 1 });
                    lock (leases[0].SyncRoot) source = codec.Capture(leases[0].Inventory, InventoryListType.Main, 65);
                    foreach (var lease in leases)
                        if (!InventoryPersistenceService.SaveDirty(lease)) throw new InvalidOperationException("Risk fixture baseline did not persist.");
                    Store = new AuctionOrderStore(Path.Combine(root, "private.db"), codec, allowFixturePolicies: true);
                    var files = new Dictionary<string, string> {
                        ["equipment/equipment.lst"] = "101 `fixture.equ`",
                        ["equipment/fixture.equ"] = "[name]\n`fixture sword`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n",
                        ["stackable/stackable.lst"] = "3001 `fixture.stk`",
                        ["stackable/fixture.stk"] = "[name]\n`fixture material`\n[stackable type]\n`[material]` 0\n[attach type]\n`[free]`\n"
                    };
                    var catalog = AuctionTemplateCatalog.Load(p => files.TryGetValue(p, out var v) ? v : null, new string('a', 64));
                    var eligibility = new AuctionListingEligibility(catalog, codec, c => new(AuctionNativeProofKind.Fixture,
                        AuctionListingEligibilityStatus.Allowed, c.Template.TemplateId, c.SourceContentHash, c.Template.SourcePvfSha256,
                        AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount, false, "fixture/risk-only-count", ""), allowFixtureProofs: true);
                    AuctionItemEnvelope Transfer(AuctionItemEnvelope item, int quantity, bool returning)
                    { var core = codec.DecodeStrict(item); core.Count = quantity; return codec.CreateTransferSnapshot(item, core); }
                    var planner = new AuctionMarketPlanner(codec, Transfer, allowFixtureQuotes: true);
                    var approved = new AuctionApprovedQuoteProvider((r, b, n) => new(null, 0, true));
                    AuctionVerifiedQuote Quote(AuctionMarketRequest r, AuctionOrderSnapshot b, long n)
                        => approved.Quote(r, b, n) with { Availability = AuctionQuoteAvailability.FixtureOnly };
                    MailboxSendRequest Mail(AuctionDeliveryLeg leg, Guid op)
                    {
                        var mail = new MailboxSendRequest { SenderCharacterId = leg.Recipient.CharacterId, SenderAccountId = leg.Recipient.AccountId,
                            SenderName = leg.Recipient.FrozenName, ReceiverCharacterId = leg.Recipient.CharacterId, ReceiverAccountId = leg.Recipient.AccountId,
                            ReceiverName = leg.Recipient.FrozenName, Gold = checked((int)leg.Gold), MailType = 1, Text = "risk fixture system mail",
                            Unlimited = true, IdempotencyKey = "auction:" + op.ToString("N") + ":" + leg.LegKey };
                        if (leg.ItemEnvelopeJson != null)
                        {
                            var item = codec.DeserializeStrict(leg.ItemEnvelopeJson);
                            mail.Attachments = new[] { new MailboxSendAttachmentRequest { ItemId = codec.DecodeStrict(item).ItemId,
                                ItemCount = leg.Quantity, ItemCoreData = item.NativeCore99, DetailJson = item.DetailJson, ExtraJson = item.ExtensionsJson } };
                        }
                        return mail;
                    }
                    var gate = new object();
                    var rail = new AuctionPublicAssetCommit(Database, codec, gate);
                    Application = new AuctionMarketApplication(Store, codec, planner, eligibility, Quote, rail, Mail, gate,
                        () => timestamp, allowFixtureListingProofs: true);
                    Require("three roles start with persisted wallets and two bidders share an account", First.AccountId == Alternate.AccountId
                        && Seller.AccountId != First.AccountId && new[] { Seller, First, Alternate }.All(p => Wallet(p) == 50000));
                }
                catch { Dispose(); throw; }
            }

            internal void Require(string name, bool ok)
            { check("high-risk " + label + ": " + name, ok); if (!ok) throw new InvalidOperationException("High-risk fixture assertion: " + name); }
            internal AuctionMarketRequest Request(AuctionOperationAction action, long order, long version, AuctionParty actor, long gold)
                => new(Guid.NewGuid(), action, order, version, actor, 1, gold, 0);
            internal ExpectedMail[] SaleMail(AuctionParty buyer) => new[] {
                new ExpectedMail("item-delivery", buyer, 0, 1), new ExpectedMail("seller-proceeds", Seller, 950), new ExpectedMail("deposit-refund", Seller, 10000) };
            internal AuctionOrderSnapshot List()
            {
                var request = new AuctionMarketRequest(Guid.NewGuid(), AuctionOperationAction.List, 0, 0, Seller, 1, 0, timestamp + 86400, 100, 1000);
                var result = Application.Execute(request, leases[0], InventoryListType.Main, 65);
                Committed("real funded listing", request, result, 10000);
                Balance("listed", result.Order.OrderId, 1, 50000, 50000, 0, 0);
                return result.Order;
            }
            internal void DisconnectFirstBidder()
            {
                Require("old highest owns a real trusted lease before disconnect",
                    InventoryContext.IsCurrentLease(FirstLease, sessions[1], First.CharacterId)
                    && InventoryContext.TryGetLeaseForRecovery(First.CharacterId, out var physical)
                    && ReferenceEquals(physical, FirstLease));
                Require("old highest dirty baseline saves and normal unregister succeeds",
                    InventoryPersistenceService.SaveDirty(FirstLease) && InventoryContext.Unregister(sessions[1], First.CharacterId));
                RequireFirstBidderOffline("normal disconnect removes the physical owner and session mapping");
            }
            internal void RequireFirstBidderOffline(string phase)
            {
                // Use the raw recovery lookup too: a poisoned/filtered owner is
                // not proof of absence. Never replace it with a synthetic lease.
                Require(phase, !InventoryContext.TryGetLeaseForRecovery(First.CharacterId, out _)
                    && !InventoryContext.TryGetLease(First.CharacterId, out _)
                    && !InventoryContext.TryGetOwnedLease(sessions[1], First.CharacterId, out _)
                    && !InventoryContext.IsCurrentLease(FirstLease, sessions[1], First.CharacterId)
                    && !InventoryContext.GetLeasesSnapshot().Any(l => l.CharacterId == First.CharacterId));
            }
            private long Wallet(AuctionParty party) => Database.Read(c => CurrencyService.LoadWallet(c, null, party.CharacterId).Gold);
            private long Scalar(string sql, params (string Name, object Value)[] parameters) => Database.Read(c => {
                using var cmd = c.CreateCommand(); cmd.CommandText = sql;
                foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value);
                return Convert.ToInt64(cmd.ExecuteScalar()); });
            internal long ReceiptCount(Guid op) => Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name=@action;", ("@action", "auction_asset:" + op.ToString("N")));
            internal long ReceiptTotal() => Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name LIKE 'auction_asset:%';");
            private FinancialMail[] MailRows() => Database.Read(c => {
                using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT message_id,receiver_character_id,receiver_account_id,gold,mail_type,unlimited_flag,idempotency_key FROM mailbox_messages ORDER BY message_id;";
                using var r = cmd.ExecuteReader(); var rows = new List<FinancialMail>();
                while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetString(6)));
                return rows.ToArray(); });

            internal void Committed(string name, AuctionMarketRequest request, AuctionMarketResult result, int debit, params ExpectedMail[] expected)
            {
                Require(name + " completed real application/private journal", result.Status == AuctionMarketStatus.Committed
                    && result.Operation?.State == AuctionOperationState.Committed && Store.FindOperation(request.OperationId)?.State == AuctionOperationState.Committed);
                var entry = Store.FindOperation(request.OperationId);
                var receipt = Database.Read(c => AuctionPublicReceiptRepository.Find(c, null, entry.Intent));
                Require(name + " permanent receipt uniquely matches private result", receipt != null && ReceiptCount(request.OperationId) == 1
                    && receipt.ResultJson == entry.ResultJson && receipt.CharacterId == request.Actor.CharacterId && receipt.AccountId == request.Actor.AccountId);
                using var body = JsonDocument.Parse(receipt.ResultJson);
                var ids = body.RootElement.GetProperty("mailMessageIds").EnumerateArray().Select(x => x.GetInt64()).ToArray();
                string prefix = "auction:" + request.OperationId.ToString("N") + ":";
                var messages = MailRows().Where(m => m.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                Require(name + " exact durable debit and mail IDs", body.RootElement.GetProperty("goldDebited").GetInt32() == debit
                    && body.RootElement.GetProperty("itemDebitCount").GetInt32() == (request.Action == AuctionOperationAction.List ? 1 : 0)
                    && messages.Length == expected.Length && ids.Distinct().Count() == ids.Length && ids.SequenceEqual(messages.Select(m => m.Id)));
                foreach (var leg in expected)
                {
                    var matches = messages.Where(m => m.Key == prefix + leg.Leg).ToArray();
                    Require(name + " unique " + leg.Leg + " recipient/gold/system lifetime", matches.Length == 1
                        && matches[0].CharacterId == leg.Receiver.CharacterId && matches[0].AccountId == leg.Receiver.AccountId
                        && matches[0].Gold == leg.Gold && matches[0].Type == 1 && matches[0].Unlimited == 1);
                    long id = matches[0].Id;
                    Require(name + " " + leg.Leg + " is unclaimed inbox asset", Scalar("SELECT count(*) FROM mailbox_recipients WHERE message_id=@id AND character_id=@cid AND folder=0 AND deleted_flag=0 AND received_gold_flag=0;",
                        ("@id", id), ("@cid", leg.Receiver.CharacterId)) == 1
                        && Scalar("SELECT count(*) FROM mailbox_attachments WHERE message_id=@id;", ("@id", id)) == leg.ItemCount);
                    if (leg.ItemCount != 0)
                    {
                        bool exact = Database.Read(c => { using var cmd = c.CreateCommand();
                            cmd.CommandText = "SELECT item_template_id,item_count,item_core,detail_json,extra_json,claimed_flag FROM mailbox_attachments WHERE message_id=@id;";
                            cmd.Parameters.AddWithValue("@id", id); using var r = cmd.ExecuteReader();
                            return r.Read() && r.GetInt32(0) == 3001 && r.GetInt32(1) == 1 && !r.IsDBNull(2)
                                && ((byte[])r.GetValue(2)).AsSpan().SequenceEqual(source.NativeCore99)
                                && r.GetString(3) == source.DetailJson && r.GetString(4) == source.ExtensionsJson && r.GetInt32(5) == 0 && !r.Read(); });
                        Require(name + " one full99/detail/extension attachment", exact);
                    }
                }
            }

            internal void Balance(string phase, long orderId, long version, int firstGold, int alternateGold,
                long fundedBid, int mailCount, bool sold = false, AuctionParty highest = null)
            {
                var order = Store.FindOrder(orderId); var messages = MailRows();
                var parties = new[] { Seller, First, Alternate }; long[] expectedGold = { 40000, firstGold, alternateGold };
                Require(phase + " persistent wallets equal expected and owned memory", parties.Select((p, i) => Wallet(p) == expectedGold[i]
                    && leases[i].Inventory.GetMainVirtualCount(0).Count == expectedGold[i]).All(x => x));
                Require(phase + " exact CAS/funding/fee and no pending reservation", order.Version == version && order.PendingOperationId == null
                    && order.State == (sold ? AuctionOrderState.Sold : AuctionOrderState.Active) && order.RemainingQuantity == (sold ? 0 : 1)
                    && order.FundedBidGold == fundedBid && order.FundedDepositGold == (sold ? 0 : 10000) && order.HighestBidder == highest
                    && order.FeeRatePercent == 5 && order.CumulativeGrossGold == (sold ? 1000 : 0) && order.CumulativeFeeGold == (sold ? 50 : 0)
                    && Store.Pending(100).Count == 0 && Store.CountActiveAndReservedAccountListings(Seller.AccountId) == (sold ? 0 : 1));
                long items = parties.Sum(p => Database.Read(c => InventoryItemRepository.LoadCharacterItems(c, p.CharacterId)
                    .Where(x => x.Core.ItemId == 3001).Sum(x => x.Core.Count)));
                Require(phase + " persistent wallets plus unclaimed gold funding and tax conserve 150000",
                    messages.Length == mailCount && parties.Sum(Wallet) + messages.Sum(m => (long)m.Gold)
                    + order.FundedBidGold + order.FundedDepositGold + order.CumulativeFeeGold == 150000);
                Require(phase + " source escrow and real mail conserve one item and exact delivery IDs", items == 0
                    && leases.All(l => l.Inventory.CountMainItem(3001) == 0)
                    && items + order.RemainingQuantity + Scalar("SELECT COALESCE(sum(item_count),0) FROM mailbox_attachments;") == 1
                    && parties.SelectMany(p => Store.DeliveryMessageIds(p.CharacterId)).OrderBy(x => x).SequenceEqual(messages.Select(m => m.Id)));
            }
            internal void NoEffects(string name, AuctionMarketRequest request, AuctionParty loser, int expectedGold)
            {
                string prefix = "auction:" + request.OperationId.ToString("N") + ":";
                Require(name + " has no private intent receipt mail or wallet debit", Store.FindOperation(request.OperationId) == null
                    && ReceiptCount(request.OperationId) == 0 && !MailRows().Any(m => m.Key.StartsWith(prefix, StringComparison.Ordinal))
                    && Wallet(loser) == expectedGold && leases.Single(l => l.CharacterId == loser.CharacterId).Inventory.GetMainVirtualCount(0).Count == expectedGold);
            }

            internal AuctionMarketResult[] Concurrent(Func<AuctionMarketResult> first, Func<AuctionMarketResult> second)
            {
                var ready = new CountdownEvent(2); var start = new ManualResetEventSlim(false);
                var results = new AuctionMarketResult[2]; var errors = new Exception[2];
                var actions = new[] { first, second }; int cancelled = 0;
                var threads = Enumerable.Range(0, 2).Select(i => new Thread(() => {
                    try { ready.Signal(); if (!start.Wait(TimeSpan.FromSeconds(5)) || Volatile.Read(ref cancelled) != 0)
                            throw new TimeoutException("Risk worker start barrier expired.");
                        results[i] = actions[i](); }
                    catch (Exception ex) { errors[i] = ex; }
                }) { IsBackground = true, Name = "auction-market-risk-" + label + "-" + i }).ToArray();
                var started = new bool[2]; Exception launchError = null;
                for (int i = 0; i < 2; i++)
                    try { threads[i].Start(); started[i] = true; } catch (Exception ex) { launchError = ex; break; }
                bool readyInTime = launchError == null && ready.Wait(TimeSpan.FromSeconds(5));
                if (!readyInTime) Volatile.Write(ref cancelled, 1);
                start.Set();
                bool joined = true;
                for (int i = 0; i < 2; i++) if (started[i] && !threads[i].Join(TimeSpan.FromSeconds(10))) joined = false;
                if (!joined)
                {
                    // No subsequent fixture or live-worker DB cleanup. The
                    // dedicated SelfTest process can exit: both are background.
                    abandonCleanup = true;
                    Require("bounded workers completed; timeout retains only this offline fixture", false);
                }
                start.Dispose(); ready.Dispose();
                Require("both concurrent workers reached the bounded start barrier", readyInTime);
                if (launchError != null) throw new InvalidOperationException("Risk worker launch failed.", launchError);
                if (errors.Any(e => e != null)) throw new AggregateException(errors.Where(e => e != null));
                return results;
            }
            public void Dispose()
            {
                if (abandonCleanup) { Console.WriteLine("[INFO] auction market retained timed-out offline fixture: " + root); return; }
                foreach (var session in sessions) InventoryContext.Unregister(session);
                SqliteConnection.ClearAllPools();
                string full = Path.GetFullPath(root), boundary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".scratch")) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Risk fixture cleanup escaped its boundary.");
                if (Directory.Exists(full)) Directory.Delete(full, true);
            }
        }
    }
}
