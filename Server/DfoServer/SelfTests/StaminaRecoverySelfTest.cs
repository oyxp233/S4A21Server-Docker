using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class StaminaRecoverySelfTest
    {
        private const int AccountId = 930000;
        private const int CharacterId = 930001;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== STAMINA_RECOVERY selftest ===");

            var databasePath = Path.Combine(Path.GetTempPath(), "stamina_recovery_selftest.db");
            DeleteDatabase(databasePath);
            var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
            var cost = StaminaHandler.CalculateRecoverStaminaGoldCost(60, 10);
            Seed(database.ConnectionString, cost);

            InventoryLease lease = null;
            try
            {
                var inventory = LoadInventory(database);
                lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                Check("initial stamina state loads", LoadStamina(database.ConnectionString) == 10);
                Check("initial gold loads", LoadGold(database.ConnectionString) == cost + 100000);

                Check("failed stamina recovery rolls back gold and subtype0",
                    !ApplyRecovery(lease, cost, commit: false)
                    && LoadGold(database.ConnectionString) == cost + 100000
                    && LoadStamina(database.ConnectionString) == 10);

                Check("successful stamina recovery commits gold and subtype0",
                    ApplyRecovery(lease, cost, commit: true)
                    && LoadGold(database.ConnectionString) == 100000
                    && LoadStamina(database.ConnectionString) == 0
                    && LoadFatiguePenalty(database.ConnectionString) == 0);
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
            }

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static bool ApplyRecovery(InventoryLease lease, int cost, bool commit)
        {
            return OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                commit ? "stamina-selftest-success" : "stamina-selftest-rollback",
                (connection, transaction) =>
                {
                    if (!lease.Inventory.TryConsumeMainItem(
                            InventoryService.MainVirtualCurrencySlotStart,
                            cost,
                            out var consumed)
                        || !consumed.Success)
                        return false;

                    if (!SqliteSubtype0FieldsRepository.ResetStaminaInTransaction(
                            connection,
                            transaction,
                            CharacterId))
                        return false;

                    return commit;
                });
        }

        private static InventoryService LoadInventory(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
                return InventoryService.LoadFromDb(connection, CharacterId, AccountId, database);
        }

        private static void Seed(string connectionString, int cost)
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
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, 'stamina-recovery-selftest', '');
INSERT INTO characters(character_id, account_id, name, level)
VALUES(@cid, @aid, 'stamina-recovery-main', 60);";
                        command.Parameters.AddWithValue("@aid", AccountId);
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.ExecuteNonQuery();
                    }

                    InventoryMainVirtualCountRepository.UpsertCurrencySlot(
                        connection,
                        transaction,
                        CharacterId,
                        InventoryService.MainVirtualCurrencySlotStart,
                        cost + 100000);

                    using (var subtypeCommand = connection.CreateCommand())
                    {
                        subtypeCommand.Transaction = transaction;
                        subtypeCommand.CommandText = @"
INSERT INTO character_subtype0_fields(character_id, stamina, fatigue_penalty)
VALUES(@cid, 10, 1234);";
                        subtypeCommand.Parameters.AddWithValue("@cid", CharacterId);
                        subtypeCommand.ExecuteNonQuery();
                    }
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

        private static int LoadStamina(string connectionString)
        {
            return LoadSubtype0(connectionString, "stamina");
        }

        private static int LoadFatiguePenalty(string connectionString)
        {
            return LoadSubtype0(connectionString, "fatigue_penalty");
        }

        private static int LoadSubtype0(string connectionString, string column)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"SELECT {column} FROM character_subtype0_fields WHERE character_id=@cid;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
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
