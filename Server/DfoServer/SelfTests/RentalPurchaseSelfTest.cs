using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class RentalPurchaseSelfTest
    {
        private const int AccountId = 929000;
        private const int CharacterId = 929001;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== RENTAL_PURCHASE selftest ===");

            if (!TryFindRentalTemplate(out var shopEntryId, out var inventoryTemplateId))
            {
                Check("PVF rental template found", false);
                PrintSummary();
                return 1;
            }

            Check("PVF rental template found", inventoryTemplateId > 0);

            var databasePath = Path.Combine(Path.GetTempPath(), "rental_purchase_selftest.db");
            DeleteDatabase(databasePath);
            var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
            Seed(database.ConnectionString);

            var characterRepository = new SqliteCharacterRepository(database);
            var dataSource = new SqliteSelectCharacterDataSource(database, characterRepository);
            InventoryLease lease = null;
            try
            {
                var inventory = LoadInventory(database);
                lease = InventoryContext.Register(Guid.NewGuid(), inventory);

                Check("failed rental purchase rolls back all three stores",
                    !ApplyRentalPurchase(
                        database,
                        dataSource,
                        lease,
                        shopEntryId,
                        inventoryTemplateId,
                        commit: false)
                    && LoadLuckyStar(database.ConnectionString) == 10
                    && LoadRentalCount(database.ConnectionString) == 0
                    && lease.Inventory.CountMainItem(inventoryTemplateId) == 0);

                Check("successful rental purchase commits all three stores",
                    ApplyRentalPurchase(
                        database,
                        dataSource,
                        lease,
                        shopEntryId,
                        inventoryTemplateId,
                        commit: true)
                    && LoadLuckyStar(database.ConnectionString) == 9
                    && LoadRentalCount(database.ConnectionString) == 1
                    && lease.Inventory.CountMainItem(inventoryTemplateId) == 1);
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
            }

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static bool ApplyRentalPurchase(
            IGameDatabase database,
            SqliteSelectCharacterDataSource dataSource,
            InventoryLease lease,
            uint shopEntryId,
            int inventoryTemplateId,
            bool commit)
        {
            var expireTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400;
            var result = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                commit ? "rental-purchase-selftest-success" : "rental-purchase-selftest-rollback",
                (connection, transaction) =>
                {
                    var wallet = CurrencyService.LoadWallet(connection, transaction, CharacterId);
                    if (!CurrencyService.TrySpendLuckyStar(connection, transaction, AccountId, 1))
                        return false;

                    var rental = dataSource.LoadRentalInfo(connection, transaction, CharacterId);
                    rental.UpsertItem(shopEntryId, unchecked((uint)inventoryTemplateId), unchecked((uint)expireTime));
                    dataSource.SaveRentalInfo(connection, transaction, CharacterId, rental);

                    if (!InventoryShopRuntimeService.TryRentWeapon(
                            lease.Inventory,
                            inventoryTemplateId,
                            expireTime,
                            out _,
                            connection,
                            transaction))
                        return false;

                    return commit && wallet.LuckyStar == 10;
                });

            return result;
        }

        private static bool TryFindRentalTemplate(out uint shopEntryId, out int inventoryTemplateId)
        {
            shopEntryId = 0;
            inventoryTemplateId = 0;
            try
            {
                var list = LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst"));
                foreach (var entry in list.Entries)
                {
                    if (entry == null
                        || entry.Id <= 0
                        || entry.FilePath.IndexOf("chn_rental_", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (!RentalWeaponInventoryMapper.IsValidInventoryTemplate(entry.Id))
                        continue;

                    shopEntryId = unchecked((uint)entry.Id);
                    inventoryTemplateId = entry.Id;
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[RentalPurchaseSelfTest] rental template lookup failed: {ex.Message}");
            }

            return false;
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
INSERT INTO accounts(account_id, m_id, password_hash, lucky_star)
VALUES(@aid, 'rental-purchase-selftest', '', 10);
INSERT INTO characters(character_id, account_id, name, level)
VALUES(@cid, @aid, 'rental-purchase-main', 60);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
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

        private static int LoadRentalCount(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM character_rental_items WHERE character_id=@cid;";
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

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}
