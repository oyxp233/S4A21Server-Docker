using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class LuckyStarPurchaseSelfTest
    {
        private const int AccountId = 928000;
        private const int CharacterId = 928001;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== LUCKY_STAR_PURCHASE selftest ===");

            var databasePath = Path.Combine(Path.GetTempPath(), "lucky_star_purchase_selftest.db");
            DeleteDatabase(databasePath);
            var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
            Seed(database.ConnectionString);

            InventoryLease lease = null;
            try
            {
                var inventory = LoadInventory(database);
                lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                Check("initial inventory save succeeds", InventoryPersistenceService.SaveDirty(lease));

                Check("failed LuckyStar transaction rolls back gold and lucky star",
                    !OnlineInventoryMutationCommitCoordinator.TryCommit(
                        lease,
                        "lucky-star-selftest-rollback",
                        (connection, transaction) =>
                        {
                            var wallet = CurrencyService.LoadWallet(connection, transaction, CharacterId);
                            var ok = wallet.LuckyStar == 10
                                && lease.Inventory.TryConsumeMainItem(
                                    InventoryService.MainVirtualCurrencySlotStart,
                                    100000,
                                    out var consumed)
                                && consumed.Success;
                            if (!ok)
                                return false;

                            CurrencyService.GrantLuckyStar(connection, transaction, AccountId, 1);
                            return false;
                        })
                    && LoadGold(database.ConnectionString) == 1_000_000
                    && LoadLuckyStar(database.ConnectionString) == 10
                    && lease.Inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart) == 1_000_000);

                Check("successful LuckyStar transaction commits gold and lucky star",
                    OnlineInventoryMutationCommitCoordinator.TryCommit(
                        lease,
                        "lucky-star-selftest-success",
                        (connection, transaction) =>
                        {
                            var wallet = CurrencyService.LoadWallet(connection, transaction, CharacterId);
                            if (wallet.LuckyStar != 10
                                || !lease.Inventory.TryConsumeMainItem(
                                    InventoryService.MainVirtualCurrencySlotStart,
                                    100000,
                                    out var consumed)
                                || !consumed.Success)
                                return false;

                            CurrencyService.GrantLuckyStar(connection, transaction, AccountId, 1);
                            return true;
                        })
                    && LoadGold(database.ConnectionString) == 900_000
                    && LoadLuckyStar(database.ConnectionString) == 11
                    && lease.Inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart) == 900_000);
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
            }

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static InventoryService LoadInventory(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
                return InventoryService.LoadFromDb(connection, CharacterId, AccountId, database);
        }

        private static void Seed(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash, lucky_star)
VALUES(@aid, 'lucky-star-selftest', '', 10);
INSERT INTO characters(character_id, account_id, name, level)
VALUES(@cid, @aid, 'lucky-star-main', 60);";
                        command.Parameters.AddWithValue("@aid", AccountId);
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.ExecuteNonQuery();
                    }

                    InventoryMainVirtualCountRepository.UpsertCurrencySlot(
                        connection,
                        transaction,
                        CharacterId,
                        InventoryService.MainVirtualCurrencySlotStart,
                        1_000_000);
                    transaction.Commit();
                }
            }
        }

        private static int LoadGold(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                return InventoryMainVirtualCountRepository.LoadCurrencyCount(
                    connection,
                    null,
                    CharacterId,
                    InventoryService.MainVirtualCurrencySlotStart);
            }
        }

        private static int LoadLuckyStar(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT lucky_star FROM accounts WHERE account_id=@aid;";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static void DeleteDatabase(string path)
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok)
                _pass++;
            else
                _fail++;
        }
    }
}
