using DfoServer.Game.CraneMiniGame;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class CraneStartTransactionSelfTest
    {
        private const int AccountId = 983500;
        private const int CharacterId = 983501;
        private const int MaterialItemId = 2660547;
        private const short MaterialSlot = 120;
        private const ushort MachineId = 140;
        private const int InitialMaterialCount = 2;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "crane-start-transaction-" +
                Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(database);

                var catalog = CraneMiniGameCatalog.Parse(BuildCatalogText());
                var startService = new CraneMiniGameStartService(catalog);
                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    database);
                var material = InventoryCreateService.CreateCore(
                    ItemCore.KindMaterial,
                    MaterialItemId,
                    ItemCreateReason.AdminGrant,
                    InitialMaterialCount);
                material.Count = InitialMaterialCount;
                if (!inventory.SetItem(
                        InventoryListType.Main,
                        MaterialSlot,
                        material))
                {
                    throw new InvalidOperationException(
                        "unable to prepare crane material");
                }

                var fixtureLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                Check(
                    "crane start fixture persists material",
                    InventoryPersistenceService.SaveDirty(fixtureLease),
                    ref failures);
                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);

                CreateMaterialUpdateFailureTrigger(databasePath);
                var failed = !CraneMiniGameStartCommitService.TryStart(
                    lease,
                    MachineId,
                    startService,
                    out var failedResult);
                var sessions = new CraneMiniGameSessionCoordinator();
                if (!failed)
                    sessions.Set(lease.SessionId, failedResult);
                Check(
                    "crane start rejects material persistence failure",
                    failed
                    && failedResult != null
                    && failedResult.MaterialRemainingCount == 1,
                    ref failures);
                Check(
                    "crane failure reloads material and dirty state",
                    lease.Inventory.CountMainItem(MaterialItemId)
                        == InitialMaterialCount
                    && !lease.Inventory.DirtyListTypes.Contains(
                        InventoryListType.Main),
                    ref failures);
                Check(
                    "crane failure leaves database and pending session unchanged",
                    LoadPersistedMaterialCount(database)
                        == InitialMaterialCount
                    && !sessions.TryGet(lease.SessionId, out _),
                    ref failures);

                DropFailureTriggers(databasePath);
                var retried = CraneMiniGameStartCommitService.TryStart(
                    lease,
                    MachineId,
                    startService,
                    out var retryResult);
                if (retried)
                    sessions.Set(lease.SessionId, retryResult);
                Check(
                    "crane start retries after persistence recovery",
                    retried
                    && retryResult != null
                    && retryResult.MachineId == MachineId
                    && retryResult.MaterialSlot == MaterialSlot
                    && retryResult.MaterialRemainingCount == 1
                    && retryResult.DisplayItems.Count == catalog.ViewCount,
                    ref failures);
                Check(
                    "crane retry commits material and pending session",
                    lease.Inventory.CountMainItem(MaterialItemId) == 1
                    && LoadPersistedMaterialCount(database) == 1
                    && sessions.TryGet(lease.SessionId, out var pending)
                    && ReferenceEquals(pending, retryResult),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] crane start transaction selftest threw: " + ex);
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
                    ? "CraneStartTransactionSelfTest OK"
                    : "CraneStartTransactionSelfTest FAIL ("
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
VALUES(@aid, 'crane-start-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level, town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes("CraneStartTransaction"));
                    command.ExecuteNonQuery();
                }
            });
        }

        private static string BuildCatalogText()
        {
            var text = "[viewCnt]\n6\n";
            for (var index = 1; index <= 7; index++)
            {
                text += $"[item]\n{10000000 + index}\n[cnt]\n1\n"
                    + "[viewRatio]\n10\n[pickRatio]\n90\n";
            }

            return text
                + $"[material]\n{MaterialItemId}\t1\n"
                + "[need material]\n3333\t3\n[/need material]\n";
        }

        private static void CreateMaterialUpdateFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_crane_start_material_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {MaterialSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected crane start material failure');
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
                    "DROP TRIGGER IF EXISTS fail_crane_start_material_update;");
            }
            catch
            {
            }
        }

        private static int LoadPersistedMaterialCount(
            IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    database);
                return inventory.CountMainItem(MaterialItemId);
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
