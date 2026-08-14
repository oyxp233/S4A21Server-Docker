using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.ReviveCoin;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class ReviveCoinDailyClaimTransactionSelfTest
    {
        private const int AccountId = 983700;
        private const int CharacterId = 983701;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "revive-coin-daily-claim-"
                    + Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(database);
                var dailyReset = new DailyResetService(database);
                var service = new ReviveCoinService(dailyReset);
                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    database);
                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);

                CreateWalletInsertFailureTrigger(databasePath);
                var failed = !service.TryGrantDaily(
                    lease,
                    out var failedSlot);
                Check(
                    "daily revive coin rejects wallet persistence failure",
                    failed && failedSlot == -1,
                    ref failures);
                Check(
                    "daily revive coin failure reloads online wallet and dirty state",
                    lease.Inventory.CountMainItem(
                        ReviveCoinService.ItemId) == 0
                    && lease.Inventory.DirtyMainVirtualCountSlots.Count == 0,
                    ref failures);
                Check(
                    "daily revive coin failure rolls back flag and database wallet",
                    !dailyReset.IsClaimed(
                        CharacterId,
                        ReviveCoinService.DailyClaimKey)
                    && LoadPersistedWallet(database) == 0,
                    ref failures);

                DropFailureTriggers(databasePath);
                var retried = service.TryGrantDaily(
                    lease,
                    out var retrySlot);
                Check(
                    "daily revive coin retries after persistence recovery",
                    retried
                    && retrySlot == ReviveCoinService.WalletSlot
                    && lease.Inventory.CountMainItem(
                        ReviveCoinService.ItemId) == 1,
                    ref failures);
                Check(
                    "daily revive coin retry commits flag and wallet together",
                    dailyReset.IsClaimed(
                        CharacterId,
                        ReviveCoinService.DailyClaimKey)
                    && LoadPersistedWallet(database) == 1
                    && lease.Inventory.DirtyMainVirtualCountSlots.Count == 0,
                    ref failures);
                Check(
                    "daily revive coin rejects a duplicate claim without mutation",
                    !service.TryGrantDaily(lease, out var duplicateSlot)
                    && duplicateSlot == -1
                    && lease.Inventory.CountMainItem(
                        ReviveCoinService.ItemId) == 1
                    && LoadPersistedWallet(database) == 1,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] revive coin daily claim transaction selftest threw: "
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
                    ? "ReviveCoinDailyClaimTransactionSelfTest OK"
                    : "ReviveCoinDailyClaimTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
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
VALUES(@aid, 'revive-coin-daily-claim', '');
INSERT INTO characters(
    character_id, account_id, name, level, town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes("ReviveCoinDailyClaim"));
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void CreateWalletInsertFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_revive_coin_daily_wallet_insert
BEFORE INSERT ON character_inventory_items
WHEN NEW.character_id = {CharacterId}
 AND NEW.list_type = {(int)InventoryListType.Main}
 AND NEW.slot_index = {ReviveCoinService.WalletSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected daily revive coin wallet failure');
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
                    "DROP TRIGGER IF EXISTS "
                        + "fail_revive_coin_daily_wallet_insert;");
            }
            catch
            {
            }
        }

        private static int LoadPersistedWallet(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            {
                return InventoryMainVirtualCountRepository.LoadCurrencyCount(
                    connection,
                    null,
                    CharacterId,
                    ReviveCoinService.WalletSlot);
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
