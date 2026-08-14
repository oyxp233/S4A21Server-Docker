using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.SelfTests
{
    public static class MailboxSelfTest
    {
        private const int SenderAccountId = 981001;
        private const int ReceiverAccountId = 981002;
        private const int SenderCharacterId = 981101;
        private const int ReceiverCharacterId = 981102;
        private const int TransferItemId = 10007330;
        private const short SourceSlot = 65;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== MAILBOX ItemCore selftest ===");

            TestFeePolicy();
            TestItemCorePolicy();
            TestDetailCodec();
            TestListBatching();
            TestOverflowAttachmentSplitting();
            TestMailboxAlarmPacket();
            var dbPath = Path.Combine(
                Path.GetTempPath(),
                "dfo-mailbox-itemcore-selftest-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
                SeedCharacters(connectionString);

                var repository = new MailboxRepository(dbPath, ServerPaths.SchemaFilePath);
                var senderLease = LoadLease(connectionString, SenderCharacterId, SenderAccountId);
                var receiverLease = LoadLease(connectionString, ReceiverCharacterId, ReceiverAccountId);

                var sourceCore = CreateTransferCore(10);
                sourceCore.Attr = 0x05;
                sourceCore.Marker16 = ItemCore.Marker16Default;
                sourceCore.RandomOption0.Type = 7;
                sourceCore.RandomOption0.Value1 = 8;
                sourceCore.RandomOption0.Value2 = 9;
                Check(
                    "seed source ItemCore into new inventory",
                    senderLease.Inventory.SetItem(InventoryListType.Main, SourceSlot, sourceCore)
                    && senderLease.Inventory.SetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart,
                        100_000));

                var request = CreatePlayerMail();
                var send = repository.SendMail(request, senderLease);
                Check("player mail sends through InventoryLease", send.Success);
                Check("player mail deducts gold and fee in new virtual wallet",
                    send.UpdatedGold == 98_895
                    && senderLease.Inventory.GetMainVirtualCount(0)?.Count == 98_895);
                Check("player mail consumes only requested stack count",
                    senderLease.Inventory.GetItem(InventoryListType.Main, SourceSlot)?.Count == 8);

                var page = repository.LoadInboxPage(ReceiverCharacterId, 20);
                var mail = page.Entries.SingleOrDefault(entry => entry.MessageId == send.MessageId);
                var attachment = mail?.Attachments.SingleOrDefault();
                var persistedCore = MailboxItemCoreCodec.Decode(attachment);
                Check("mailbox persists transferred 82-byte ItemCore",
                    attachment != null
                    && attachment.ItemCoreData.Length == ItemCore.Size
                    && persistedCore != null
                    && persistedCore.ItemId == TransferItemId
                    && persistedCore.Count == 2
                    && persistedCore.Attr == 0x05
                    && persistedCore.RandomOption0.Type == 7);

                var replay = repository.SendMail(request, senderLease);
                Check("client retry returns original mail without second deduction",
                    replay.Success
                    && replay.MessageId == send.MessageId
                    && senderLease.Inventory.GetMainVirtualCount(0)?.Count == 98_895
                    && CountMessages(connectionString, request.IdempotencyKey) == 1);

                var claim = repository.ClaimMail(
                    ReceiverCharacterId,
                    attachment?.AttachmentId ?? 0,
                    receiverLease);
                Check("claim grants attachment and mail gold atomically",
                    claim.Success
                    && claim.ClaimedAttachmentCount == 1
                    && claim.ClaimedGold == 100
                    && receiverLease.Inventory.CountMainItem(TransferItemId) == 2
                    && receiverLease.Inventory.GetMainVirtualCount(0)?.Count == 100);
                Check("claim exposes committed attachment mutation",
                    claim.InventoryMutations.Count == 1
                    && claim.InventoryMutations[0].ItemTemplateId == TransferItemId
                    && claim.InventoryMutations[0].AppliedCount == 2);
                Check("claimed letter remains as an empty read letter",
                    repository.LoadInboxPage(ReceiverCharacterId, 20)
                        .Entries.Any(entry => entry.MessageId == send.MessageId
                            && entry.Gold == 0
                            && entry.AttachmentCount == 0));

                var duplicateClaims = new ConcurrentBag<MailboxClaimResult>();
                Parallel.For(0, 2, _ =>
                    duplicateClaims.Add(repository.ClaimMail(
                        ReceiverCharacterId,
                        send.MessageId,
                        receiverLease)));
                Check("duplicate claim cannot grant a second copy",
                    duplicateClaims.All(result => !result.Success)
                    && duplicateClaims.All(result => result.InventoryMutations.Count == 0)
                    && receiverLease.Inventory.CountMainItem(TransferItemId) == 2
                    && receiverLease.Inventory.GetMainVirtualCount(0)?.Count == 100);

                var systemRequest = CreateSystemMail();
                var systemFirst = repository.SendSystemMail(systemRequest);
                var systemReplay = repository.SendSystemMail(systemRequest);
                Check("system mail is idempotent", systemFirst.Success
                    && systemReplay.Success
                    && systemFirst.MessageId == systemReplay.MessageId);
                Check("administrator mail defaults to unlimited lifetime",
                    LoadUnlimitedFlag(connectionString, systemFirst.MessageId) == 1);
                Check("system attachment also persists ItemCore",
                    LoadItemCoreLength(connectionString, systemFirst.MessageId) == ItemCore.Size);

                var virtualAttachmentRequest = CreateSystemMail();
                virtualAttachmentRequest.IdempotencyKey = "selftest:system-virtual-attachment";
                virtualAttachmentRequest.Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = 0,
                        ItemId = 2,
                        ItemCount = 1
                    }
                };
                var virtualAttachment = repository.SendSystemMail(virtualAttachmentRequest);
                Check("system mail rejects virtual currency as an ordinary attachment",
                    !virtualAttachment.Success
                    && virtualAttachment.Error == MailboxSendError.InvalidAttachment
                    && CountMessages(connectionString, virtualAttachmentRequest.IdempotencyKey) == 0);

                var expiredRequest = CreateSystemMail();
                expiredRequest.IdempotencyKey = "selftest:expired-attachment";
                expiredRequest.Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = 0,
                        ItemId = TransferItemId,
                        ItemCount = 1,
                        ExpireTime = (int)DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()
                    }
                };
                var expiredSend = repository.SendSystemMail(expiredRequest);
                var expiredAttachment = repository.LoadInboxPage(ReceiverCharacterId, 20)
                    .Entries.Single(entry => entry.MessageId == expiredSend.MessageId)
                    .Attachments.Single();
                var expiredClaim = repository.ClaimMail(
                    ReceiverCharacterId,
                    expiredAttachment.AttachmentId,
                    receiverLease);
                Check("expired attachment reports ExpiredItem instead of InventoryFull",
                    expiredSend.Success
                    && !expiredClaim.Success
                    && expiredClaim.Error == MailboxSendError.ExpiredItem);

                Check("new inventory rows are authoritative",
                    CountNewInventoryRows(connectionString, SenderCharacterId) > 0
                    && CountNewInventoryRows(connectionString, ReceiverCharacterId) > 0);
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("[FAIL] unhandled: " + ex);
            }
            finally
            {
                DeleteDatabaseFiles(dbPath);
            }

            Console.WriteLine($"=== MAILBOX result: {_pass} passed, {_fail} failed ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void TestFeePolicy()
        {
            Check("mail fee: empty attachment uses base 100",
                MailboxRepository.CalculateFeeGold(0, 0) == 100);
            Check("mail fee: attachment base plus capped five percent gold",
                MailboxRepository.CalculateFeeGold(100, 1) == 1005
                && MailboxRepository.CalculateFeeGold(1_000_000, 1) == 11_000);
        }

        private static void TestListBatching()
        {
            var entries = new[]
            {
                new MailboxListEntry { MessageId = 3, Body = "claimed newest" },
                new MailboxListEntry { MessageId = 2, Body = "claimed older" },
                new MailboxListEntry
                {
                    MessageId = 1,
                    Body = "unclaimed oldest",
                    Attachments = new[]
                    {
                        new MailboxAttachmentEntry
                        {
                            AttachmentId = 1,
                            ItemTemplateId = TransferItemId,
                            ItemCount = 1
                        }
                    }
                }
            };

            var batches = MailboxHandler.BuildMailboxListNotificationBatches(entries, 0);
            Check(
                "0x0061 separates detail-created rows from summary-created rows",
                batches.Count == 2
                && batches[0].Length >= 2
                && batches[0][1] == 0
                && batches[1].Length >= 2
                && batches[1][1] == 1);
        }

        private static void TestItemCorePolicy()
        {
            var request = new MailboxSendRequest
            {
                SenderAccountId = SenderAccountId,
                ReceiverAccountId = ReceiverAccountId
            };
            var core = CreateTransferCore(1);
            Check("PVF-free item is tradable through ItemCore policy",
                MailboxSendPolicy.ValidateAttachment(request, core) == MailboxSendError.None);

            core.TradeRestriction = 1;
            Check("instance tradeRestriction rejects transfer",
                MailboxSendPolicy.ValidateAttachment(request, core) == MailboxSendError.NotTradable);

            var limited = ItemCore.Create(ItemCore.KindConsumable, 1007147);
            limited.Count = 1;
            limited.StackTradeCount = 1;
            Check("trade-limit item accepts a remaining transfer",
                MailboxSendPolicy.ValidateAttachment(request, limited) == MailboxSendError.None);
            limited.StackTradeCount = 0;
            Check("trade-limit item rejects exhausted transfer count",
                MailboxSendPolicy.ValidateAttachment(request, limited) == MailboxSendError.NotTradable);
        }

        private static void TestDetailCodec()
        {
            var inventory = new InventoryService(SenderCharacterId, SenderAccountId);
            var avatar = ItemCore.Create(ItemCore.KindAvatar, 39075);
            avatar.AvatarUid = 12345;
            inventory.AvatarDetails.Attach(new AvatarDetail
            {
                AvatarUid = avatar.AvatarUid,
                OwnerId = SenderAccountId,
                CharacterId = SenderCharacterId,
                ItemId = avatar.ItemId,
                ExpireDate = 2_000_000_000,
                ClearAvatarId = 77,
                JewelSocket = Enumerable.Range(0, JewelSocket.Size).Select(value => (byte)value).ToArray(),
                Color1 = 11,
                Color2 = 22,
                DeleteDate = 33,
            });

            var detailJson = MailboxItemDetailCodec.Capture(inventory, avatar);
            var options = MailboxItemDetailCodec.BuildCreateOptions(detailJson);
            Check("avatar detail survives mailbox ownership-transfer payload",
                options?.AvatarDetailTemplate != null
                && options.AvatarDetailTemplate.ExpireDate == 2_000_000_000
                && options.AvatarDetailTemplate.ClearAvatarId == 77
                && options.AvatarDetailTemplate.JewelSocket.Length == JewelSocket.Size
                && options.AvatarDetailTemplate.Color1 == 11
                && options.AvatarDetailTemplate.Color2 == 22);

            var legacyAvatar = MailboxItemCoreCodec.Decode(new MailboxAttachmentEntry
            {
                ItemTemplateId = avatar.ItemId,
                ItemKind = "avatar",
                ItemCount = 1,
                OptionValue = 4,
                ItemCoreData = Array.Empty<byte>(),
            });
            Check("legacy mailbox avatar option_value restores ability_no",
                legacyAvatar != null
                && legacyAvatar.ItemKind == ItemCore.KindAvatar
                && legacyAvatar.AbilityNo == 4);
        }

        private static void TestOverflowAttachmentSplitting()
        {
            var stackLimitOneItemId = FindStackLimitOneStackableItemId();
            if (stackLimitOneItemId <= 0)
            {
                Check("find stack-limit-one stackable fixture", false);
                return;
            }

            var createRequest = InventoryRewardGrantRequest.Create(
                stackLimitOneItemId,
                2,
                ItemCreateReason.Unknown);
            Check("overflow mail splits created stack-limit-one item into separate attachments",
                MailboxInventoryOverflowRewardSink.TryBuildAttachmentRequests(
                    new[] { createRequest },
                    out var createdAttachments)
                && createdAttachments.Count == 2
                && createdAttachments.All(attachment =>
                    attachment.ItemId == stackLimitOneItemId
                    && attachment.ItemCount == 1
                    && ItemCore.FromBytes(attachment.ItemCoreData)?.Count == 1));

            var existingCore = ItemCore.Create(ItemCore.KindConsumable, stackLimitOneItemId);
            existingCore.Count = 2;
            var existingRequest = InventoryRewardGrantRequest.Existing(
                existingCore,
                2,
                ItemCreateReason.Unknown);
            Check("overflow mail splits existing stack-limit-one ItemCore into separate attachments",
                MailboxInventoryOverflowRewardSink.TryBuildAttachmentRequests(
                    new[] { existingRequest },
                    out var existingAttachments)
                && existingAttachments.Count == 2
                && existingAttachments.All(attachment =>
                    attachment.ItemId == stackLimitOneItemId
                    && attachment.ItemCount == 1
                    && ItemCore.FromBytes(attachment.ItemCoreData)?.Count == 1));
        }

        private static void TestMailboxAlarmPacket()
        {
            var body = MailboxHandler.BuildMailboxAlarmNotification(1);
            Check("0x0063 online mail alarm carries one UInt16 count",
                body.Length == 2 && BitConverter.ToUInt16(body, 0) == 1);
        }

        private static int FindStackLimitOneStackableItemId()
        {
            var list = PvfLib.LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst"));
            foreach (var entry in list.Entries)
            {
                try
                {
                    var metadata = ItemMetadataResolver.Resolve(entry.Id);
                    if (metadata != null
                        && metadata.IsStackable
                        && metadata.StackLimit == 1)
                        return entry.Id;
                }
                catch
                {
                }
            }

            return 0;
        }

        private static MailboxSendRequest CreatePlayerMail()
        {
            return new MailboxSendRequest
            {
                SenderCharacterId = SenderCharacterId,
                SenderAccountId = SenderAccountId,
                SenderName = "mailbox-sender",
                SenderLevel = 60,
                ReceiverCharacterId = ReceiverCharacterId,
                ReceiverAccountId = ReceiverAccountId,
                ReceiverName = "mailbox-receiver",
                ReceiverLevel = 60,
                Gold = 100,
                Text = "itemcore-transfer",
                SourceProtocol = 0x005E,
                IdempotencyKey = "selftest:itemcore-transfer",
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = 0,
                        ItemSlot = (ushort)SourceSlot,
                        ItemId = TransferItemId,
                        ItemCount = 2
                    }
                }
            };
        }

        private static MailboxSendRequest CreateSystemMail()
        {
            return new MailboxSendRequest
            {
                SenderCharacterId = SenderCharacterId,
                SenderAccountId = SenderAccountId,
                SenderName = "DNFadmin",
                ReceiverCharacterId = ReceiverCharacterId,
                ReceiverAccountId = ReceiverAccountId,
                ReceiverName = "mailbox-receiver",
                Text = "system-itemcore",
                MailType = 1,
                SourceProtocol = 0,
                IdempotencyKey = "selftest:system-itemcore",
                AuditActor = "selftest",
                AuditReason = "mailbox regression",
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = 0,
                        ItemId = TransferItemId,
                        ItemCount = 1
                    }
                }
            };
        }

        private static ItemCore CreateTransferCore(int count)
        {
            if (ItemMetadataResolver.TryResolveItemKind(TransferItemId, out var kind))
            {
                var created = InventoryCreateService.CreateCore(
                    kind,
                    TransferItemId,
                    ItemCreateReason.MailAttachment,
                    count);
                if (InventoryStackRuleService.IsStackable(created))
                    created.Count = count;
                return created;
            }

            return new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = TransferItemId,
                Count = count
            };
        }

        private static InventoryLease LoadLease(string connectionString, int characterId, int accountId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                return new InventoryLease(
                    Guid.NewGuid(),
                    characterId,
                    InventoryService.LoadFromDb(connection, characterId, accountId),
                    1);
            }
        }

        private static void SeedCharacters(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id) VALUES
    (@senderAid, 'mailbox-selftest-sender'),
    (@receiverAid, 'mailbox-selftest-receiver');
INSERT INTO characters(character_id, account_id, name, level) VALUES
    (@senderCid, @senderAid, 'mailbox-sender', 60),
    (@receiverCid, @receiverAid, 'mailbox-receiver', 60);";
                    command.Parameters.AddWithValue("@senderAid", SenderAccountId);
                    command.Parameters.AddWithValue("@receiverAid", ReceiverAccountId);
                    command.Parameters.AddWithValue("@senderCid", SenderCharacterId);
                    command.Parameters.AddWithValue("@receiverCid", ReceiverCharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int CountMessages(string connectionString, string idempotencyKey)
        {
            return Scalar(
                connectionString,
                "SELECT COUNT(*) FROM mailbox_messages WHERE idempotency_key=@value;",
                idempotencyKey);
        }

        private static int LoadUnlimitedFlag(string connectionString, long messageId)
        {
            return Scalar(
                connectionString,
                "SELECT unlimited_flag FROM mailbox_messages WHERE message_id=@value;",
                messageId);
        }

        private static int LoadItemCoreLength(string connectionString, long messageId)
        {
            return Scalar(
                connectionString,
                "SELECT COALESCE(length(item_core), 0) FROM mailbox_attachments WHERE message_id=@value LIMIT 1;",
                messageId);
        }

        private static int CountNewInventoryRows(string connectionString, int characterId)
        {
            return Scalar(
                connectionString,
                "SELECT COUNT(*) FROM character_inventory_items WHERE character_id=@value;",
                characterId);
        }

        private static int Scalar(string connectionString, string sql, object value)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@value", value);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static int Scalar(SqliteConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static void Check(string name, bool passed)
        {
            if (passed)
            {
                _pass++;
                Console.WriteLine("[PASS] " + name);
                return;
            }

            _fail++;
            Console.WriteLine("[FAIL] " + name);
        }

        private static void DeleteDatabaseFiles(string path)
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); }
                catch { }
            }
        }
    }
}
