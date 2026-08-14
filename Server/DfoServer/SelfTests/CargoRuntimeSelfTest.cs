using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class CargoRuntimeSelfTest
    {
        private const int AccountId = 927000;
        private const int CharacterId = 927001;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== CARGO_RUNTIME selftest ===");

            var databasePath = Path.Combine(Path.GetTempPath(), "cargo_runtime_selftest.db");
            DeleteDatabase(databasePath);
            var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
            Seed(database.ConnectionString);

            InventoryLease lease = null;
            try
            {
                var inventory = LoadInventory(database);
                lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                inventory.SetListParam16(InventoryListType.AccountCargo, 8);
                Check("initial account cargo state saves", InventoryPersistenceService.SaveDirty(lease));

                Check("failed account cargo Cera upgrade rolls back together",
                    !OnlineInventoryMutationCommitCoordinator.TryCommit(
                        lease,
                        "cargo-runtime-selftest-rollback",
                        (connection, transaction) =>
                        {
                            var ok = InventoryCargoRuntimeService.TryUpgradeAccountCargo(
                                lease.Inventory,
                                connection,
                                transaction,
                                out _,
                                out _);
                            return ok && false;
                        })
                    && LoadCera(database.ConnectionString) == 2000
                    && lease.Inventory.AccountCargo.SelectionKey == 8);

                Check("successful account cargo Cera upgrade commits payment and capacity",
                    OnlineInventoryMutationCommitCoordinator.TryCommit(
                        lease,
                        "cargo-runtime-selftest-success",
                        (connection, transaction) => InventoryCargoRuntimeService.TryUpgradeAccountCargo(
                            lease.Inventory,
                            connection,
                            transaction,
                            out _,
                            out _))
                    && LoadCera(database.ConnectionString) == 0
                    && LoadAccountCargoSelection(database.ConnectionString) == 16
                    && lease.Inventory.AccountCargo.SelectionKey == 16);
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
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash, cera)
VALUES(@aid, 'cargo-runtime-selftest', '', 2000);
INSERT INTO characters(character_id, account_id, name, level)
VALUES(@cid, @aid, 'cargo-runtime-main', 60);
INSERT INTO account_cargo_state(account_id, selection_key, value32, item_count)
VALUES(@aid, 8, 0, 0);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
            }
        }

        private static int LoadCera(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT cera FROM accounts WHERE account_id=@aid;";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static int LoadAccountCargoSelection(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT selection_key FROM account_cargo_state WHERE account_id=@aid;";
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
