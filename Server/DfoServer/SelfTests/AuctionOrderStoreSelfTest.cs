using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;

namespace DfoServer.SelfTests
{
    public static class AuctionOrderStoreSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool value) { Console.WriteLine($"[{(value ? "PASS" : "FAIL")}] auction store: {name}"); if (value) passed++; else failed++; }
            bool Rejects(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } }
            var root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Guid sellerSession = Guid.NewGuid(), buyerSession = Guid.NewGuid();
            const int sellerId = 79201, buyerId = 79202;
            const long now = 1792700000;
            try
            {
                var db = new GameDatabase(Path.Combine(root, "public.db"), ServerPaths.SchemaFilePath);
                db.Write((c, t) => {
                    using var cmd = c.CreateCommand(); cmd.Transaction = t;
                    cmd.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(79201,'auction-store-s',''),(79202,'auction-store-b','');"
                        + "INSERT INTO characters(character_id,account_id,name,level) VALUES(79201,79201,'auction-store-s',85),(79202,79202,'auction-store-b',85);";
                    cmd.ExecuteNonQuery();
                });
                var seller = new AuctionParty(sellerId, sellerId, "auction-store-s");
                var buyer = new AuctionParty(buyerId, buyerId, "auction-store-b");
                var lease = InventoryContext.Register(sellerSession, new InventoryService(sellerId, sellerId, db));
                var buyerLease = InventoryContext.Register(buyerSession, new InventoryService(buyerId, buyerId, db));
                lease.Inventory.SetMainVirtualCount(0, 20000); buyerLease.Inventory.SetMainVirtualCount(0, 1000);
                lease.Inventory.SetItem(InventoryListType.Main, 65, new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3001, Count = 10 });
                Check("fixture baseline saved", InventoryPersistenceService.SaveDirty(lease) && InventoryPersistenceService.SaveDirty(buyerLease));
                var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
                var store = new AuctionOrderStore(Path.Combine(root, "private.db"), codec, allowFixturePolicies: true);
                var gate = new object(); var rail = new AuctionPublicAssetCommit(db, codec, gate);
                var deletionGuard = new AuctionCharacterLiabilityGuard(store, db, gate);
                int deletionCalls = 0;
                Check("clear character reaches guarded mutation", deletionGuard.TryExecuteWhenClear(buyerId, () => deletionCalls++) && deletionCalls == 1);
                var source = codec.Capture(lease.Inventory, InventoryListType.Main, 65);
                AuctionItemEnvelope Quantity(AuctionItemEnvelope from, int count) {
                    var core = codec.DecodeStrict(from); core.Count = count;
                    return codec.CreateTransferSnapshot(from, core);
                }
                long orderId = store.AllocateOrderId();
                var escrow = Quantity(source, 3);
                var active = new AuctionOrderSnapshot(orderId, 1, AuctionOrderState.Active, seller,
                    codec.Serialize(source), codec.Serialize(escrow), 3, 3, null, 300, null, 0, 10000,
                    now, now + 86400, null, "account:" + sellerId, "fixture-store-v1", 5, 0, 0, 100);
                AuctionVerifiedQuote Quote(AuctionOperationAction action, AuctionParty actor, long version, int qty, long offered,
                    long gross = 0, long deposit = 0, long fee = 0, long forfeit = 0)
                    => new(AuctionQuoteAvailability.FixtureOnly, "explicit-fixture", now + 60, gross, 0, deposit, fee, 0, forfeit,
                        active.QuotaKey, 5, new(action, orderId, version, actor.CharacterId, actor.AccountId, qty, offered, active.ExpiresAtUnix, "fixture-request"),
                        active.PolicyVersion, 5, 100);
                AuctionOperationIntent Intent(Guid id, AuctionOperationAction action, AuctionParty actor, long version,
                    AuctionOrderTransition transition, AuctionPublicAssetPlan plan)
                    => new(id, "order:" + orderId, action, actor.CharacterId, actor.AccountId, version, now,
                        JsonSerializer.Serialize(new { publicAssetPlan = plan.Serialize(), orderTransition = transition.Serialize() }));
                var listPlan = AuctionPublicAssetPlan.Create(codec, 10000,
                    new[] { new AuctionItemDebit(InventoryListType.Main, 65, 3, source) }, Array.Empty<MailboxSendRequest>());
                var listTransition = new AuctionOrderTransition(null, active, Quote(AuctionOperationAction.List, seller, 0, 3, 0, deposit:10000), Array.Empty<AuctionDeliveryLeg>());
                var list = Intent(Guid.NewGuid(), AuctionOperationAction.List, seller, 0, listTransition, listPlan);
                var productionStore = new AuctionOrderStore(Path.Combine(root, "private.db"), codec);
                Check("production store rejects fixture quotes without explicit test construction", Rejects(() => productionStore.Prepare(list, listTransition))
                    && productionStore.FindOrder(orderId) == null);
                Check("descriptor roundtrip is canonical", AuctionOrderTransition.DeserializeStrict(listTransition.Serialize()).Serialize() == listTransition.Serialize());
                var substituted = codec.DecodeStrict(escrow); substituted.Durability = 77;
                var forgedActive = active with { EscrowEnvelopeJson = codec.Serialize(codec.CreateTransferSnapshot(escrow, substituted)) };
                var forgedTransition = new AuctionOrderTransition(null, forgedActive, listTransition.Quote, Array.Empty<AuctionDeliveryLeg>());
                var forged = Intent(Guid.NewGuid(), AuctionOperationAction.List, seller, 0, forgedTransition, listPlan);
                Check("same-template native instance substitution rejected before reservation", Rejects(() => store.Prepare(forged, forgedTransition))
                    && store.FindOrder(orderId) == null && store.FindOperation(forged.OperationId) == null);
                Check("prepare creates unfunded reservation", store.Prepare(list, listTransition).Created
                    && store.FindOrder(orderId).State == AuctionOrderState.Funding && store.FindOrder(orderId).FundedDepositGold == 0);
                Check("pending listing blocks character deletion", !deletionGuard.TryExecuteWhenClear(sellerId, () => deletionCalls++) && deletionCalls == 1);
                Check("unfunded private reservation is absent from public queries", store.ReadQueryCandidates(
                    AuctionOrderReadScope.PublicActive, buyerId, buyerId, now).Orders.Count == 0);
                Check("replay does not reserve twice", !store.Prepare(list, listTransition).Created && store.Pending(10).Count == 1);
                var listReceipt = rail.Execute(list, listPlan, lease);
                Check("public commit precedes funded publication", listReceipt.State == AuctionAssetCommitState.Committed
                    && store.FindOrder(orderId).State == AuctionOrderState.Funding && lease.Inventory.GetItem(InventoryListType.Main, 65).Count == 7);
                // Reopen simulates losing the private completion after public commit.
                store = new AuctionOrderStore(Path.Combine(root, "private.db"), codec, allowFixturePolicies: true);
                var recovery = new AuctionOrderRecovery(store, db, rail, gate);
                var recovered = recovery.RecoverPendingAfterExecutorsQuiesced(10);
                Check("receipt completes recovered order", recovered.Completed == 1 && recovered.Aborted == 0
                    && store.FindOrder(orderId) == active && store.Pending(10).Count == 0);
                var frozenRead = store.ReadQueryCandidates(AuctionOrderReadScope.PublicActive, buyerId, buyerId, now);
                Check("query publishes the exact funded validated order", frozenRead.Available && frozenRead.Orders.Single() == active);
                Check("seller query cannot enumerate another character", store.ReadQueryCandidates(
                    AuctionOrderReadScope.SellerActive, buyerId, buyerId, now).Orders.Count == 0
                    && store.ReadQueryCandidates(AuctionOrderReadScope.SellerActive, sellerId, sellerId, now).Orders.Single() == active);
                Check("no-bid and expired orders are not current buyer offers", store.ReadQueryCandidates(
                    AuctionOrderReadScope.CurrentHighestBidderActive, buyerId, buyerId, now).Orders.Count == 0
                    && store.ReadQueryCandidates(AuctionOrderReadScope.PublicActive, buyerId, buyerId, active.ExpiresAtUnix).Orders.Count == 0);
                Check("active account quota and absolute expiry projection", store.CountActiveAndReservedAccountListings(sellerId) == 1
                    && store.FindDue(active.ExpiresAtUnix - 1, 0, 10).Count == 0 && store.FindDue(active.ExpiresAtUnix, 0, 10).Single() == active);
                Check("duplicate completion is idempotent", store.Complete(list, listReceipt.Receipt) == active);
                Check("inconsistent terminal receipt rejected", Rejects(() => store.Complete(list, listReceipt.Receipt with { ResultJson = "{}" })));
                var stale = Intent(Guid.NewGuid(), AuctionOperationAction.List, seller, 0, listTransition, listPlan);
                Check("second operation cannot relist same identity", Rejects(() => store.Prepare(stale, listTransition)));

                var pieceId = Guid.NewGuid(); var delivered = Quantity(escrow, 1); var remaining = Quantity(escrow, 2);
                var afterPiece = active with { Version = 2, RemainingQuantity = 2, EscrowEnvelopeJson = codec.Serialize(remaining),
                    BuyoutGold = 200, CumulativeGrossGold = 100, CumulativeFeeGold = 5 };
                var legs = new[] {
                    new AuctionDeliveryLeg("item", AuctionDeliveryKind.ItemDelivery, buyer, 0, codec.Serialize(delivered), 1),
                    new AuctionDeliveryLeg("seller", AuctionDeliveryKind.SellerProceeds, seller, 95, null, 0) };
                MailboxSendRequest Mail(AuctionDeliveryLeg leg) => new() {
                    SenderCharacterId = sellerId, SenderAccountId = sellerId, SenderName = seller.FrozenName,
                    ReceiverCharacterId = leg.Recipient.CharacterId, ReceiverAccountId = leg.Recipient.AccountId,
                    ReceiverName = leg.Recipient.FrozenName, Gold = (int)leg.Gold, Text = "fixture auction settlement",
                    Unlimited = true, IdempotencyKey = "auction:" + pieceId.ToString("N") + ":" + leg.LegKey,
                    Attachments = leg.ItemEnvelopeJson == null ? Array.Empty<MailboxSendAttachmentRequest>()
                        : new[] { new MailboxSendAttachmentRequest { ItemId = 3001, ItemCount = leg.Quantity,
                            ItemCoreData = delivered.NativeCore99, DetailJson = delivered.DetailJson, ExtraJson = delivered.ExtensionsJson } } };
                var piecePlan = AuctionPublicAssetPlan.Create(codec, 100, Array.Empty<AuctionItemDebit>(), legs.Select(Mail).ToArray());
                var pieceTransition = new AuctionOrderTransition(active, afterPiece, Quote(AuctionOperationAction.BuyPiece, buyer, 1, 1, 100, gross:100, fee:5), legs);
                var piece = Intent(pieceId, AuctionOperationAction.BuyPiece, buyer, 1, pieceTransition, piecePlan);
                var misdirectedLegs = new[] { legs[0], legs[1] with { Recipient = buyer } };
                var misdirectedTransition = new AuctionOrderTransition(active, afterPiece, pieceTransition.Quote, misdirectedLegs);
                var misdirectedPlan = AuctionPublicAssetPlan.Create(codec, 100, Array.Empty<AuctionItemDebit>(), misdirectedLegs.Select(Mail).ToArray());
                var misdirected = Intent(pieceId, AuctionOperationAction.BuyPiece, buyer, 1, misdirectedTransition, misdirectedPlan);
                Check("conserved money cannot redirect seller proceeds to buyer", Rejects(() => store.Prepare(misdirected, misdirectedTransition))
                    && store.FindOperation(pieceId) == null);
                var duplicateLegs = new[] { legs[0], legs[0] with { LegKey = "item-second" }, legs[1] };
                var duplicateTransition = new AuctionOrderTransition(active, afterPiece with { RemainingQuantity = 1,
                    EscrowEnvelopeJson = codec.Serialize(delivered), BuyoutGold = 100 }, pieceTransition.Quote, duplicateLegs);
                var duplicatePlan = AuctionPublicAssetPlan.Create(codec, 100, Array.Empty<AuctionItemDebit>(), duplicateLegs.Select(Mail).ToArray());
                var duplicate = Intent(pieceId, AuctionOperationAction.BuyPiece, buyer, 1, duplicateTransition, duplicatePlan);
                Check("duplicate item delivery rejected before public commit", Rejects(() => store.Prepare(duplicate, duplicateTransition))
                    && store.FindOperation(pieceId) == null);
                Check("partial prepare retains old funded state", store.Prepare(piece, pieceTransition).Created
                    && store.FindOrder(orderId).RemainingQuantity == 3);
                Check("pending partial sale is hidden rather than published twice", store.ReadQueryCandidates(
                    AuctionOrderReadScope.PublicActive, buyerId, buyerId, now).Orders.Count == 0);
                var competitor = Intent(Guid.NewGuid(), AuctionOperationAction.BuyPiece, buyer, 1, pieceTransition, piecePlan);
                Check("pending order blocks concurrent buyer", Rejects(() => store.Prepare(competitor, pieceTransition)));
                var pieceReceipt = rail.Execute(piece, piecePlan, buyerLease);
                Check("partial purchase settles exactly one escrow item", pieceReceipt.State == AuctionAssetCommitState.Committed
                    && store.Complete(piece, pieceReceipt.Receipt) == afterPiece && buyerLease.Inventory.GetMainVirtualCount(0).Count == 900);
                Check("new read sees partial sale while prior snapshot keeps its frozen quantity/version", store.ReadQueryCandidates(
                    AuctionOrderReadScope.PublicActive, buyerId, buyerId, now).Orders.Single() == afterPiece
                    && frozenRead.Orders.Single() == active && frozenRead.Orders.Single().RemainingQuantity == 3);
                Check("unclaimed actual auction item mail blocks buyer deletion", !deletionGuard.TryExecuteWhenClear(buyerId, () => deletionCalls++) && deletionCalls == 1);
                // Fixture only: model both reserved and completed public claim states.
                db.Write((c,t) => { using var cmd=c.CreateCommand(); cmd.Transaction=t;
                    cmd.CommandText="UPDATE mailbox_attachments SET claimed_flag=2;"; cmd.ExecuteNonQuery(); });
                Check("reserved claim remains a liability", !deletionGuard.TryExecuteWhenClear(buyerId, () => deletionCalls++) && deletionCalls == 1);
                db.Write((c,t) => { using var cmd=c.CreateCommand(); cmd.Transaction=t;
                    cmd.CommandText="UPDATE mailbox_attachments SET claimed_flag=1;"; cmd.ExecuteNonQuery(); });
                Check("claimed history alone does not prevent deletion", deletionGuard.TryExecuteWhenClear(buyerId, () => deletionCalls++) && deletionCalls == 2);
                Check("old completion returns old result without reverting current order", store.Complete(list, listReceipt.Receipt) == active && store.FindOrder(orderId) == afterPiece);
                Check("stale expected version cannot overwrite partial sale", Rejects(() => store.Prepare(competitor, pieceTransition)));

                // Aborting a verified-uncommitted no-op reservation releases only that operation.
                var later = afterPiece with { Version = 3 };
                var noopPlan = AuctionPublicAssetPlan.Create(codec, 0, Array.Empty<AuctionItemDebit>(), Array.Empty<MailboxSendRequest>());
                var noopTransition = new AuctionOrderTransition(afterPiece, later, Quote(AuctionOperationAction.Bid, buyer, 2, 0, 0), Array.Empty<AuctionDeliveryLeg>());
                var aborted = Intent(Guid.NewGuid(), AuctionOperationAction.Bid, buyer, 2, noopTransition, noopPlan);
                store.Prepare(aborted, noopTransition);
                var absentRecovery = recovery.RecoverPendingAfterExecutorsQuiesced(10);
                Check("known absence restores exact prior escrow", store.FindOrder(orderId) == afterPiece && store.FindOperation(aborted.OperationId).State == AuctionOperationState.Aborted);
                Check("quiescent absent recovery releases only unfunded operation", absentRecovery.Aborted == 1 && absentRecovery.Completed == 0);
                var held = Intent(Guid.NewGuid(), AuctionOperationAction.Bid, buyer, 2, noopTransition, noopPlan);
                store.Prepare(held, noopTransition); store.Quarantine(held, "Fixture conflicting evidence");
                Check("quarantine retains pending order liability", store.FindOrder(orderId).PendingOperationId == held.OperationId
                    && store.FindOperation(held.OperationId).State == AuctionOperationState.Quarantined);
                Check("quarantine cannot be silently aborted", Rejects(() => store.AbortVerifiedUncommitted(held, "unsafe release"))
                    && store.FindOrder(orderId).PendingOperationId == held.OperationId);
                Check("sequence allocates distinct positive IDs", store.AllocateOrderId() > orderId);
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            finally
            {
                InventoryContext.Unregister(sellerSession); InventoryContext.Unregister(buyerSession);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var resolved = Path.GetFullPath(root);
                var expectedParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".scratch")) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("auction-store-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Fixture cleanup outside its owned directory.");
                Directory.Delete(resolved, true);
            }
            Console.WriteLine($"Auction order store: {passed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
