using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.Lottery;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class LotteryProgressResetTransactionSelfTest
    {
        private const int AccountId = 983800;
        private const int CharacterId = 983801;
        private const int LotteryItemId = 7_654_321;
        private const int RewardItemId = 1004;
        private const short LotterySlot = 105;
        private const int InitialGold = 500;
        private const int ResetGoldCost = 100;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "lottery-progress-reset-"
                    + Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(database);
                var service = CreateService(database);
                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    database);
                inventory.SetListParam16(
                    InventoryListType.Main,
                    ItemSlotBoundService.MainExpandStageFull);
                var lotteryItem = ItemCore.Create(
                    ItemCore.KindConsumable,
                    LotteryItemId);
                lotteryItem.Count = 1;
                inventory.SetItem(
                    InventoryListType.Main,
                    LotterySlot,
                    lotteryItem);
                inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    InitialGold);
                var fixtureLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                Check(
                    "lottery reset fixture persists source and gold",
                    InventoryPersistenceService.SaveDirty(fixtureLease),
                    ref failures);
                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);

                SeedProgress(database);
                CreateProgressDeleteFailureTrigger(databasePath);
                var progressFailed = !service.TryResetProgress(
                    lease,
                    AccountId,
                    LotterySlot,
                    LotteryItemId,
                    out var failedProgress,
                    out var failedProgressGold);
                Check(
                    "lottery reset rejects progress delete failure",
                    progressFailed
                    && failedProgress == null
                    && failedProgressGold == InitialGold,
                    ref failures);
                Check(
                    "lottery progress failure restores online and database gold",
                    lease.Inventory.CountMainItem(0) == InitialGold
                    && LoadPersistedGold(database) == InitialGold
                    && lease.Inventory.DirtyMainVirtualCountSlots.Count == 0,
                    ref failures);
                Check(
                    "lottery progress failure preserves claimed indexes",
                    CountProgressRows(database) == 2,
                    ref failures);

                DropFailureTriggers(databasePath);
                var firstRetry = service.TryResetProgress(
                    lease,
                    AccountId,
                    LotterySlot,
                    LotteryItemId,
                    out var firstRetryProgress,
                    out var firstRetryGold);
                Check(
                    "lottery reset retries after progress recovery",
                    firstRetry
                    && firstRetryProgress != null
                    && firstRetryProgress.NewRewardIndex == -1
                    && firstRetryGold == InitialGold - ResetGoldCost
                    && lease.Inventory.CountMainItem(0)
                        == InitialGold - ResetGoldCost
                    && LoadPersistedGold(database)
                        == InitialGold - ResetGoldCost
                    && CountProgressRows(database) == 0,
                    ref failures);

                ResetFixtureForGoldFailure(database, lease);
                CreateGoldUpdateFailureTrigger(databasePath);
                var goldFailed = !service.TryResetProgress(
                    lease,
                    AccountId,
                    LotterySlot,
                    LotteryItemId,
                    out var failedGoldProgress,
                    out var failedGold);
                Check(
                    "lottery reset rejects gold persistence failure",
                    goldFailed
                    && failedGoldProgress == null
                    && failedGold == InitialGold,
                    ref failures);
                Check(
                    "lottery gold failure restores gold and progress together",
                    lease.Inventory.CountMainItem(0) == InitialGold
                    && LoadPersistedGold(database) == InitialGold
                    && CountProgressRows(database) == 2
                    && lease.Inventory.DirtyMainVirtualCountSlots.Count == 0,
                    ref failures);

                DropFailureTriggers(databasePath);
                var secondRetry = service.TryResetProgress(
                    lease,
                    AccountId,
                    LotterySlot,
                    LotteryItemId,
                    out var secondRetryProgress,
                    out var secondRetryGold);
                Check(
                    "lottery reset commits gold and progress after recovery",
                    secondRetry
                    && secondRetryProgress != null
                    && secondRetryProgress.ItemTemplateId == LotteryItemId
                    && secondRetryGold == InitialGold - ResetGoldCost
                    && LoadPersistedGold(database)
                        == InitialGold - ResetGoldCost
                    && CountProgressRows(database) == 0,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] lottery progress reset transaction selftest threw: "
                        + ex);
                failures++;
            }
            finally
            {
                DropFailureTriggers(databasePath);
                if (lease != null)
                {
                    InventoryContext.Unregister(
                        lease.SessionId,
                        lease.CharacterId);
                }

                DeleteDatabaseFiles(databasePath);
            }

            Console.WriteLine(
                failures == 0
                    ? "LotteryProgressResetTransactionSelfTest OK"
                    : "LotteryProgressResetTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static LotteryItemOpenService CreateService(
            IGameDatabase database)
        {
            var stackable = new PvfLib.StackableItemFile
            {
                Name = "lottery progress reset transaction",
                StackableType = "`[upgradable legacy]` 1",
                ActionTypeName = "[increase chance lottery]",
            };
            stackable.ActionTypeParams.Add(0);
            stackable.ActionTypeParams.Add(9);
            stackable.ActionTypeParams.Add(ResetGoldCost);
            stackable.UpgradableLegacyRewards.Add(
                new PvfLib.BoosterRewardEntry
                {
                    ItemId = RewardItemId,
                    Weight = 10_000,
                    Count = 1,
                });
            var definitions = new LotteryItemDefinitionProvider(
                itemId => itemId == LotteryItemId ? stackable : null);
            var dailyReset = new DailyResetService(database);
            var doublePolicy = new LotteryDoubleRewardPolicy(
                dailyReset,
                database.ConnectionString);
            return new LotteryItemOpenService(
                database.ConnectionString,
                definitions,
                doublePolicy);
        }

        private static void Seed(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, 'lottery-progress-reset', '');
INSERT INTO characters(
    character_id, account_id, name, level, town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes("LotteryProgressReset"));
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void SeedProgress(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT OR IGNORE INTO account_increase_chance_lottery_progress(
    account_id, item_template_id, reward_index)
VALUES(@aid, @item, 1), (@aid, @item, 4);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@item", LotteryItemId);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void ResetFixtureForGoldFailure(
            IGameDatabase database,
            InventoryLease lease)
        {
            lease.Inventory.SetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                InitialGold);
            if (!InventoryPersistenceService.SaveDirty(lease))
            {
                throw new InvalidOperationException(
                    "unable to reset lottery gold fixture");
            }

            SeedProgress(database);
        }

        private static void CreateProgressDeleteFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_lottery_progress_reset_delete
BEFORE DELETE ON account_increase_chance_lottery_progress
WHEN OLD.account_id = {AccountId}
 AND OLD.item_template_id = {LotteryItemId}
BEGIN
    SELECT RAISE(ABORT, 'injected lottery progress delete failure');
END;");
        }

        private static void CreateGoldUpdateFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_lottery_progress_reset_gold
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {InventoryService.MainVirtualCurrencySlotStart}
BEGIN
    SELECT RAISE(ABORT, 'injected lottery reset gold failure');
END;");
        }

        private static void DropFailureTriggers(string databasePath)
        {
            if (!File.Exists(databasePath))
                return;

            try
            {
                ExecuteNonQuery(
                    databasePath,
                    @"
DROP TRIGGER IF EXISTS fail_lottery_progress_reset_delete;
DROP TRIGGER IF EXISTS fail_lottery_progress_reset_gold;");
            }
            catch
            {
            }
        }

        private static int LoadPersistedGold(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            {
                return InventoryMainVirtualCountRepository.LoadCurrencyCount(
                    connection,
                    null,
                    CharacterId,
                    InventoryService.MainVirtualCurrencySlotStart);
            }
        }

        private static int CountProgressRows(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*)
FROM account_increase_chance_lottery_progress
WHERE account_id=@aid AND item_template_id=@item;";
                command.Parameters.AddWithValue("@aid", AccountId);
                command.Parameters.AddWithValue("@item", LotteryItemId);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static void ExecuteNonQuery(
            string databasePath,
            string sql)
        {
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    ForeignKeys = true,
                }.ToString()))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                "[" + (condition ? "PASS" : "FAIL") + "] " + label);
            if (!condition)
                failures++;
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var path = databasePath + suffix;
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }
    }
}
