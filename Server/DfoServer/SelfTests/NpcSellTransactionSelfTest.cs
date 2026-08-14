using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class NpcSellTransactionSelfTest
    {
        private const int AccountId = 983100;
        private const int CharacterId = 983101;
        private const int SellItemId = 1004;
        private const int InitialItemCount = 3;
        private const int InitialGold = 1_000;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "npc-sell-transaction-" +
                Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;
            var previousDatabasePath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");

            try
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    databasePath);
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(database);

                var metadata = ItemMetadataResolver.Resolve(SellItemId);
                Check(
                    "NPC sell fixture resolves a sellable stackable item",
                    metadata != null
                    && metadata.IsStackable
                    && metadata.SellGold > 0,
                    ref failures);
                if (metadata == null
                    || !metadata.IsStackable
                    || metadata.SellGold <= 0)
                {
                    return 1;
                }

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    database);
                var granted = InventoryRewardGrantService.TryCreateAndInsert(
                    inventory,
                    SellItemId,
                    ItemCreateReason.AdminGrant,
                    InitialItemCount,
                    out var grant);
                inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    InitialGold);
                var fixtureLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                Check(
                    "NPC sell fixture persists item and gold",
                    granted
                    && grant != null
                    && grant.Success
                    && InventoryPersistenceService.SaveDirty(fixtureLease),
                    ref failures);
                if (!granted || grant == null || !grant.Success)
                    return 1;

                var slotIndex = grant.SlotIndex;
                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);

                CreateItemUpdateFailureTrigger(databasePath, slotIndex);
                InventoryMutationResult failedPartialResult = null;
                var failedPartialCode = 1;
                var failedPartial = !OnlineInventoryMutationCommitCoordinator
                    .TryCommit(
                        lease,
                        "npc-sell-partial-selftest-failure",
                        (connection, transaction) =>
                        {
                            failedPartialCode = InventoryShopRuntimeService
                                .TrySellItem(
                                    lease.Inventory,
                                    InventoryListType.Main,
                                    slotIndex,
                                    1,
                                    out failedPartialResult);
                            return failedPartialCode == 0;
                        });
                Check(
                    "partial NPC sale rejects an item update failure",
                    failedPartial && failedPartialCode == 0,
                    ref failures);
                Check(
                    "partial sale failure reloads item, gold and dirty state",
                    lease.Inventory.CountMainItem(SellItemId)
                        == InitialItemCount
                    && lease.Inventory.CountMainItem(0) == InitialGold
                    && !lease.Inventory.DirtyListTypes.Contains(
                        InventoryListType.Main)
                    && lease.Inventory.DirtyMainVirtualCountSlots.Count == 0,
                    ref failures);
                Check(
                    "partial sale failure leaves database unchanged",
                    LoadPersistedCount(databasePath, SellItemId)
                        == InitialItemCount
                    && LoadPersistedCount(databasePath, 0) == InitialGold,
                    ref failures);

                DropFailureTriggers(databasePath);
                InventoryMutationResult partialRetryResult = null;
                var partialRetryCode = 1;
                var partialRetried = OnlineInventoryMutationCommitCoordinator
                    .TryCommit(
                        lease,
                        "npc-sell-partial-selftest-retry",
                        (connection, transaction) =>
                        {
                            partialRetryCode = InventoryShopRuntimeService
                                .TrySellItem(
                                    lease.Inventory,
                                    InventoryListType.Main,
                                    slotIndex,
                                    1,
                                    out partialRetryResult);
                            return partialRetryCode == 0;
                        });
                var partialGold = InitialGold + metadata.SellGold;
                Check(
                    "partial NPC sale retries after persistence recovery",
                    partialRetried
                    && partialRetryCode == 0
                    && partialRetryResult != null
                    && partialRetryResult.AppliedCount == 1,
                    ref failures);
                Check(
                    "partial sale retry commits online item and gold",
                    lease.Inventory.CountMainItem(SellItemId)
                        == InitialItemCount - 1
                    && lease.Inventory.CountMainItem(0) == partialGold,
                    ref failures);
                Check(
                    "partial sale retry commits database item and gold",
                    LoadPersistedCount(databasePath, SellItemId)
                        == InitialItemCount - 1
                    && LoadPersistedCount(databasePath, 0) == partialGold,
                    ref failures);

                CreateItemDeleteFailureTrigger(databasePath, slotIndex);
                InventoryMutationResult failedDeleteResult = null;
                var failedDeleteCode = 1;
                var failedDelete = !OnlineInventoryMutationCommitCoordinator
                    .TryCommit(
                        lease,
                        "npc-sell-delete-selftest-failure",
                        (connection, transaction) =>
                        {
                            failedDeleteCode = InventoryShopRuntimeService
                                .TrySellItem(
                                    lease.Inventory,
                                    InventoryListType.Main,
                                    slotIndex,
                                    0,
                                    out failedDeleteResult);
                            return failedDeleteCode == 0;
                        });
                Check(
                    "full NPC sale rejects an item delete failure",
                    failedDelete && failedDeleteCode == 0,
                    ref failures);
                Check(
                    "delete failure reloads remaining item and gold",
                    lease.Inventory.CountMainItem(SellItemId)
                        == InitialItemCount - 1
                    && lease.Inventory.CountMainItem(0) == partialGold
                    && !lease.Inventory.DirtyListTypes.Contains(
                        InventoryListType.Main)
                    && lease.Inventory.DirtyMainVirtualCountSlots.Count == 0,
                    ref failures);
                Check(
                    "delete failure leaves database unchanged",
                    LoadPersistedCount(databasePath, SellItemId)
                        == InitialItemCount - 1
                    && LoadPersistedCount(databasePath, 0) == partialGold,
                    ref failures);

                DropFailureTriggers(databasePath);
                InventoryMutationResult deleteRetryResult = null;
                var deleteRetryCode = 1;
                var deleteRetried = OnlineInventoryMutationCommitCoordinator
                    .TryCommit(
                        lease,
                        "npc-sell-delete-selftest-retry",
                        (connection, transaction) =>
                        {
                            deleteRetryCode = InventoryShopRuntimeService
                                .TrySellItem(
                                    lease.Inventory,
                                    InventoryListType.Main,
                                    slotIndex,
                                    0,
                                    out deleteRetryResult);
                            return deleteRetryCode == 0;
                        });
                var finalGold = partialGold
                    + metadata.SellGold * (InitialItemCount - 1);
                Check(
                    "full NPC sale retries and commits after recovery",
                    deleteRetried
                    && deleteRetryCode == 0
                    && deleteRetryResult != null
                    && deleteRetryResult.AppliedCount
                        == InitialItemCount - 1
                    && lease.Inventory.CountMainItem(SellItemId) == 0
                    && lease.Inventory.CountMainItem(0) == finalGold
                    && LoadPersistedCount(databasePath, SellItemId) == 0
                    && LoadPersistedCount(databasePath, 0) == finalGold,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] NPC sell transaction selftest threw: " + ex);
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

                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);
                DeleteDatabaseFiles(databasePath);
            }

            Console.WriteLine(
                failures == 0
                    ? "NpcSellTransactionSelfTest OK"
                    : "NpcSellTransactionSelfTest FAIL (" + failures + ")");
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
VALUES(@aid, 'npc-sell-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level, town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 1, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes("NpcSellTransaction"));
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
CREATE TRIGGER fail_npc_sell_inventory_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {slotIndex}
BEGIN
    SELECT RAISE(ABORT, 'injected NPC sell item update failure');
END;");
        }

        private static void CreateItemDeleteFailureTrigger(
            string databasePath,
            short slotIndex)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_npc_sell_inventory_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {slotIndex}
BEGIN
    SELECT RAISE(ABORT, 'injected NPC sell item delete failure');
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
DROP TRIGGER IF EXISTS fail_npc_sell_inventory_update;
DROP TRIGGER IF EXISTS fail_npc_sell_inventory_delete;");
            }
            catch
            {
            }
        }

        private static void ExecuteNonQuery(
            string databasePath,
            string sql)
        {
            using (var connection = OpenConnection(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static int LoadPersistedCount(
            string databasePath,
            int itemId)
        {
            using (var connection = OpenConnection(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_core
FROM character_inventory_items
WHERE character_id = @cid
  AND list_type = @listType;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue(
                    "@listType",
                    (int)InventoryListType.Main);
                using (var reader = command.ExecuteReader())
                {
                    var total = 0;
                    while (reader.Read())
                    {
                        var core = ItemCore.FromBytes((byte[])reader[0]);
                        if (core.ItemId == itemId)
                            total += core.Count;
                    }

                    return total;
                }
            }
        }

        private static SqliteConnection OpenConnection(string databasePath)
        {
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    ForeignKeys = true,
                }.ToString());
            connection.Open();
            return connection;
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
