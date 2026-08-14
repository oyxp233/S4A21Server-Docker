using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class StackableUseTransactionSelfTest
    {
        private const int AccountId = 984200;
        private const int CharacterId = 984201;
        private const int ItemTemplateId = 1004;
        private const short ItemSlot = 105;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "stackable-use-transaction-"
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
                SetStack(inventory, 2);
                var fixtureLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                Check(
                    "stackable use fixture persists source stack",
                    InventoryPersistenceService.SaveDirty(fixtureLease)
                    && LoadPersistedCount(database) == 2,
                    ref failures);
                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);

                var mismatchRejected = !InventoryDeleteCommitService
                    .TryCommitStackableUse(
                        lease,
                        InventoryListType.Main,
                        ItemSlot,
                        ItemTemplateId + 1,
                        out var mismatchMutation);
                Check(
                    "stackable use rejects an expected item mismatch without mutation",
                    mismatchRejected
                    && mismatchMutation == null
                    && GetOnlineCount(lease) == 2
                    && LoadPersistedCount(database) == 2
                    && lease.Inventory.DirtyListTypes.Count == 0,
                    ref failures);

                CreateItemUpdateFailureTrigger(databasePath);
                var updateFailed = !InventoryDeleteCommitService
                    .TryCommitStackableUse(
                        lease,
                        InventoryListType.Main,
                        ItemSlot,
                        ItemTemplateId,
                        out var failedUpdateMutation);
                Check(
                    "stackable use rejects item update persistence failure",
                    updateFailed && failedUpdateMutation == null,
                    ref failures);
                Check(
                    "stackable update failure restores online and database count",
                    GetOnlineCount(lease) == 2
                    && LoadPersistedCount(database) == 2
                    && lease.Inventory.DirtyListTypes.Count == 0,
                    ref failures);

                DropFailureTriggers(databasePath);
                var updateRetried = InventoryDeleteCommitService
                    .TryCommitStackableUse(
                        lease,
                        InventoryListType.Main,
                        ItemSlot,
                        ItemTemplateId,
                        out var updateMutation);
                Check(
                    "stackable use retries update after recovery",
                    updateRetried
                    && updateMutation != null
                    && updateMutation.RemainingStackCount == 1
                    && GetOnlineCount(lease) == 1
                    && LoadPersistedCount(database) == 1,
                    ref failures);

                CreateItemDeleteFailureTrigger(databasePath);
                var deleteFailed = !InventoryDeleteCommitService
                    .TryCommitStackableUse(
                        lease,
                        InventoryListType.Main,
                        ItemSlot,
                        ItemTemplateId,
                        out var failedDeleteMutation);
                Check(
                    "stackable use rejects final item delete persistence failure",
                    deleteFailed
                    && failedDeleteMutation == null
                    && GetOnlineCount(lease) == 1
                    && LoadPersistedCount(database) == 1
                    && lease.Inventory.DirtyListTypes.Count == 0,
                    ref failures);

                DropFailureTriggers(databasePath);
                var deleteRetried = InventoryDeleteCommitService
                    .TryCommitStackableUse(
                        lease,
                        InventoryListType.Main,
                        ItemSlot,
                        ItemTemplateId,
                        out var deleteMutation);
                Check(
                    "stackable use commits final item after recovery",
                    deleteRetried
                    && deleteMutation != null
                    && deleteMutation.RemainingStackCount == 0
                    && GetOnlineCount(lease) == 0
                    && LoadPersistedCount(database) == 0,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] stackable use transaction selftest threw: " + ex);
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
                    ? "StackableUseTransactionSelfTest OK"
                    : "StackableUseTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void SetStack(
            InventoryService inventory,
            int count)
        {
            var item = ItemCore.Create(
                ItemCore.KindConsumable,
                ItemTemplateId);
            item.Count = count;
            if (!inventory.SetItem(
                    InventoryListType.Main,
                    ItemSlot,
                    item))
            {
                throw new InvalidOperationException(
                    "unable to seed stackable use fixture");
            }
        }

        private static int GetOnlineCount(InventoryLease lease)
        {
            return lease.Inventory.GetItem(
                InventoryListType.Main,
                ItemSlot)?.Count ?? 0;
        }

        private static int LoadPersistedCount(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    database);
                return inventory.GetItem(
                    InventoryListType.Main,
                    ItemSlot)?.Count ?? 0;
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
VALUES(@aid, 'stackable-use-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes("StackableUseTransaction"));
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void CreateItemUpdateFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_stackable_use_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {ItemSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected stackable use update failure');
END;");
        }

        private static void CreateItemDeleteFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_stackable_use_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {ItemSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected stackable use delete failure');
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
DROP TRIGGER IF EXISTS fail_stackable_use_update;
DROP TRIGGER IF EXISTS fail_stackable_use_delete;");
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
