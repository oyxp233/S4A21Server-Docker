using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class InventoryDeleteTransactionSelfTest
    {
        private const int AccountId = 984100;
        private const int CharacterId = 984101;
        private const int ItemTemplateId = 1004;
        private const short FirstSlot = 105;
        private const short SecondSlot = 106;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "inventory-delete-transaction-"
                    + Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(database);
                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    database);
                inventory.SetListParam16(
                    InventoryListType.Main,
                    ItemSlotBoundService.MainExpandStageFull);
                SetStack(inventory, FirstSlot, 3);
                var fixtureLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                Check(
                    "delete fixture persists source stack",
                    InventoryPersistenceService.SaveDirty(fixtureLease)
                    && LoadSlotCount(database, FirstSlot) == 3,
                    ref failures);
                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);

                CreateItemUpdateFailureTrigger(databasePath, FirstSlot);
                var updateFailed = !InventoryDeleteCommitService.TryCommit(
                    lease,
                    InventoryListType.Main,
                    FirstSlot,
                    1,
                    out var failedUpdateMutation);
                Check(
                    "delete rejects stack update persistence failure",
                    updateFailed && failedUpdateMutation == null,
                    ref failures);
                Check(
                    "stack update failure restores online and database count",
                    GetSlotCount(lease.Inventory, FirstSlot) == 3
                    && LoadSlotCount(database, FirstSlot) == 3
                    && lease.Inventory.DirtyListTypes.Count == 0,
                    ref failures);

                DropFailureTriggers(databasePath);
                var updateRetried = InventoryDeleteCommitService.TryCommit(
                    lease,
                    InventoryListType.Main,
                    FirstSlot,
                    1,
                    out var updateMutation);
                Check(
                    "delete retries stack update after recovery",
                    updateRetried
                    && updateMutation != null
                    && updateMutation.RemainingStackCount == 2
                    && GetSlotCount(lease.Inventory, FirstSlot) == 2
                    && LoadSlotCount(database, FirstSlot) == 2,
                    ref failures);

                ResetPartialBatchFixture(lease);
                Check(
                    "extended delete fixture persists two independent entries",
                    GetSlotCount(lease.Inventory, FirstSlot) == 1
                    && GetSlotCount(lease.Inventory, SecondSlot) == 1
                    && LoadSlotCount(database, FirstSlot) == 1
                    && LoadSlotCount(database, SecondSlot) == 1,
                    ref failures);

                CreateItemDeleteFailureTrigger(databasePath, SecondSlot);
                var firstCommitted = InventoryDeleteCommitService.TryCommit(
                    lease,
                    InventoryListType.Main,
                    FirstSlot,
                    1,
                    out var firstMutation);
                Check(
                    "extended delete commits the first entry independently",
                    firstCommitted
                    && firstMutation != null
                    && firstMutation.RemainingStackCount == 0
                    && GetSlotCount(lease.Inventory, FirstSlot) == 0
                    && LoadSlotCount(database, FirstSlot) == 0,
                    ref failures);

                var secondFailed = !InventoryDeleteCommitService.TryCommit(
                    lease,
                    InventoryListType.Main,
                    SecondSlot,
                    1,
                    out var failedSecondMutation);
                Check(
                    "extended delete failure preserves prior success and restores current entry",
                    secondFailed
                    && failedSecondMutation == null
                    && GetSlotCount(lease.Inventory, FirstSlot) == 0
                    && GetSlotCount(lease.Inventory, SecondSlot) == 1
                    && LoadSlotCount(database, FirstSlot) == 0
                    && LoadSlotCount(database, SecondSlot) == 1,
                    ref failures);

                DropFailureTriggers(databasePath);
                var secondRetried = InventoryDeleteCommitService.TryCommit(
                    lease,
                    InventoryListType.Main,
                    SecondSlot,
                    1,
                    out var secondMutation);
                Check(
                    "extended delete retries only the failed entry",
                    secondRetried
                    && secondMutation != null
                    && secondMutation.RemainingStackCount == 0
                    && GetSlotCount(lease.Inventory, FirstSlot) == 0
                    && GetSlotCount(lease.Inventory, SecondSlot) == 0
                    && LoadSlotCount(database, FirstSlot) == 0
                    && LoadSlotCount(database, SecondSlot) == 0,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] inventory delete transaction selftest threw: " + ex);
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
                    ? "InventoryDeleteTransactionSelfTest OK"
                    : "InventoryDeleteTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void ResetPartialBatchFixture(InventoryLease lease)
        {
            SetStack(lease.Inventory, FirstSlot, 1);
            SetStack(lease.Inventory, SecondSlot, 1);
            if (!InventoryPersistenceService.SaveDirty(lease))
            {
                throw new InvalidOperationException(
                    "unable to persist extended delete fixture");
            }
        }

        private static void SetStack(
            InventoryService inventory,
            short slotIndex,
            int count)
        {
            var item = ItemCore.Create(
                ItemCore.KindConsumable,
                ItemTemplateId);
            item.Count = count;
            if (!inventory.SetItem(
                    InventoryListType.Main,
                    slotIndex,
                    item))
            {
                throw new InvalidOperationException(
                    "unable to seed delete fixture slot " + slotIndex);
            }
        }

        private static int GetSlotCount(
            InventoryService inventory,
            short slotIndex)
        {
            return inventory.GetItem(
                InventoryListType.Main,
                slotIndex)?.Count ?? 0;
        }

        private static int LoadSlotCount(
            IGameDatabase database,
            short slotIndex)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    database);
                return GetSlotCount(inventory, slotIndex);
            }
        }

        private static void SeedCharacter(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, 'inventory-delete-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes("InventoryDeleteTransaction"));
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void CreateItemUpdateFailureTrigger(
            string databasePath,
            short slotIndex)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_delete_item_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {slotIndex}
BEGIN
    SELECT RAISE(ABORT, 'injected delete item update failure');
END;");
        }

        private static void CreateItemDeleteFailureTrigger(
            string databasePath,
            short slotIndex)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_delete_item_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {slotIndex}
BEGIN
    SELECT RAISE(ABORT, 'injected delete item delete failure');
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
DROP TRIGGER IF EXISTS fail_delete_item_update;
DROP TRIGGER IF EXISTS fail_delete_item_delete;");
            }
            catch
            {
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
    }
}
