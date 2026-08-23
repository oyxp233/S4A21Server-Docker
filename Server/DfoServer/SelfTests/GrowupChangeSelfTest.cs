using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Characters;
using DfoServer.Network.Parsers.Characters;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.SelfTests
{
    public static class GrowupChangeSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== GROWUP_CHANGE selftest ===");
            var failures = 0;

            VerifyRequestAndAck(ref failures);
            VerifyConfigParser(ref failures);
            VerifyRealPvfConfig(ref failures);
            VerifySchemaMigration(ref failures);
            VerifyApplyRules(ref failures);
            VerifyClassChangeItemProtocol(ref failures);
            VerifyRealPvfClassChangeItems(ref failures);
            VerifyClassChangeItemRules(ref failures);

            Console.WriteLine(failures == 0
                ? "GROWUP_CHANGE selftest passed"
                : $"GROWUP_CHANGE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyRequestAndAck(ref int failures)
        {
            var captured = new byte[]
            {
                0x18, 0xF7, 0x1A, 0x00, 0x4E,
                0x43, 0x30, 0x02, 0xD4, 0xF6,
                0x1A, 0x00, 0xB0, 0xA5, 0x03,
            };
            var capturedWithPadding = captured.Concat(new byte[] { 0x00 }).ToArray();

            Check(
                "0x0310 request parses target grow type from captured body",
                GrowupChangeRequestParser.TryParse(captured, out var request)
                && request.TargetGrowType == 3
                && GrowupChangeRequestParser.TryParse(capturedWithPadding, out var padded)
                && padded.TargetGrowType == 3
                && !GrowupChangeRequestParser.TryParse(captured.Take(14).ToArray(), out _),
                ref failures);

            var ack = GrowupChangeAckBuilder.Build(new GrowupChangeResult
            {
                Status = GrowupChangeStatus.Success,
                ResultCode = GrowupChangeResult.ResultCodeSuccess,
                NewChangeCount = 1,
            });
            Check(
                "0x0310 success ACK is outer success + i32 result + u8 count",
                ack.SequenceEqual(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x01 }),
                ref failures);

            var packet = GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.RE_GROWUP_CHANGE,
                ack);
            Check(
                "0x0310 ACK envelope length matches captured 21B packet",
                packet.Length == 21
                && BitConverter.ToUInt16(packet, 1)
                    == (ushort)CmdPacketTypeA21.RE_GROWUP_CHANGE
                && BitConverter.ToInt32(packet, 3) == 21,
                ref failures);

            var goldAck = GrowupChangeAckBuilder.Build(new GrowupChangeResult
            {
                Status = GrowupChangeStatus.InsufficientGold,
                ResultCode = GrowupChangeResult.ResultCodeInsufficientGold,
                NewChangeCount = 2,
            });
            Check(
                "0x0310 gold failure ACK preserves current count",
                goldAck.SequenceEqual(new byte[] { 0x01, 0x16, 0x00, 0x00, 0x00, 0x02 }),
                ref failures);
        }

        private static void VerifyConfigParser(ref int failures)
        {
            var config = GrowupChangeConfigProvider.Parse(@"
[grow up change lv]
15 49
[/grow up change lv]
[grow up change gold]
0 100113 133484
[/grow up change gold]
");
            Check(
                "growup.etc parser reads level range and gold table",
                config.IsValid
                && config.MinLevel == 15
                && config.MaxLevel == 49
                && config.AllowsLevel(15)
                && config.AllowsLevel(49)
                && !config.AllowsLevel(14)
                && !config.AllowsLevel(50)
                && config.ResolveGoldCost(0) == 0
                && config.ResolveGoldCost(1) == 100113
                && config.ResolveGoldCost(9) == 133484,
                ref failures);
        }

        private static void VerifyClassChangeItemProtocol(ref int failures)
        {
            var body = new byte[] { 0x05, 0x00, 0x03, 0xAA };
            Check(
                "0x01F7 class-change item request parses item slot and target grow type",
                ClassChangeItemRequestParser.TryParse(body, out var request)
                && request.ItemSlotIndex == 5
                && request.TargetGrowType == 3
                && !ClassChangeItemRequestParser.TryParse(body.Take(2).ToArray(), out _),
                ref failures);

            Check(
                "0x01F7 class-change item success ACK is common success only",
                ClassChangeItemAckBuilder.Build(new ClassChangeItemResult
                {
                    Status = ClassChangeItemStatus.Success,
                }).SequenceEqual(new byte[] { 0x01 }),
                ref failures);

            Check(
                "0x01F7 expired class-change item ACK maps to client expired-item code",
                ClassChangeItemAckBuilder.Build(new ClassChangeItemResult
                {
                    Status = ClassChangeItemStatus.SourceExpired,
                }).SequenceEqual(new byte[] { 0x00, 0xEB }),
                ref failures);
        }

        private static void VerifyRealPvfConfig(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("real PVF growup.etc check skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            var config = GrowupChangeConfigProvider.Parse(
                PvfArchiveAccessor.ReadText("character/growup.etc"));
            Check(
                "real PVF growup.etc matches system change bounds and gold table",
                config.IsValid
                && config.MinLevel == 15
                && config.MaxLevel == 49
                && config.GoldCosts.Count >= 10
                && config.ResolveGoldCost(0) == 0
                && config.ResolveGoldCost(1) == 100113
                && config.ResolveGoldCost(9) == 1000000,
                ref failures);
        }

        private static void VerifyRealPvfClassChangeItems(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("real PVF class-change item check skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            var beginnerFound = false;
            var advancedFound = false;
            var list = LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst"));
            foreach (var entry in list.Entries)
            {
                try
                {
                    var stackable = StackableItemFile.Parse(
                        PvfArchiveAccessor.ReadText(
                            Path.Combine("stackable", entry.FilePath)));
                    if (!string.Equals(
                            NormalizeClassChangeAction(stackable.ActionTypeName),
                            "class change",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var iconIndex = ResolveClassChangeIconIndex(stackable.Icon);
                    if (iconIndex == 788)
                        beginnerFound |= IsValidClassChangeLevelRange(stackable);
                    else if (iconIndex == 789)
                        advancedFound |= IsValidClassChangeLevelRange(stackable);

                    if (beginnerFound && advancedFound)
                        break;
                }
                catch
                {
                    // PVF 中个别历史条目解析失败不影响本标签巡检。
                }
            }

            Check(
                "real PVF contains beginner and advanced [class change] stackables",
                beginnerFound && advancedFound,
                ref failures);
        }

        private static void VerifySchemaMigration(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_growup_change_migration_{Guid.NewGuid():N}.db");

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                {
                    Check(
                        "new schema creates growup_change_count",
                        ColumnExists(connection, "characters", "growup_change_count")
                        && SqliteMigrations.ReadVersion(connection)
                            == SqliteMigrations.CurrentVersion,
                        ref failures);

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
ALTER TABLE characters DROP COLUMN growup_change_count;
UPDATE schema_metadata SET schema_version = 7 WHERE singleton_id = 1;
PRAGMA user_version = 7;";
                        command.ExecuteNonQuery();
                    }

                    SqliteMigrations.Apply(connection);
                    Check(
                        "schema v7 migrates to current schema with growup_change_count",
                        ColumnExists(connection, "characters", "growup_change_count")
                        && SqliteMigrations.ReadVersion(connection)
                            == SqliteMigrations.CurrentVersion,
                        ref failures);
                }
            }
            finally
            {
                TryDelete(tempDbPath);
            }
        }

        private static void VerifyApplyRules(ref int failures)
        {
            const int accountId = 76001;
            const int characterId = 76002;
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_growup_change_apply_{Guid.NewGuid():N}.db");
            var config = new GrowupChangeConfig
            {
                MinLevel = 1,
                MaxLevel = 1,
            };
            config.GoldCosts.AddRange(new[] { 0, 100000, 200000 });

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                SeedCharacter(database, accountId, characterId);

                InventoryService insufficientGoldInventory;
                using (var connection = database.OpenConnection())
                {
                    insufficientGoldInventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                }
                insufficientGoldInventory.AttachMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    0,
                    99999);
                insufficientGoldInventory.ClearDirtyState();

                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    GrowupChangeApplicationService.TryApply(
                        connection,
                        transaction,
                        insufficientGoldInventory,
                        new GrowupChangeRequest { TargetGrowType = 2 },
                        config,
                        out var result);
                    transaction.Commit();

                    Check(
                        "growup change rejects insufficient gold without changing character",
                        result.Status == GrowupChangeStatus.InsufficientGold
                        && result.ResultCode == GrowupChangeResult.ResultCodeInsufficientGold
                        && result.NewChangeCount == 1
                        && ReadCharacterInt(database, characterId, "grow_type") == 1
                        && ReadCharacterInt(database, characterId, "growup_change_count") == 1,
                        ref failures);
                }

                var sessionId = Guid.NewGuid();
                InventoryLease lease = null;
                try
                {
                    using (var connection = database.OpenConnection())
                    {
                        var inventory = InventoryService.LoadFromDb(
                            connection,
                            characterId,
                            accountId,
                            database);
                        inventory.AttachMainVirtualCount(
                            InventoryService.MainVirtualCurrencySlotStart,
                            0,
                            200000);
                        inventory.ClearDirtyState();
                        lease = InventoryContext.Register(
                            sessionId,
                            characterId,
                            inventory);
                    }

                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        var applied = GrowupChangeApplicationService.TryApply(
                            connection,
                            transaction,
                            lease.Inventory,
                            new GrowupChangeRequest { TargetGrowType = 2 },
                            config,
                            out var result);
                        var saved = InventoryPersistenceService
                            .SaveDirtyInTransaction(
                                connection,
                                transaction,
                                lease);
                        transaction.Commit();
                        lease.Inventory.ClearDirtyState();

                        Check(
                            "growup change applies grow type, count and gold atomically",
                            applied
                            && saved
                            && result.Success
                            && result.PreviousChangeCount == 1
                            && result.NewChangeCount == 2
                            && result.UpdatedGold == 100000
                            && ReadCharacterInt(database, characterId, "grow_type") == 2
                            && ReadCharacterInt(database, characterId, "growup_change_count") == 2
                            && ReadGold(database, characterId) == 100000,
                            ref failures);
                    }
                }
                finally
                {
                    InventoryContext.Unregister(sessionId, characterId);
                }
            }
            finally
            {
                TryDelete(tempDbPath);
            }
        }

        private static void VerifyClassChangeItemRules(ref int failures)
        {
            const int accountId = 76101;
            const int characterId = 76102;
            const int beginnerItemId = 880000001;
            const int advancedItemId = 880000002;
            const short beginnerSlot = 10;
            const short advancedSlot = 11;
            const short secondAwakenedSlot = 13;
            const short rejectSlot = 12;
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_class_change_item_{Guid.NewGuid():N}.db");

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                SeedCharacter(database, accountId, characterId);
                UpdateCharacterJobGrowAndLevel(database, characterId, job: 0, growType: 1, level: 30);

                var sessionId = Guid.NewGuid();
                InventoryLease lease = null;
                try
                {
                    lease = RegisterInventoryWithMainItem(
                        database,
                        sessionId,
                        characterId,
                        accountId,
                        beginnerSlot,
                        beginnerItemId,
                        2);

                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        var applied = ClassChangeItemApplicationService.TryApply(
                            connection,
                            transaction,
                            lease.Inventory,
                            new ClassChangeItemRequest
                            {
                                ItemSlotIndex = beginnerSlot,
                                TargetGrowType = 2,
                            },
                            id => BuildClassChangeStackable(id == beginnerItemId),
                            out var result);
                        var saved = InventoryPersistenceService
                            .SaveDirtyInTransaction(
                                connection,
                                transaction,
                                lease);
                        transaction.Commit();
                        lease.Inventory.ClearDirtyState();

                        Check(
                            "beginner class-change item changes transfer branch and consumes one item",
                            applied
                            && saved
                            && result.Success
                            && result.Mode == ClassChangeItemMode.Beginner
                            && result.NewGrowType == 2
                            && ReadCharacterInt(database, characterId, "grow_type") == 2
                            && ReadMainItemCount(database, characterId, accountId, beginnerSlot) == 1,
                            ref failures);
                    }
                }
                finally
                {
                    InventoryContext.Unregister(sessionId, characterId);
                }

                UpdateCharacterJobGrowAndLevel(database, characterId, job: 0, growType: 0x11, level: 55);
                sessionId = Guid.NewGuid();
                lease = null;
                try
                {
                    lease = RegisterInventoryWithMainItem(
                        database,
                        sessionId,
                        characterId,
                        accountId,
                        advancedSlot,
                        advancedItemId,
                        1);

                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        var applied = ClassChangeItemApplicationService.TryApply(
                            connection,
                            transaction,
                            lease.Inventory,
                            new ClassChangeItemRequest
                            {
                                ItemSlotIndex = advancedSlot,
                                TargetGrowType = 2,
                            },
                            id => BuildClassChangeStackable(id == beginnerItemId),
                            out var result);
                        var saved = InventoryPersistenceService
                            .SaveDirtyInTransaction(
                                connection,
                                transaction,
                                lease);
                        transaction.Commit();
                        lease.Inventory.ClearDirtyState();

                        Check(
                            "advanced class-change item replaces transfer branch, preserves first awakening and clears target awakening quests",
                            applied
                            && saved
                            && result.Success
                            && result.Mode == ClassChangeItemMode.Advanced
                            && result.NewGrowType == 0x12
                            && result.MarkedAwakeningQuestCount > 0
                            && ReadCharacterInt(database, characterId, "grow_type") == 0x12
                            && HasClearedAwakeningQuest(
                                database,
                                characterId,
                                targetGrowType: 2,
                                jobChangeQuestValue: 2)
                            && ReadMainItemCount(database, characterId, accountId, advancedSlot) == 0,
                            ref failures);
                    }
                }
                finally
                {
                    InventoryContext.Unregister(sessionId, characterId);
                }

                UpdateCharacterJobGrowAndLevel(database, characterId, job: 0, growType: 0x21, level: 85);
                sessionId = Guid.NewGuid();
                lease = null;
                try
                {
                    lease = RegisterInventoryWithMainItem(
                        database,
                        sessionId,
                        characterId,
                        accountId,
                        secondAwakenedSlot,
                        advancedItemId,
                        1);

                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        var applied = ClassChangeItemApplicationService.TryApply(
                            connection,
                            transaction,
                            lease.Inventory,
                            new ClassChangeItemRequest
                            {
                                ItemSlotIndex = secondAwakenedSlot,
                                TargetGrowType = 3,
                            },
                            id => BuildClassChangeStackable(id == beginnerItemId),
                            out var result);
                        var saved = InventoryPersistenceService
                            .SaveDirtyInTransaction(
                                connection,
                                transaction,
                                lease);
                        transaction.Commit();
                        lease.Inventory.ClearDirtyState();

                        Check(
                            "advanced class-change item accepts second-awakened characters and preserves second awakening",
                            applied
                            && saved
                            && result.Success
                            && result.Mode == ClassChangeItemMode.Advanced
                            && result.NewGrowType == 0x23
                            && result.MarkedAwakeningQuestCount > 0
                            && ReadCharacterInt(database, characterId, "grow_type") == 0x23
                            && HasClearedAwakeningQuest(
                                database,
                                characterId,
                                targetGrowType: 3,
                                jobChangeQuestValue: 2)
                            && HasClearedAwakeningQuest(
                                database,
                                characterId,
                                targetGrowType: 3,
                                jobChangeQuestValue: 3)
                            && ReadMainItemCount(database, characterId, accountId, secondAwakenedSlot) == 0,
                            ref failures);
                    }
                }
                finally
                {
                    InventoryContext.Unregister(sessionId, characterId);
                }

                UpdateCharacterJobGrowAndLevel(database, characterId, job: 0, growType: 0x11, level: 30);
                sessionId = Guid.NewGuid();
                lease = null;
                try
                {
                    lease = RegisterInventoryWithMainItem(
                        database,
                        sessionId,
                        characterId,
                        accountId,
                        rejectSlot,
                        beginnerItemId,
                        1);

                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        ClassChangeItemApplicationService.TryApply(
                            connection,
                            transaction,
                            lease.Inventory,
                            new ClassChangeItemRequest
                            {
                                ItemSlotIndex = rejectSlot,
                                TargetGrowType = 3,
                            },
                            id => BuildClassChangeStackable(id == beginnerItemId),
                            out var result);
                        transaction.Commit();

                        Check(
                            "beginner class-change item rejects first-awakened characters without consuming",
                            result.Status == ClassChangeItemStatus.InvalidState
                            && ReadCharacterInt(database, characterId, "grow_type") == 0x11
                            && ReadMainItemCount(database, characterId, accountId, rejectSlot) == 1,
                            ref failures);
                    }
                }
                finally
                {
                    InventoryContext.Unregister(sessionId, characterId);
                }
            }
            finally
            {
                TryDelete(tempDbPath);
            }
        }

        private static void SeedCharacter(
            GameDatabase database,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'growup-change-account', '');
INSERT INTO characters (
    character_id, account_id, name, job, grow_type, level, growup_change_count
) VALUES (
    @cid, @aid, 'growup-change-character', 99, 1, 1, 1
);
INSERT INTO character_subtype1_fields(character_id)
VALUES (@cid);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        private static int ReadCharacterInt(
            GameDatabase database,
            int characterId,
            string columnName)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    $"SELECT {columnName} FROM characters WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static int ReadGold(GameDatabase database, int characterId)
        {
            using (var connection = database.OpenConnection())
            {
                return InventoryMainVirtualCountRepository.LoadCurrencyCount(
                    connection,
                    null,
                    characterId,
                    InventoryService.MainVirtualCurrencySlotStart);
            }
        }

        private static InventoryLease RegisterInventoryWithMainItem(
            GameDatabase database,
            Guid sessionId,
            int characterId,
            int accountId,
            short slotIndex,
            int itemTemplateId,
            int count)
        {
            InventoryService inventory;
            using (var connection = database.OpenConnection())
            {
                inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
            }

            inventory.SetItem(
                InventoryListType.Main,
                slotIndex,
                new ItemCore
                {
                    ItemKind = ItemCore.KindConsumable,
                    ItemId = itemTemplateId,
                    Count = count,
                });

            var lease = InventoryContext.Register(
                sessionId,
                characterId,
                inventory);
            if (!OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "selftest-seed-class-change-item"))
            {
                throw new InvalidOperationException(
                    "failed to persist class-change selftest item");
            }

            return lease;
        }

        private static StackableItemFile BuildClassChangeStackable(
            bool beginner)
        {
            return new StackableItemFile
            {
                ActionTypeName = "[class change]",
                Icon = beginner ? "`cash.img` 788" : "`cash.img` 789",
                MinimumLevel = beginner ? 15 : 50,
                MaximumLevel = beginner ? 49 : 85,
            };
        }

        private static void UpdateCharacterJobGrowAndLevel(
            GameDatabase database,
            int characterId,
            int job,
            int growType,
            int level)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE characters
SET job = @job,
    grow_type = @grow,
    level = @level
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@job", job);
                command.Parameters.AddWithValue("@grow", growType);
                command.Parameters.AddWithValue("@level", level);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        private static int ReadMainItemCount(
            GameDatabase database,
            int characterId,
            int accountId,
            short slotIndex)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                var item = inventory.GetItem(
                    InventoryListType.Main,
                    slotIndex);
                return item != null && item.ItemId > 0 ? item.Count : 0;
            }
        }

        private static bool HasClearedAwakeningQuest(
            GameDatabase database,
            int characterId,
            int targetGrowType,
            int jobChangeQuestValue)
        {
            using (var connection = database.OpenConnection())
            {
                var clearedFlags = QuestRepository.LoadClearedFlags(
                    connection,
                    null,
                    characterId);
                foreach (var questId in QuestCatalog.OrderedIds)
                {
                    if (!clearedFlags.ContainsKey(questId))
                        continue;

                    var quest = QuestData.GetQuestFile(questId);
                    if (quest != null
                        && quest.GrowType == targetGrowType
                        && quest.JobChangeQuestValue == jobChangeQuestValue)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsValidClassChangeLevelRange(
            StackableItemFile stackable)
        {
            return stackable != null
                && stackable.MinimumLevel >= 0
                && stackable.MaximumLevel >= stackable.MinimumLevel;
        }

        private static string NormalizeClassChangeAction(string raw)
        {
            var text = StackableItemProvider.NormalizeType(raw);
            if (text.Length >= 2
                && text[0] == '['
                && text[text.Length - 1] == ']')
            {
                return text.Substring(1, text.Length - 2).Trim();
            }

            return text.Trim();
        }

        private static int ResolveClassChangeIconIndex(string icon)
        {
            if (string.IsNullOrWhiteSpace(icon))
                return -1;

            var matches = Regex.Matches(icon, @"(?<!\d)\d+(?!\d)");
            if (matches.Count == 0)
                return -1;

            return int.TryParse(
                matches[matches.Count - 1].Value,
                out var value)
                ? value
                : -1;
        }

        private static bool ColumnExists(
            SqliteConnection connection,
            string tableName,
            string columnName)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA table_info({tableName});";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(
                                reader.GetString(1),
                                columnName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine("[PASS] " + name);
                return;
            }

            failures++;
            Console.WriteLine("[FAIL] " + name);
        }

        private static void TryDelete(string path)
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[WARN] temp cleanup failed: " + ex.Message);
                }
            }
        }
    }
}
