using DfoServer.Game.Inventory;
using DfoServer.Game.TitleBook;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class TitleBookUseItemAchievementSelfTest
    {
        private const int ColorlessCubeItemId = 3037;
        private const int UltimateColorlessQuestId = 6532;
        private const int UltimateColorlessTitleItemId = 26648;
        private const short ColorlessCubeSlot = 358;

        public static int Run()
        {
            Console.WriteLine("=== TITLEBOOK_USE_ITEM_ACHIEVEMENT selftest ===");
            var failures = 0;

            VerifyPvfDefinition(ref failures);
            VerifyOperationTypeBoundary(ref failures);
            VerifyBatchMerge(ref failures);
            VerifyAtomicCompletion(ref failures);
            VerifyRollback(ref failures);

            Console.WriteLine(failures == 0
                ? "TITLEBOOK_USE_ITEM_ACHIEVEMENT selftest passed"
                : $"TITLEBOOK_USE_ITEM_ACHIEVEMENT selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyPvfDefinition(ref int failures)
        {
            var provider = TitleBookStaticDataProvider.LoadDefault();
            var colorless = provider.GetUseItemQuests(ColorlessCubeItemId)
                .SingleOrDefault(quest => quest.QuestId == UltimateColorlessQuestId);
            Check(
                "PVF maps colorless cube consumption to Ultimate Colorless",
                colorless != null
                    && colorless.CheckCount == 10000
                    && colorless.RewardTitleItemId == UltimateColorlessTitleItemId
                    && colorless.GetUseItemProgressPerItem(ColorlessCubeItemId) == 1,
                ref failures);

            var merged = provider.BuildUseItemProgressDeltas(
                new[]
                {
                    new KeyValuePair<int, int>(ColorlessCubeItemId, 2),
                    new KeyValuePair<int, int>(ColorlessCubeItemId, 3),
                });
            Check(
                "same material deltas merge by achievement quest",
                merged.TryGetValue(UltimateColorlessQuestId, out var delta)
                    && delta == 5,
                ref failures);
        }

        private static void VerifyOperationTypeBoundary(ref int failures)
        {
            Check(
                "only skill-material DELETE_ITEM operations are eligible",
                !InventoryHandler.IsSkillMaterialDeleteOperation(0)
                    && !InventoryHandler.IsSkillMaterialDeleteOperation(1)
                    && InventoryHandler.IsSkillMaterialDeleteOperation(2)
                    && InventoryHandler.IsSkillMaterialDeleteOperation(ushort.MaxValue),
                ref failures);
        }

        private static void VerifyBatchMerge(ref int failures)
        {
            var merged = new SortedDictionary<int, AchievementTriggerResult>();
            InventoryHandler.MergeAchievementProgress(
                merged,
                new[]
                {
                    new AchievementTriggerResult
                    {
                        Success = true,
                        QuestId = UltimateColorlessQuestId,
                        Remain1 = 1,
                    },
                });
            InventoryHandler.MergeAchievementProgress(
                merged,
                new[]
                {
                    new AchievementTriggerResult
                    {
                        Success = true,
                        QuestId = UltimateColorlessQuestId,
                        Remain1 = 0,
                        Completed = true,
                        TitleItemId = UltimateColorlessTitleItemId,
                    },
                    new AchievementTriggerResult
                    {
                        Success = true,
                        QuestId = UltimateColorlessQuestId,
                        Remain1 = 0,
                        Completed = false,
                    },
                });

            Check(
                "batch merge preserves the completion notification",
                merged.TryGetValue(UltimateColorlessQuestId, out var result)
                    && result.Completed
                    && result.TitleItemId == UltimateColorlessTitleItemId,
                ref failures);
        }

        private static void VerifyAtomicCompletion(ref int failures)
        {
            const int accountId = 9653201;
            const int characterId = 9653202;
            var sessionId = Guid.NewGuid();
            var tempDbPath = BuildTempDatabasePath("commit");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                SeedOwner(database, accountId, characterId, "titlebook-use-item-commit");
                lease = LoadAndRegister(database, sessionId, accountId, characterId);
                var mutations = new TitleBookMutationService();

                AchievementTriggerResult initial;
                lock (lease.SyncRoot)
                {
                    lease.Inventory.SetMainVirtualCount(ColorlessCubeSlot, 1);
                    initial = mutations.TriggerUseItemAchievements(
                        lease,
                        ColorlessCubeItemId,
                        9999).SingleOrDefault();
                }

                Check(
                    "precondition persists one cube and one remaining progress",
                    initial?.Success == true
                        && initial.Remain1 == 1
                        && !initial.Completed
                        && InventoryPersistenceService.SaveDirty(lease),
                    ref failures);

                IReadOnlyList<AchievementTriggerResult> progress =
                    Array.Empty<AchievementTriggerResult>();
                var committed = InventoryDeleteCommitService.TryCommit(
                    lease,
                    InventoryListType.Main,
                    ColorlessCubeSlot,
                    1,
                    "selftest-titlebook-use-item",
                    deleteMutation =>
                    {
                        progress = mutations.TriggerUseItemAchievements(
                            lease,
                            deleteMutation.ItemTemplateId,
                            deleteMutation.AppliedCount);
                        return true;
                    },
                    out var deletion);

                var completion = progress.SingleOrDefault();
                Check(
                    "cube deletion and title achievement commit together",
                    committed
                        && deletion?.ItemTemplateId == ColorlessCubeItemId
                        && deletion.AppliedCount == 1
                        && completion?.Completed == true
                        && completion.Remain1 == 0
                        && completion.Category == 1
                        && completion.BookIndex == 0
                        && completion.TitleItemId == UltimateColorlessTitleItemId,
                    ref failures);

                using (var connection = database.OpenConnection())
                {
                    Check(
                        "committed database contains zero cube, zero remainder and title",
                        ReadInt64(
                            connection,
                            "SELECT cube_clear FROM accounts WHERE account_id = @id;",
                            accountId) == 0
                            && ReadAchievementRemain(connection, characterId) == 0
                            && ReadTitleItemId(connection, characterId, 1, 0)
                                == UltimateColorlessTitleItemId,
                        ref failures);
                }

                var repeated = mutations.TriggerUseItemAchievements(
                    lease,
                    ColorlessCubeItemId,
                    1).SingleOrDefault();
                Check(
                    "completed achievement does not award the title twice",
                    repeated?.Success == true
                        && repeated.Remain1 == 0
                        && !repeated.Completed
                        && repeated.TitleItemId < 0,
                    ref failures);
            }
            finally
            {
                ReleaseLease(sessionId, characterId, lease);
                TryDeleteDatabase(tempDbPath);
            }
        }

        private static void VerifyRollback(ref int failures)
        {
            const int accountId = 9653211;
            const int characterId = 9653212;
            var sessionId = Guid.NewGuid();
            var tempDbPath = BuildTempDatabasePath("rollback");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                SeedOwner(database, accountId, characterId, "titlebook-use-item-rollback");
                lease = LoadAndRegister(database, sessionId, accountId, characterId);
                var mutations = new TitleBookMutationService();

                lock (lease.SyncRoot)
                    lease.Inventory.SetMainVirtualCount(ColorlessCubeSlot, 1);
                InventoryPersistenceService.SaveDirty(lease);

                var committed = InventoryDeleteCommitService.TryCommit(
                    lease,
                    InventoryListType.Main,
                    ColorlessCubeSlot,
                    1,
                    "selftest-titlebook-use-item-rollback",
                    deleteMutation =>
                    {
                        mutations.TriggerUseItemAchievements(
                            lease,
                            deleteMutation.ItemTemplateId,
                            deleteMutation.AppliedCount);
                        return false;
                    },
                    out _);

                using (var connection = database.OpenConnection())
                {
                    Check(
                        "failed commit restores cube and discards achievement progress",
                        !committed
                            && lease.Inventory.GetMainVirtualCount(ColorlessCubeSlot)?.Count == 1
                            && ReadInt64(
                                connection,
                                "SELECT cube_clear FROM accounts WHERE account_id = @id;",
                                accountId) == 1
                            && ReadAchievementRemain(connection, characterId) == null
                            && ReadTitleItemId(connection, characterId, 1, 0) == 0,
                        ref failures);
                }
            }
            finally
            {
                ReleaseLease(sessionId, characterId, lease);
                TryDeleteDatabase(tempDbPath);
            }
        }

        private static InventoryLease LoadAndRegister(
            GameDatabase database,
            Guid sessionId,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return InventoryContext.Register(sessionId, characterId, inventory);
            }
        }

        private static void SeedOwner(
            GameDatabase database,
            int accountId,
            int characterId,
            string name)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @name, '');
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@name", name);
                command.ExecuteNonQuery();
            }
        }

        private static long? ReadInt64(
            SqliteConnection connection,
            string sql,
            int id)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.Parameters.AddWithValue("@id", id);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? (long?)null
                    : Convert.ToInt64(value);
            }
        }

        private static long? ReadAchievementRemain(
            SqliteConnection connection,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT p1
FROM character_achievements
WHERE character_id = @cid AND achievement_id = @questId;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@questId", UltimateColorlessQuestId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? (long?)null
                    : Convert.ToInt64(value);
            }
        }

        private static int ReadTitleItemId(
            SqliteConnection connection,
            int characterId,
            int category,
            int slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_core
FROM character_titlebook_items
WHERE character_id = @cid
  AND category = @category
  AND slot_index = @slot;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@category", category);
                command.Parameters.AddWithValue("@slot", slotIndex);
                var bytes = command.ExecuteScalar() as byte[];
                return bytes == null ? 0 : ItemCore.FromBytes(bytes).ItemId;
            }
        }

        private static string BuildTempDatabasePath(string suffix)
        {
            return Path.Combine(
                Path.GetTempPath(),
                $"dfo_titlebook_use_item_{suffix}_{Guid.NewGuid():N}.db");
        }

        private static void ReleaseLease(
            Guid sessionId,
            int characterId,
            InventoryLease lease)
        {
            if (lease == null)
                return;

            InventoryPersistenceService.SaveDirty(lease);
            InventoryContext.Unregister(sessionId, characterId);
        }

        private static void TryDeleteDatabase(string path)
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
                catch
                {
                }
            }
        }

        private static void Check(string label, bool success, ref int failures)
        {
            Console.WriteLine($"  [{(success ? "PASS" : "FAIL")}] {label}");
            if (!success)
                failures++;
        }
    }
}
