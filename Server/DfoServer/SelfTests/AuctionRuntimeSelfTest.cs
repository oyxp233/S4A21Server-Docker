using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Auction;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Auction;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Auction;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // Composition-owner regression only: no listener, socket, started clock,
    // native eligibility bypass at a runtime entry, or fabricated receipt.
    public static class AuctionRuntimeSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            { Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] auction runtime: {name}"); if (condition) passed++; else failed++; }
            foreach (bool enableEntry in new[] { false, true })
            {
                try { VerifyFundedRuntime(enableEntry, Check); }
                catch (Exception ex) { Console.WriteLine(ex); failed++; }
            }
            try { VerifyEnabledCatalog(Check); }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            try { VerifyConfiguredPvfListing(Check); }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            try { VerifySendBoundary(Check); }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            Console.WriteLine($"AuctionRuntimeSelfTest: passed={passed} failed={failed}; offline owners only, no server or clock started.");
            return failed == 0 ? 0 : 1;
        }

        private static void VerifyFundedRuntime(bool enableEntry, Action<string, bool> check)
        {
            string label = enableEntry ? "catalog unavailable" : "entry disabled";
            int sellerId = enableEntry ? 80711 : 80701, buyerId = sellerId + 1;
            var seller = new AuctionParty(sellerId, sellerId, "卖家");
            var buyer = new AuctionParty(buyerId, buyerId, "买家");
            Guid sellerSession = Guid.NewGuid(), buyerSession = Guid.NewGuid();
            string root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var db = new GameDatabase(Path.Combine(root, "inventory.db"), ServerPaths.SchemaFilePath);
                void Sql(string sql) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
                long Scalar(string sql) => db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); });
                bool Refused(Action action)
                {
                    try { action(); return false; }
                    catch (InvalidOperationException ex) when (ex.Message == "Character has unresolved auction assets.") { return true; }
                }
                Sql($"INSERT INTO accounts(account_id,m_id,password_hash) VALUES({sellerId},'auction-runtime-a',''),({buyerId},'auction-runtime-b','');"
                    + $"INSERT INTO characters(character_id,account_id,name,level,slot_index) VALUES({sellerId},{sellerId},'卖家',85,0),({buyerId},{buyerId},'买家',85,0);");
                long time = 1792700000;
                int catalogLoads = 0;
                using var runtime = new AuctionRuntime(db, Path.Combine(root, "private.db"),
                    () => { catalogLoads++; throw new InvalidDataException("fixture unavailable catalog"); }, () => time, enableEntry);
                var repository = new SqliteCharacterRepository(db);
                repository.ConfigureDeletionGuard(runtime.DeletionGuard.TryExecuteWhenClear);
                var left = InventoryContext.Register(sellerSession, new InventoryService(sellerId, sellerId, db));
                var right = InventoryContext.Register(buyerSession, new InventoryService(buyerId, buyerId, db));
                left.Inventory.SetMainVirtualCount(0, 50000); right.Inventory.SetMainVirtualCount(0, 50000);
                // A real weapon fixture from the current PVF. Capture
                // this actual instance; the runtime's listing gate is not called.
                if (!InventoryRewardGrantService.TryCreateAndInsert(left.Inventory, 27602, ItemCreateReason.Unknown, 1, out var grant))
                    throw new InvalidOperationException("Cannot create established real mail weapon fixture.");
                var ownedCore = left.Inventory.GetItem(grant.ListType, grant.SlotIndex);
                ownedCore.Upgrade = 10; ownedCore.A21Tail_Unknown84 = 123456;
                left.Inventory.MarkDirty(grant.ListType, grant.SlotIndex);
                check(label + ": preexisting asset baseline persisted", InventoryPersistenceService.SaveDirty(left) && InventoryPersistenceService.SaveDirty(right));
                var codec = runtime.Codec;
                var source = codec.Capture(left.Inventory, grant.ListType, grant.SlotIndex);
                // RemoveItem legitimately Init()s the owned slot object. Only
                // the immutable captured instance can identify it after debit.
                var originalCore = codec.DecodeStrict(source);
                var transformer = new AuctionTransferableItemAdapter(codec);
                var planner = new AuctionMarketPlanner(codec, transformer.Transform);
                var quotes = new AuctionApprovedQuoteProvider((r, b, n) => new(null, 0, false));
                var rail = new AuctionPublicAssetCommit(db, codec, runtime.Gate);
                AuctionMarketPlan Plan(AuctionMarketRequest request, AuctionOrderSnapshot before,
                    AuctionItemEnvelope item = null)
                {
                    var planned = planner.Plan(request, before, item, quotes.Quote(request, before, time), time);
                    return planned.Plan ?? throw new InvalidOperationException("Fixture planning failed: " + planned.Reason);
                }
                AuctionOperationIntent Intent(AuctionMarketRequest request, AuctionMarketPlan plan, AuctionPublicAssetPlan assets)
                    => new(request.OperationId, "order:" + request.OrderId, request.Action, request.Actor.CharacterId,
                        request.Actor.AccountId, request.ExpectedVersion, time,
                        JsonSerializer.Serialize(new { publicAssetPlan = assets.Serialize(), orderTransition = plan.Transition.Serialize() }));
                long id = runtime.Store.AllocateOrderId();
                var listing = new AuctionMarketRequest(Guid.NewGuid(), AuctionOperationAction.List, id, 0, seller, 1, 0,
                    time + 86400, OpeningBidGold: 100, BuyoutGold: 1000, SourceList: grant.ListType, SourceSlot: grant.SlotIndex);
                var listingPlan = Plan(listing, null, source);
                var listingAssets = AuctionPublicAssetPlan.Create(codec, listingPlan.GoldDebit,
                    new[] { new AuctionItemDebit(grant.ListType, grant.SlotIndex, 1, source) }, Array.Empty<MailboxSendRequest>());
                var listingIntent = Intent(listing, listingPlan, listingAssets);
                runtime.Store.Prepare(listingIntent, listingPlan.Transition);
                var fundedListing = rail.Execute(listingIntent, listingAssets, left);
                if (fundedListing.State != AuctionAssetCommitState.Committed)
                    throw new InvalidOperationException("Fixture listing debit failed: " + fundedListing.Reason);
                runtime.Store.Complete(listingIntent, fundedListing.Receipt);
                var active = runtime.Store.FindOrder(id);
                check(label + ": real asset escrow is funded with its permanent public receipt", active.State == AuctionOrderState.Active
                    && left.Inventory.GetItem(grant.ListType, grant.SlotIndex) == null
                    && db.Read(c => AuctionPublicReceiptRepository.Find(c, null, listingIntent)) != null);
                check(label + ": actual simple deletion rejects active escrow", Refused(() => repository.SoftDelete(sellerId))
                    && Scalar($"SELECT delete_flag FROM characters WHERE character_id={sellerId};") == 0);

                var bid = new AuctionMarketRequest(Guid.NewGuid(), AuctionOperationAction.Bid, id, active.Version, buyer, 1, 110, active.ExpiresAtUnix);
                var bidPlan = Plan(bid, active);
                var bidAssets = AuctionPublicAssetPlan.Create(codec, bidPlan.GoldDebit, Array.Empty<AuctionItemDebit>(), Array.Empty<MailboxSendRequest>());
                var bidIntent = Intent(bid, bidPlan, bidAssets);
                runtime.Store.Prepare(bidIntent, bidPlan.Transition);
                var fundedBid = rail.Execute(bidIntent, bidAssets, right);
                check(label + ": crash fixture has a real debit but private Prepared", fundedBid.State == AuctionAssetCommitState.Committed
                    && runtime.Store.FindOperation(bid.OperationId).State == AuctionOperationState.Prepared
                    && right.Inventory.GetMainVirtualCount(0).Count == 49890);
                if (fundedBid.State != AuctionAssetCommitState.Committed) throw new InvalidOperationException("Fixture bid failed: " + fundedBid.Reason);
                check(label + ": actual compacting deletion rejects unresolved funded bid", Refused(() => repository.SoftDeleteAndCompactSlots(buyerId, buyerId, 0))
                    && Scalar($"SELECT delete_flag FROM characters WHERE character_id={buyerId};") == 0);
                InventoryContext.Unregister(sellerSession); InventoryContext.Unregister(buyerSession);
                time = active.ExpiresAtUnix;
                var recovered = runtime.Start(new ClockService()); // Deliberately never Start() the clock.
                var sold = runtime.Store.FindOrder(id);
                check(label + ": startup recovery resolves only private side before funded offline expiry", recovered.Completed == 1
                    && recovered.Pending == 0 && recovered.Quarantined == 0
                    && runtime.Store.FindOperation(bid.OperationId).State == AuctionOperationState.Committed
                    && sold.State == AuctionOrderState.Sold && sold.Version == 3 && sold.FundedBidGold == 0 && sold.RemainingQuantity == 0
                    && !InventoryContext.TryGetLease(sellerId, out _) && !InventoryContext.TryGetLease(buyerId, out _));
                check(label + ": unavailable new entry cannot disable existing funded obligations", !runtime.EntryEnabled
                    && catalogLoads == (enableEntry && !AuctionRuntime.RecoveryOnlyBuild ? 1 : 0));
                var history = runtime.TradeHistory.Read(new(buyerId, buyerId, null, 10));
                check(label + ": actual recovered funded expiry reaches the default independent history", history.Entries.Count == 1
                    && history.Entries[0].OrderId == id && history.Entries[0].GrossGold == 110 && history.Entries[0].FeeGold == 5
                    && history.Entries[0].ItemId == 27602 && !history.Entries[0].IsSeller
                    && File.Exists(Path.Combine(root, "auction-history.db")));
                runtime.SynchronizeHistory(); runtime.SynchronizeHistory();
                check(label + ": source replay keeps one sale without repeating public effects",
                    runtime.TradeHistory.Read(new(sellerId, sellerId, null, 10)).Entries.Count == 1
                    && Scalar("SELECT count(*) FROM mailbox_messages;") == 3
                    && Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name LIKE 'auction_asset:%';") == 3);
                string historyPath = Path.Combine(root, "auction-history.db");
                byte[] savedHistory = File.ReadAllBytes(historyPath);
                var saleEntry = runtime.Store.ReadHistorySource(new(0, null, null)).Single(x => x.Intent.Action == AuctionOperationAction.Expire);
                using (var historyConnection = new SqliteConnection("Data Source=" + historyPath + ";Pooling=False"))
                {
                    historyConnection.Open(); using var corruptHistory = historyConnection.CreateCommand();
                    corruptHistory.CommandText = "UPDATE history_sources SET intent_hash=@hash WHERE operation_id=@id;";
                    corruptHistory.Parameters.AddWithValue("@hash", new string('a', 64));
                    corruptHistory.Parameters.AddWithValue("@id", saleEntry.Intent.OperationId.ToString("N")); corruptHistory.ExecuteNonQuery();
                }
                runtime.PublishCommitted(new(AuctionMarketStatus.Committed, "", sold, saleEntry));
                check(label + ": history conflict cannot alter an already committed sale or its public effects",
                    runtime.Store.FindOperation(saleEntry.Intent.OperationId).State == AuctionOperationState.Committed
                    && Scalar("SELECT count(*) FROM mailbox_messages;") == 3);
                File.WriteAllBytes(historyPath, savedHistory);
                runtime.SynchronizeHistory();
                check(label + ": restored projection replays without duplicate delivery or history",
                    runtime.TradeHistory.Read(new(buyerId, buyerId, null, 10)).Entries.Count == 1
                    && Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name LIKE 'auction_asset:%';") == 3);
                bool doubleStartRefused = false;
                try { runtime.Start(new ClockService()); }
                catch (InvalidOperationException) { doubleStartRefused = true; }
                check(label + ": same runtime cannot schedule startup twice", doubleStartRefused);
                var sellerMessages = runtime.Store.DeliveryMessageIds(sellerId);
                var buyerMessages = runtime.Store.DeliveryMessageIds(buyerId);
                check(label + ": real expiry delivery legs have private IDs and public system origins", sellerMessages.Count == 2 && buyerMessages.Count == 1
                    && Scalar("SELECT count(*) FROM mailbox_messages WHERE mail_type=1 AND unlimited_flag=1 AND idempotency_key LIKE 'auction:%'"
                        + " AND sender_character_id=receiver_character_id AND sender_account_id=receiver_account_id;") == 3
                    && Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name LIKE 'auction_asset:%';") == 3
                    && runtime.Store.Pending(10).Count == 0);
                check(label + ": actual deletion still refuses delivered but unclaimed gold and item",
                    Refused(() => repository.SoftDelete(sellerId)) && Refused(() => repository.SoftDeleteAndCompactSlots(buyerId, buyerId, 0)));
                long itemMessage = buyerMessages.Single();
                long attachment = Scalar($"SELECT attachment_id FROM mailbox_attachments WHERE message_id={itemMessage};");
                Sql($"UPDATE mailbox_attachments SET claimed_flag=2 WHERE attachment_id={attachment};");
                check(label + ": held claim is still a deletion liability", Refused(() => repository.SoftDelete(buyerId)));
                Sql($"UPDATE mailbox_attachments SET claimed_flag=0 WHERE attachment_id={attachment};");
                left = InventoryContext.Register(sellerSession, db.Read(c => InventoryService.LoadFromDb(c, sellerId, sellerId, db)));
                right = InventoryContext.Register(buyerSession, db.Read(c => InventoryService.LoadFromDb(c, buyerId, buyerId, db)));
                check(label + ": recovery never re-debits bidder or restores escrow source", right.Inventory.GetMainVirtualCount(0).Count == 49890
                    && left.Inventory.GetMainVirtualCount(0).Count == 40000 && left.Inventory.GetItem(grant.ListType, grant.SlotIndex) == null);
                var mailbox = new MailboxRepository(db);
                byte[] mailCore = db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT item_core FROM mailbox_attachments WHERE attachment_id={attachment};"; return (byte[])cmd.ExecuteScalar(); });
                void SetCore(byte[] bytes) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                    cmd.CommandText = "UPDATE mailbox_attachments SET item_core=@core WHERE attachment_id=@id;";
                    cmd.Parameters.AddWithValue("@core", (object)bytes ?? DBNull.Value); cmd.Parameters.AddWithValue("@id", attachment); cmd.ExecuteNonQuery(); });
                // NULL is legal legacy storage, unlike a short BLOB (schema
                // rejects short BLOBs). System origin must still forbid fallback.
                SetCore(null);
                var corrupt = mailbox.ClaimMail(buyerId, MailboxRepository.AttachmentClaimFlag + attachment, right);
                check(label + ": runtime system origin forbids missing99B fallback without a detail marker", !corrupt.Success
                    && right.Inventory.GetItems(InventoryListType.Main).All(p => p.Value.ItemId != originalCore.ItemId)
                    && Scalar($"SELECT claimed_flag FROM mailbox_attachments WHERE attachment_id={attachment};") == 0);
                SetCore(mailCore);
                var itemClaim = mailbox.ClaimMail(buyerId, MailboxRepository.AttachmentClaimFlag + attachment, right);
                if (!itemClaim.Success) throw new InvalidOperationException("Actual runtime item claim failed: " + itemClaim.Error);
                var claimedRows = right.Inventory.GetItems(InventoryListType.Main)
                    .Where(p => p.Value.ItemId == originalCore.ItemId).ToArray();
                var claimed = claimedRows.Length == 1
                    ? codec.Capture(right.Inventory, InventoryListType.Main, claimedRows[0].Key) : null;
                if (claimed == null || claimed.ContentHashHex != source.ContentHashHex)
                {
                    var offsets = claimed == null ? Array.Empty<int>() : Enumerable.Range(0, ItemCore.Size)
                        .Where(i => source.NativeCore99[i] != claimed.NativeCore99[i]).ToArray();
                    Console.WriteLine("AuctionRuntime claim diagnostics: itemRows=" + claimedRows.Length
                        + "; changedNativeOffsets=" + string.Join(",", offsets)
                        + "; detailEqual=" + (claimed?.DetailJson == source.DetailJson)
                        + "; extensionsEqual=" + (claimed?.ExtensionsJson == source.ExtensionsJson));
                }
                check(label + ": actual item claim preserves exact native and private state", itemClaim.Success
                    && claimedRows.Length == 1 && claimed.ContentHashHex == source.ContentHashHex);
                foreach (long message in sellerMessages)
                    check(label + ": actual gold claim " + message, mailbox.ClaimMail(sellerId, message, left).Success);
                long fee = AuctionEconomyPolicy.CumulativeFee(110, sold.FeeRatePercent);
                check(label + ": final inventories conserve funded gold less approved fee", left.Inventory.GetMainVirtualCount(0).Count
                    + right.Inventory.GetMainVirtualCount(0).Count == 100000 - fee);
                check(label + ": consumed mail no longer blocks the guard", runtime.DeletionGuard.TryExecuteWhenClear(sellerId, () => { })
                    && runtime.DeletionGuard.TryExecuteWhenClear(buyerId, () => { }));
                repository.SoftDelete(sellerId);
                repository.SoftDeleteAndCompactSlots(buyerId, buyerId, 0);
                check(label + ": both real repository deletion entrypoints permit fully resolved assets",
                    Scalar($"SELECT count(*) FROM characters WHERE character_id IN ({sellerId},{buyerId}) AND delete_flag=1;") == 2);
            }
            finally
            {
                InventoryContext.Unregister(sellerSession); InventoryContext.Unregister(buyerSession);
                SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
            }
        }

        private static void VerifyConfiguredPvfListing(Action<string, bool> check)
        {
            if (AuctionRuntime.RecoveryOnlyBuild) return;
            string root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-current-pvf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var session = Guid.NewGuid();
            try
            {
                const int seller = 80731;
                var db = new GameDatabase(Path.Combine(root, "inventory.db"), ServerPaths.SchemaFilePath);
                db.Write((c,t) => {
                    using var command = c.CreateCommand(); command.Transaction=t;
                    command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(80731,'auction-current-pvf','');"
                        + "INSERT INTO characters(character_id,account_id,name,level) VALUES(80731,80731,'拍卖测试',85);";
                    command.ExecuteNonQuery();
                });
                var lease = InventoryContext.Register(session, new InventoryService(seller,seller,db));
                lease.Inventory.SetMainVirtualCount(0,50000);
                using var runtime = new AuctionRuntime(db,Path.Combine(root,"auction.db"),ServerRuntimeBuilder.LoadAuctionCatalog,
                    () => 1792700000,entryEnabled:true);
                runtime.Start(new ClockService());
                var actor = new AuctionParty(seller,seller,"拍卖测试");
                // These are real current-PVF templates already used by transfer
                // tests. The production gate is used without fixture authority.
                foreach (int id in new[] { 27602,3028 })
                {
                    if (!InventoryRewardGrantService.TryCreateAndInsert(lease.Inventory,id,ItemCreateReason.Unknown,1,out var grant))
                        throw new InvalidOperationException("Cannot create current-PVF listing fixture " + id);
                    var core = lease.Inventory.GetItem(grant.ListType,grant.SlotIndex);
                    if (runtime.Catalog.TryGet(id,out var template)
                        && template.AttachType.Contains("seal",StringComparison.OrdinalIgnoreCase)) core.SealFlag=1;
                    lease.Inventory.MarkDirty(grant.ListType,grant.SlotIndex);
                    if (!InventoryPersistenceService.SaveDirty(lease)) throw new InvalidOperationException("Cannot save fixture baseline.");
                    var source = runtime.Codec.Capture(lease.Inventory,grant.ListType,grant.SlotIndex);
                    int wallet = lease.Inventory.GetMainVirtualCount(0).Count;
                    var request = new AuctionMarketRequest(Guid.NewGuid(),AuctionOperationAction.List,0,0,actor,1,0,0,
                        BuyoutGold:100,SourceList:grant.ListType,SourceSlot:grant.SlotIndex,DurationHours:24);
                    var listed = runtime.Application.Execute(request,lease);
                    check("current PVF production listing commits once " + id,listed.Status==AuctionMarketStatus.Committed);
                    if (listed.Status!=AuctionMarketStatus.Committed)
                        throw new InvalidOperationException("Production listing rejected: " + listed.Reason);
                    var stored = runtime.Store.FindOrder(listed.Order.OrderId);
                    check("escrow retains complete source for current PVF " + id,
                        stored.SourceEnvelopeJson==runtime.Codec.Serialize(source)
                        && runtime.Codec.DeserializeStrict(stored.EscrowEnvelopeJson).ContentHashHex==source.ContentHashHex
                        && lease.Inventory.GetItem(grant.ListType,grant.SlotIndex)?.IsEmpty != false
                        && lease.Inventory.GetMainVirtualCount(0).Count==wallet-10000);
                    var replay = runtime.Application.Resume(listed.Operation.Intent.OperationId);
                    check("replaying committed current-PVF listing does not debit again " + id,
                        replay.Status==AuctionMarketStatus.Committed && lease.Inventory.GetMainVirtualCount(0).Count==wallet-10000);
                    var cancel = runtime.Application.Execute(new(Guid.NewGuid(),AuctionOperationAction.Cancel,
                        stored.OrderId,stored.Version,actor,0,0,0),lease);
                    check("current-PVF cancellation keeps original item and existing deposit policy " + id,
                        cancel.Status==AuctionMarketStatus.Committed && cancel.Order.State==AuctionOrderState.Cancelled
                        && lease.Inventory.GetMainVirtualCount(0).Count==wallet-10000);
                }
            }
            finally
            {
                InventoryContext.Unregister(session); SqliteConnection.ClearAllPools(); Directory.Delete(root,true);
            }
        }

        private static void VerifyEnabledCatalog(Action<string, bool> check)
        {
            string root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-runtime-ready-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var db = new GameDatabase(Path.Combine(root, "inventory.db"), ServerPaths.SchemaFilePath);
                var files = new Dictionary<string, string> {
                    ["equipment/equipment.lst"] = "101 `fixture.equ`",
                    ["equipment/fixture.equ"] = "[name]\n`fixture sword`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n",
                    ["stackable/stackable.lst"] = "3001 `fixture.stk`",
                    ["stackable/fixture.stk"] = "[name]\n`fixture material`\n[stackable type]\n`[material]` 0\n[attach type]\n`[free]`\n"
                };
                int catalogLoads = 0, indexReads = 0, modelReads = 0;
                AuctionTemplateCatalog Catalog()
                {
                    catalogLoads++;
                    return AuctionTemplateCatalog.LoadIndexed(path => {
                        if (path.EndsWith(".lst", StringComparison.Ordinal)) indexReads++; else modelReads++;
                        return files.TryGetValue(path, out var text) ? text : throw new InvalidDataException("Unknown runtime fixture resource: " + path);
                    }, new string('a', 64));
                }
                using var runtime = new AuctionRuntime(db, Path.Combine(root, "private.db"), Catalog, () => 1792700000, entryEnabled: true);
                check("requested enabled entry waits for runtime readiness", !runtime.EntryEnabled && catalogLoads == 0);
                var recovered = runtime.Start(new ClockService()); // No Clock.Start, listener or session.
                bool recoveryOnly = AuctionRuntime.RecoveryOnlyBuild;
                check("compile-time recovery owner cannot be reopened by requested enabled entry", runtime.EntryEnabled == !recoveryOnly
                    && catalogLoads == (recoveryOnly ? 0 : 1) && indexReads == (recoveryOnly ? 0 : 2) && modelReads == 0
                    && recovered.Completed == 0 && recovered.Aborted == 0 && recovered.Quarantined == 0 && recovered.Pending == 0);
                if (!recoveryOnly)
                {
                    check("normal ready owner retains a real lazily indexed catalog", runtime.Catalog.TryGet(101, out var weapon)
                        && weapon.DisplayName == "fixture sword" && catalogLoads == 1 && indexReads == 2 && modelReads == 1
                        && runtime.Catalog.Templates.Count == 2);
                }
                else
                {
                    check("recovery-only owner never samples catalog despite enabled request", !runtime.EntryEnabled && catalogLoads == 0
                        && indexReads == 0 && modelReads == 0);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
            }
        }

        private static void VerifySendBoundary(Action<string, bool> check)
        {
            const long now = 1792700000;
            var principal = new AuctionQueryPrincipal(Guid.NewGuid(), 1, 80722, 80722);
            var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
            var envelope = AuctionItemEnvelope.Create(
                new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3001, Count = 1 }.ToBytes(), "", "{}");
            var row = new AuctionQueryRow(1, 1, 3001, 80721, 80721, "卖家", null, null, 1, null, 100, 100,
                "fixture", 1, 2, 0, 0, now - 10, now + 1000, codec.Serialize(envelope), envelope.ContentHashHex);
            var snapshot = new AuctionQuerySnapshot(principal, "runtime-send-fixture", new string('a', 64), now, now + 120, ImmutableArray.Create(row));
            var observations = new AuctionNativeObservations(principal, codec);
            var packets = new[] { new byte[] { 1 }, new byte[] { 2 } };
            using var sendLock = new SemaphoreSlim(1, 1);
            int sent = 0, callbacks = 0; bool lockHeld = false, observedBeforeCompletion = false;
            bool Has() => observations.TryGetObservedOrderSnapshot(row.OrderId, now, out _, out _);
            void OnSent()
            {
                callbacks++; lockHeld = sendLock.CurrentCount == 0 && sent == packets.Length;
                if (!observations.RecordSentRows(AuctionQueryView.Search, snapshot, new[] { row }, now, out var reason))
                    throw new InvalidOperationException(reason);
            }
            bool writeFailure = false;
            try
            {
                EnhancedClientSession.SendPreparedPacketBatchCoreAsync(sendLock, () => packets, packet => {
                    observedBeforeCompletion |= Has();
                    if (++sent == 2) throw new IOException("fixture second packet write failed");
                    return Task.CompletedTask;
                }, onSent: OnSent).GetAwaiter().GetResult();
            }
            catch (IOException) { writeFailure = true; }
            check("failed batch prefix cannot create actionable observations", writeFailure && sent == 2 && callbacks == 0
                && !Has() && !observedBeforeCompletion && sendLock.CurrentCount == 1);
            sent = 0;
            EnhancedClientSession.SendPreparedPacketBatchCoreAsync(sendLock, () => packets, packet => {
                observedBeforeCompletion |= Has(); sent++; return Task.CompletedTask;
            }, onSent: OnSent).GetAwaiter().GetResult();
            check("complete batch publishes observations exactly once inside send lock", sent == 2 && callbacks == 1
                && lockHeld && Has() && !observedBeforeCompletion && sendLock.CurrentCount == 1);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel(); int cancelledCallbacks = 0;
            bool cancellationObserved = false;
            try
            {
                EnhancedClientSession.SendPreparedPacketBatchCoreAsync(sendLock, () => packets, packet => Task.CompletedTask,
                    cancelled.Token, () => cancelledCallbacks++).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancellationObserved = true; }
            check("cancelled batch never invokes sent callback", cancellationObserved && cancelledCallbacks == 0 && sendLock.CurrentCount == 1);
        }
    }
}
