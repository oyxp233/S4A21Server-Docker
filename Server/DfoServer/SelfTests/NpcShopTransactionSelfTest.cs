using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class NpcShopTransactionSelfTest
    {
        private const int AccountId = 983000;
        private const int CharacterId = 983001;
        private const int TargetItemId = 3034;
        private const int GoldTargetItemId = 1004;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "npc-shop-transaction-" +
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

                var metadata = ItemMetadataResolver.Resolve(TargetItemId);
                Check(
                    "NPC transaction fixture resolves a material exchange",
                    metadata != null
                    && metadata.NeedMaterialId > 0
                    && metadata.NeedMaterialCount > 0,
                    ref failures);
                if (metadata == null
                    || metadata.NeedMaterialId <= 0
                    || metadata.NeedMaterialCount <= 0)
                {
                    return 1;
                }

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    database);
                var materialGranted = InventoryRewardGrantService.TryCreateAndInsert(
                    inventory,
                    metadata.NeedMaterialId,
                    ItemCreateReason.AdminGrant,
                    metadata.NeedMaterialCount,
                    out _);
                inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    1_000_000);
                Check(
                    "NPC transaction fixture prepares material and gold",
                    materialGranted
                    && InventoryPersistenceService.SaveDirty(
                        new InventoryLease(
                            Guid.NewGuid(),
                            CharacterId,
                            inventory,
                            1)),
                    ref failures);

                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
                var persistedMaterial = inventory.CountMainItem(
                    metadata.NeedMaterialId);
                var persistedGold = inventory.CountMainItem(0);

                CreateFailureTriggers(databasePath);
                InventoryMutationResult failedResult = null;
                var failed = !OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "npc-shop-transaction-selftest-failure",
                    (connection, transaction) =>
                        InventoryShopRuntimeService.TryBuyNpcItem(
                            lease.Inventory,
                            TargetItemId,
                            1,
                            connection,
                            transaction,
                            out failedResult));
                Check(
                    "failed NPC purchase rejects the transaction",
                    failed,
                    ref failures);
                Check(
                    "failed NPC purchase reloads material and gold",
                    lease.Inventory.CountMainItem(metadata.NeedMaterialId)
                        == persistedMaterial
                    && lease.Inventory.CountMainItem(0) == persistedGold
                    && lease.Inventory.CountMainItem(TargetItemId) == 0
                    && !lease.Inventory.DirtyListTypes.Contains(
                        InventoryListType.Main),
                    ref failures);
                Check(
                    "failed NPC purchase leaves database unchanged",
                    LoadPersistedCount(
                        databasePath,
                        metadata.NeedMaterialId) == persistedMaterial
                    && LoadPersistedCount(databasePath, TargetItemId) == 0
                    && LoadPersistedGold(databasePath) == persistedGold,
                    ref failures);

                DropFailureTriggers(databasePath);
                InventoryMutationResult retryResult = null;
                var retried = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "npc-shop-transaction-selftest-retry",
                    (connection, transaction) =>
                        InventoryShopRuntimeService.TryBuyNpcItem(
                            lease.Inventory,
                            TargetItemId,
                            1,
                            connection,
                            transaction,
                            out retryResult));
                Check(
                    "NPC purchase retries after persistence recovery",
                    retried
                    && retryResult != null
                    && retryResult.ItemTemplateId == TargetItemId,
                    ref failures);
                Check(
                    "successful NPC retry commits material and item",
                    lease.Inventory.CountMainItem(metadata.NeedMaterialId) == 0
                    && lease.Inventory.CountMainItem(TargetItemId) == 1
                    && LoadPersistedCount(
                        databasePath,
                        metadata.NeedMaterialId) == 0
                    && LoadPersistedCount(databasePath, TargetItemId) == 1,
                    ref failures);

                var goldMetadata = ItemMetadataResolver.Resolve(
                    GoldTargetItemId);
                Check(
                    "NPC transaction fixture resolves an ordinary gold item",
                    goldMetadata != null
                    && goldMetadata.IsStackable
                    && goldMetadata.BuyGold > 0
                    && goldMetadata.NeedMaterialId == 0,
                    ref failures);
                if (goldMetadata != null
                    && goldMetadata.IsStackable
                    && goldMetadata.BuyGold > 0
                    && goldMetadata.NeedMaterialId == 0)
                {
                    var goldBeforeFailure = lease.Inventory.CountMainItem(0);
                    CreateFailureTriggers(databasePath);
                    InventoryMutationResult failedGoldResult = null;
                    var goldFailed = !OnlineInventoryMutationCommitCoordinator
                        .TryCommit(
                            lease,
                            "npc-shop-gold-selftest-failure",
                            (connection, transaction) =>
                                InventoryShopRuntimeService.TryBuyNpcItem(
                                    lease.Inventory,
                                    GoldTargetItemId,
                                    1,
                                    connection,
                                    transaction,
                                    out failedGoldResult));
                    Check(
                        "failed gold NPC purchase rejects the transaction",
                        goldFailed,
                        ref failures);
                    Check(
                        "failed gold NPC purchase restores gold and item",
                        lease.Inventory.CountMainItem(0) == goldBeforeFailure
                        && lease.Inventory.CountMainItem(
                            GoldTargetItemId) == 0
                        && LoadPersistedGold(databasePath) == goldBeforeFailure
                        && LoadPersistedCount(
                            databasePath,
                            GoldTargetItemId) == 0,
                        ref failures);

                    DropFailureTriggers(databasePath);
                    InventoryMutationResult goldRetryResult = null;
                    var goldRetried = OnlineInventoryMutationCommitCoordinator
                        .TryCommit(
                            lease,
                            "npc-shop-gold-selftest-retry",
                            (connection, transaction) =>
                                InventoryShopRuntimeService.TryBuyNpcItem(
                                    lease.Inventory,
                                    GoldTargetItemId,
                                    1,
                                    connection,
                                    transaction,
                                    out goldRetryResult));
                    Check(
                        "gold NPC purchase retries after persistence recovery",
                        goldRetried
                        && goldRetryResult != null
                        && lease.Inventory.CountMainItem(0)
                            == goldBeforeFailure - goldMetadata.BuyGold
                        && lease.Inventory.CountMainItem(
                            GoldTargetItemId) == 1,
                        ref failures);
                    Check(
                        "successful gold NPC retry persists cost and item",
                        LoadPersistedGold(databasePath)
                            == goldBeforeFailure - goldMetadata.BuyGold
                        && LoadPersistedCount(
                            databasePath,
                            GoldTargetItemId) == 1,
                        ref failures);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] NPC shop transaction selftest threw: " + ex);
                failures++;
            }
            finally
            {
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
                    ? "NpcShopTransactionSelfTest OK"
                    : "NpcShopTransactionSelfTest FAIL (" + failures + ")");
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
VALUES(@aid, 'npc-shop-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level, town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 1, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes("NpcShopTransaction"));
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void CreateFailureTriggers(string databasePath)
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
                    command.CommandText = $@"
CREATE TRIGGER fail_npc_shop_inventory_insert
BEFORE INSERT ON character_inventory_items
WHEN NEW.character_id = {CharacterId}
BEGIN
    SELECT RAISE(ABORT, 'injected NPC shop inventory failure');
END;
CREATE TRIGGER fail_npc_shop_inventory_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN NEW.character_id = {CharacterId}
BEGIN
    SELECT RAISE(ABORT, 'injected NPC shop inventory update failure');
END;
CREATE TRIGGER fail_npc_shop_inventory_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
BEGIN
    SELECT RAISE(ABORT, 'injected NPC shop inventory delete failure');
END;
CREATE TRIGGER fail_npc_shop_cube_update
BEFORE UPDATE OF cube_white ON accounts
WHEN OLD.account_id = {AccountId}
BEGIN
    SELECT RAISE(ABORT, 'injected NPC shop cube update failure');
END;";
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DropFailureTriggers(string databasePath)
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
                    command.CommandText = @"
DROP TRIGGER IF EXISTS fail_npc_shop_inventory_insert;
DROP TRIGGER IF EXISTS fail_npc_shop_inventory_update;
DROP TRIGGER IF EXISTS fail_npc_shop_inventory_delete;
DROP TRIGGER IF EXISTS fail_npc_shop_cube_update;";
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int LoadPersistedCount(
            string databasePath,
            int itemId)
        {
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    ForeignKeys = true,
                }.ToString()))
            {
                connection.Open();
                if (CurrencyService.IsCubeFragment(itemId))
                {
                    foreach (var cube in CurrencyService.LoadCubeFragments(
                                 connection,
                                 null,
                                 AccountId))
                    {
                        if (cube.ItemId == itemId)
                            return cube.Count;
                    }

                    return 0;
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT item_core
FROM character_inventory_items
WHERE character_id = @cid
  AND list_type = 0
LIMIT 100;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
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
        }

        private static int LoadPersistedGold(string databasePath)
        {
            return LoadPersistedCount(databasePath, 0);
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
