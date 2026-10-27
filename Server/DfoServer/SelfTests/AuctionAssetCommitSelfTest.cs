using System;
using System.IO;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class AuctionAssetCommitSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            { Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] auction assets: {name}"); if (condition) passed++; else failed++; }
            bool Rejects(Action action)
            {
                try { action(); return false; }
                catch (ArgumentException) { return true; }
                catch (InvalidOperationException) { return true; }
                catch (JsonException) { return true; }
            }
            const int actor = 78101, receiver = 78102;
            var session = Guid.NewGuid();
            var root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-assets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var database = new GameDatabase(Path.Combine(root, "inventory.db"), ServerPaths.SchemaFilePath);
                database.Write((c, t) => {
                    using var command = c.CreateCommand(); command.Transaction = t;
                    command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(78101,'auction-assets-a',''),(78102,'auction-assets-b','');"
                        + "INSERT INTO characters(character_id,account_id,name,level) VALUES(78101,78101,'auction-assets-a',85),(78102,78102,'auction-assets-b',85);";
                    command.ExecuteNonQuery();
                });
                var lease = InventoryContext.Register(session, new InventoryService(actor, actor, database));
                InventoryLease Current()
                {
                    if (!InventoryContext.TryGetLease(actor, out var current)) throw new InvalidOperationException("Fixture lease missing.");
                    return current;
                }
                int Gold() => Current().Inventory.GetMainVirtualCount(0).Count;
                int Count(short slot) => Current().Inventory.GetItem(InventoryListType.Main, slot)?.Count ?? 0;
                long Scalar(string sql) => database.Read(c => { using var command = c.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); });
                var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
                lease.Inventory.SetMainVirtualCount(0, 1000);
                lease.Inventory.SetItem(InventoryListType.Main, 65, new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3001, Count = 10 });
                lease.Inventory.SetItem(InventoryListType.Main, 9, new ItemCore { ItemKind = ItemCore.KindEquipment, ItemId = 27602, Value = 123456,
                    Upgrade = 7 });
                Check("fixture persisted", InventoryPersistenceService.SaveDirty(lease));
                var gate = new object();
                var rail = new AuctionPublicAssetCommit(database, codec, gate);
                AuctionOperationIntent Intent(Guid id, AuctionPublicAssetPlan plan)
                    => new(id, "test:" + id.ToString("N"), AuctionOperationAction.List, actor, actor, 0, 1792700000,
                        JsonSerializer.Serialize(new { publicAssetPlan = plan.Serialize() }));
                MailboxSendRequest Mail(Guid id, int gold = 20) => new() {
                    SenderCharacterId = receiver, SenderAccountId = receiver, SenderName = "frozen-system",
                    ReceiverCharacterId = receiver, ReceiverAccountId = receiver, ReceiverName = "frozen-receiver",
                    Gold = gold, Text = "frozen auction leg", Unlimited = true, IdempotencyKey = "auction:" + id.ToString("N") + ":refund" };

                var id = Guid.NewGuid();
                var message = Mail(id);
                var debit = new AuctionItemDebit(InventoryListType.Main, 65, 3, codec.Capture(Current().Inventory, InventoryListType.Main, 65));
                var originalDebits = new[] { debit };
                var originalMails = new[] { message };
                var plan = AuctionPublicAssetPlan.Create(codec, 100, originalDebits, originalMails);
                var serialized = plan.Serialize();
                originalDebits[0] = debit with { Count = 9 }; originalMails[0] = Mail(Guid.NewGuid(), 999); message.Gold = 999;
                var copiedMessages = plan.CopySystemMails(); copiedMessages[0].Gold = 888;
                Check("mail and source collections are deeply frozen", plan.ItemDebits[0].Count == 3 && plan.CopySystemMails()[0].Gold == 20);
                Check("canonical plan restores strict complete source", AuctionPublicAssetPlan.DeserializeStrict(codec, serialized).Serialize() == serialized);
                Check("cash lower bound rejected", Rejects(() => AuctionPublicAssetPlan.Create(codec, -1, Array.Empty<AuctionItemDebit>(), Array.Empty<MailboxSendRequest>())));
                Check("cash upper bound rejected without truncation", Rejects(() => AuctionPublicAssetPlan.Create(codec, (long)int.MaxValue + 1, Array.Empty<AuctionItemDebit>(), Array.Empty<MailboxSendRequest>())));
                Check("duplicate source slot rejected", Rejects(() => AuctionPublicAssetPlan.Create(codec, 0, new[] { debit, debit }, Array.Empty<MailboxSendRequest>())));
                var finite = Mail(id); finite.Unlimited = false;
                Check("finite system debt mail rejected", Rejects(() => AuctionPublicAssetPlan.Create(codec, 0, Array.Empty<AuctionItemDebit>(), new[] { finite })));
                var timed = Mail(id); timed.ExpireAtUtc = DateTimeOffset.UtcNow.AddDays(1);
                Check("implicit expiry cannot enter frozen debt", Rejects(() => AuctionPublicAssetPlan.Create(codec, 0, Array.Empty<AuctionItemDebit>(), new[] { timed })));
                Check("duplicate mail leg key rejected", Rejects(() => AuctionPublicAssetPlan.Create(codec, 0, Array.Empty<AuctionItemDebit>(), new[] { Mail(id), Mail(id) })));

                var intent = Intent(id, plan);
                var completed = rail.Execute(intent, plan, Current());
                Check("debit, full mail leg and receipt commit together", completed.State == AuctionAssetCommitState.Committed && Gold() == 900 && Count(65) == 7
                    && Scalar("SELECT COUNT(*) FROM mailbox_messages WHERE gold=20 AND unlimited_flag=1;") == 1);
                Check("same operation replay never debits or mails twice", rail.Execute(intent, plan, Current()).State == AuctionAssetCommitState.Committed
                    && Gold() == 900 && Count(65) == 7 && Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 1);
                bool replayPreconditionCalled = false;
                Check("committed replay precedes fresh eligibility checks", rail.Execute(intent, plan, Current(), (c,t) => {
                    replayPreconditionCalled = true; return false;
                }).State == AuctionAssetCommitState.Committed && !replayPreconditionCalled && Gold() == 900);
                var guardedId = Guid.NewGuid();
                var guardedPlan = AuctionPublicAssetPlan.Create(codec, 10, Array.Empty<AuctionItemDebit>(), Array.Empty<MailboxSendRequest>());
                Check("failed final eligibility cannot debit public assets", rail.Execute(Intent(guardedId, guardedPlan), guardedPlan, Current(), (c,t) => false).State == AuctionAssetCommitState.Rejected
                    && Gold() == 900 && Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 1);
                var otherPlan = AuctionPublicAssetPlan.Create(codec, 99, Array.Empty<AuctionItemDebit>(), Array.Empty<MailboxSendRequest>());
                Check("unbound plan rejected before mutation", Rejects(() => rail.Execute(intent, otherPlan, Current())) && Gold() == 900);
                var reusedKeyId = Guid.NewGuid();
                var reusedKeyPlan = AuctionPublicAssetPlan.Create(codec, 1, Array.Empty<AuctionItemDebit>(), new[] { Mail(id) });
                Check("another operation cannot reuse a mail delivery key", Rejects(() => rail.Execute(Intent(reusedKeyId, reusedKeyPlan), reusedKeyPlan, Current())) && Gold() == 900);

                // Failure after all effects/receipt but before commit must roll
                // back this operation while preserving earlier unrelated dirty.
                Current().Inventory.SetMainVirtualCount(0, Gold() + 17);
                Current().Inventory.SetItem(InventoryListType.Main, 66, new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3002, Count = 4 });
                var rollbackId = Guid.NewGuid();
                var rollbackPlan = AuctionPublicAssetPlan.Create(codec, 10,
                    new[] { new AuctionItemDebit(InventoryListType.Main, 65, 1, codec.Capture(Current().Inventory, InventoryListType.Main, 65)) },
                    new[] { Mail(rollbackId, 5) });
                var rollbackIntent = Intent(rollbackId, rollbackPlan);
                var rollbackRail = new AuctionPublicAssetCommit(database, codec, gate,
                    point => { if (point == AuctionAssetFaultPoint.BeforePublicCommit) throw new InvalidOperationException("fixture before commit"); });
                var rolledBack = rollbackRail.Execute(rollbackIntent, rollbackPlan, Current());
                Check("failed transaction restores assets and retains prior dirty baseline", rolledBack.State == AuctionAssetCommitState.Rejected
                    && Gold() == 917 && Count(65) == 7 && Count(66) == 4 && Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 1
                    && database.Read(c => AuctionPublicReceiptRepository.Find(c, null, rollbackIntent)) == null);
                Check("rejected attempt cannot auto replay debit", rollbackRail.Execute(rollbackIntent, rollbackPlan, Current()).State == AuctionAssetCommitState.Pending && Gold() == 917);

                var staleSource = codec.Capture(Current().Inventory, InventoryListType.Main, 9);
                var weapon = Current().Inventory.GetItem(InventoryListType.Main, 9);
                weapon.Upgrade = 8;
                Current().Inventory.MarkDirty(InventoryListType.Main, 9);
                var staleId = Guid.NewGuid();
                var stalePlan = AuctionPublicAssetPlan.Create(codec, 1,
                    new[] { new AuctionItemDebit(InventoryListType.Main, 9, 1, staleSource) }, Array.Empty<MailboxSendRequest>());
                Check("same99B changed private revision cannot be consumed", rail.Execute(Intent(staleId, stalePlan), stalePlan, Current()).State == AuctionAssetCommitState.Rejected
                    && Gold() == 917 && Current().Inventory.GetItem(InventoryListType.Main, 9).Upgrade == 8);

                var wrapper = new ReadFaultDatabase(database);
                var unknownId = Guid.NewGuid();
                var unknownPlan = AuctionPublicAssetPlan.Create(codec, 10, Array.Empty<AuctionItemDebit>(), new[] { Mail(unknownId, 2) });
                var unknownIntent = Intent(unknownId, unknownPlan);
                var unknownRail = new AuctionPublicAssetCommit(wrapper, codec, gate,
                    point => { if (point == AuctionAssetFaultPoint.AfterPublicCommit) { wrapper.FailReads = true; throw new IOException("fixture lost outcome"); } });
                Check("unreadable outcome remains pending after actual commit", unknownRail.Execute(unknownIntent, unknownPlan, Current()).State == AuctionAssetCommitState.Pending && Gold() == 907);
                Check("pending cannot automatically debit twice", unknownRail.Execute(unknownIntent, unknownPlan, Current()).State == AuctionAssetCommitState.Pending && Gold() == 907);
                wrapper.FailReads = false;
                Check("receipt resolves unknown commit without another debit", unknownRail.Execute(unknownIntent, unknownPlan, Current()).State == AuctionAssetCommitState.Committed && Gold() == 907);

                var unavailableId = Guid.NewGuid();
                var unavailablePlan = AuctionPublicAssetPlan.Create(codec, 10, Array.Empty<AuctionItemDebit>(), Array.Empty<MailboxSendRequest>());
                var unavailableIntent = Intent(unavailableId, unavailablePlan);
                wrapper.FailReads = true;
                Check("unavailable public DB is never treated as absent", unknownRail.Execute(unavailableIntent, unavailablePlan, Current()).State == AuctionAssetCommitState.Pending && Gold() == 907);
                wrapper.FailReads = false;
                Check("pending admission read cannot auto replay when DB returns", unknownRail.Execute(unavailableIntent, unavailablePlan, Current()).State == AuctionAssetCommitState.Pending && Gold() == 907);

                var mismatchId = Guid.NewGuid(); var wrongOwner = Mail(mismatchId); wrongOwner.ReceiverAccountId = actor;
                var mismatchPlan = AuctionPublicAssetPlan.Create(codec, 1, Array.Empty<AuctionItemDebit>(), new[] { wrongOwner });
                Check("all receiver CID/AID bindings are verified in transaction", rail.Execute(Intent(mismatchId, mismatchPlan), mismatchPlan, Current()).State == AuctionAssetCommitState.Rejected && Gold() == 907);
                var senderId = Guid.NewGuid(); var wrongSender = Mail(senderId); wrongSender.SenderAccountId = actor;
                var senderPlan = AuctionPublicAssetPlan.Create(codec, 1, Array.Empty<AuctionItemDebit>(), new[] { wrongSender });
                Check("all sender CID/AID bindings are verified in transaction", rail.Execute(Intent(senderId, senderPlan), senderPlan, Current()).State == AuctionAssetCommitState.Rejected && Gold() == 907);
                database.Write((c, t) => { using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = "UPDATE characters SET delete_flag=1 WHERE character_id=78101;"; command.ExecuteNonQuery(); });
                var deletedId = Guid.NewGuid();
                Check("soft deleted actor cannot debit", rail.Execute(Intent(deletedId, unavailablePlan), unavailablePlan, Current()).State == AuctionAssetCommitState.Rejected && Gold() == 907);

                var attachmentId = Guid.NewGuid();
                var transferCore = codec.DecodeStrict(codec.Capture(Current().Inventory, InventoryListType.Main, 9));
                var envelope = codec.CreateTransferSnapshot(codec.Capture(Current().Inventory, InventoryListType.Main, 9), transferCore);
                var attachment = new MailboxSendAttachmentRequest { ItemId = transferCore.ItemId, ItemCount = 1,
                    ItemCoreData = envelope.NativeCore99, ExtraJson = envelope.ExtensionsJson, DetailJson = envelope.DetailJson };
                var attachmentMail = Mail(attachmentId, 0); attachmentMail.Attachments = new[] { attachment };
                var attachmentPlan = AuctionPublicAssetPlan.Create(codec, 0, Array.Empty<AuctionItemDebit>(), new[] { attachmentMail });
                attachment.ItemCoreData[0] = 255;
                Check("mail attachment bytes and private extension are frozen", attachmentPlan.CopySystemMails()[0].Attachments[0].ItemCoreData[0] == ItemCore.KindEquipment);
                var malformed = Mail(Guid.NewGuid(), 0); malformed.Attachments = new[] { new MailboxSendAttachmentRequest { ItemId = 27602, ItemCount = 1 } };
                Check("attachment without explicit99B cannot fall back to template", Rejects(() => AuctionPublicAssetPlan.Create(codec, 0, Array.Empty<AuctionItemDebit>(), new[] { malformed })));

                AuctionOperationIntent Expiry(Guid operation, AuctionPublicAssetPlan fundedPlan)
                    => new(operation, "fixture-expiry:" + operation.ToString("N"), AuctionOperationAction.Expire,
                        receiver, receiver, 1, 1792700000, JsonSerializer.Serialize(new { publicAssetPlan = fundedPlan.Serialize() }));
                var expiryId = Guid.NewGuid();
                var fundedPlan = AuctionPublicAssetPlan.Create(codec, 0, Array.Empty<AuctionItemDebit>(), new[] { Mail(expiryId, 20) });
                var expiry = Expiry(expiryId, fundedPlan);
                long beforeExpiry = Scalar("SELECT COUNT(*) FROM mailbox_messages;");
                Check("funded offline rail needs no inventory lease", rail.ExecuteFundedExpiry(expiry, fundedPlan).State == AuctionAssetCommitState.Committed
                    && Scalar("SELECT COUNT(*) FROM mailbox_messages;") == beforeExpiry + 1 && Gold() == 907);
                Check("funded offline replay never duplicates delivery", rail.ExecuteFundedExpiry(expiry, fundedPlan).State == AuctionAssetCommitState.Committed
                    && Scalar("SELECT COUNT(*) FROM mailbox_messages;") == beforeExpiry + 1);
                Check("offline rail rejects action without expiry funding contract", Rejects(() => rail.ExecuteFundedExpiry(Intent(expiryId, fundedPlan), fundedPlan)));
                var debitExpiryId = Guid.NewGuid();
                var debitExpiryPlan = AuctionPublicAssetPlan.Create(codec, 1, Array.Empty<AuctionItemDebit>(), Array.Empty<MailboxSendRequest>());
                Check("offline rail cannot debit an absent wallet", Rejects(() => rail.ExecuteFundedExpiry(Expiry(debitExpiryId, debitExpiryPlan), debitExpiryPlan)));
                var offlinePendingId = Guid.NewGuid();
                var offlinePendingPlan = AuctionPublicAssetPlan.Create(codec, 0, Array.Empty<AuctionItemDebit>(), new[] { Mail(offlinePendingId, 1) });
                var offlinePendingIntent = Expiry(offlinePendingId, offlinePendingPlan);
                wrapper.FailReads = true;
                Check("offline unavailable read remains pending", unknownRail.ExecuteFundedExpiry(offlinePendingIntent, offlinePendingPlan).State == AuctionAssetCommitState.Pending);
                wrapper.FailReads = false;
                Check("offline Pending cannot auto execute when storage returns", unknownRail.ExecuteFundedExpiry(offlinePendingIntent, offlinePendingPlan).State == AuctionAssetCommitState.Pending
                    && Scalar("SELECT COUNT(*) FROM mailbox_messages;") == beforeExpiry + 1);
                var badCount = attachmentPlan.CopySystemMails()[0]; badCount.Attachments[0].ItemCount = 2;
                Check("equipment attachment count must be exactly one", Rejects(() => AuctionPublicAssetPlan.Create(codec, 0, Array.Empty<AuctionItemDebit>(), new[] { badCount })));
                var badExtension = attachmentPlan.CopySystemMails()[0]; badExtension.Attachments[0].ExtraJson = "opaque";
                Check("bad private mail state cannot be silently stripped", Rejects(() => AuctionPublicAssetPlan.Create(codec, 0, Array.Empty<AuctionItemDebit>(), new[] { badExtension })));
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            finally
            {
                InventoryContext.Unregister(session);
                SqliteConnection.ClearAllPools();
                Directory.Delete(root, true);
            }
            Console.WriteLine($"AuctionAssetCommitSelfTest: passed={passed} failed={failed}; public transaction primitive only.");
            return failed == 0 ? 0 : 1;
        }

        private sealed class ReadFaultDatabase : IGameDatabase
        {
            private readonly IGameDatabase inner;
            internal ReadFaultDatabase(IGameDatabase inner) => this.inner = inner;
            internal bool FailReads { get; set; }
            public string DatabasePath => inner.DatabasePath;
            public string SchemaFilePath => inner.SchemaFilePath;
            public string ConnectionString => inner.ConnectionString;
            public SqliteConnection OpenConnection() => inner.OpenConnection();
            public T Read<T>(Func<SqliteConnection, T> action)
            { if (FailReads) throw new IOException("fixture read unavailable"); return inner.Read(action); }
            public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action, bool immediate = true) => inner.Write(action, immediate);
            public void Write(Action<SqliteConnection, SqliteTransaction> action, bool immediate = true) => inner.Write(action, immediate);
        }
    }
}
