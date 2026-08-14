using DfoServer.Game.Inventory;
using DfoServer.Game.SecretShop;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class SecretShopTransactionSelfTest
    {
        private const int AccountId = 983300;
        private const int CharacterId = 983301;
        private const int RewardItemId = 1004;
        private const int RequiredItemId = 3200;
        private const int InitialGold = 1_000;
        private const int InitialRequiredItemCount = 5;
        private const int GoldPrice = 100;
        private const int RequiredItemPrice = 2;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "secret-shop-transaction-" +
                Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(database);

                var rewardMetadata = ItemMetadataResolver.Resolve(
                    RewardItemId);
                var costMetadata = ItemMetadataResolver.Resolve(
                    RequiredItemId);
                Check(
                    "secret shop fixture resolves physical stackable items",
                    rewardMetadata != null
                    && rewardMetadata.IsStackable
                    && costMetadata != null
                    && costMetadata.IsStackable,
                    ref failures);

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    database);
                var costGranted = InventoryRewardGrantService
                    .TryCreateAndInsert(
                        inventory,
                        RequiredItemId,
                        ItemCreateReason.AdminGrant,
                        InitialRequiredItemCount,
                        out var costGrant);
                inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    InitialGold);
                var fixtureLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                Check(
                    "secret shop fixture persists cost item and gold",
                    costGranted
                    && costGrant != null
                    && costGrant.Success
                    && InventoryPersistenceService.SaveDirty(fixtureLease),
                    ref failures);
                if (!costGranted || costGrant == null || !costGrant.Success)
                    return 1;

                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
                var service = new SecretShopPurchaseService();

                var goldOffer = CreateOffer(
                    rawFlag: 0,
                    price: GoldPrice,
                    requiredItemId: 0);
                CreateRewardInsertFailureTrigger(databasePath);
                var goldFailed = !service.TryPurchase(
                    lease,
                    goldOffer,
                    RewardItemId,
                    1,
                    out var failedGoldResult);
                Check(
                    "gold secret shop purchase rejects reward persistence failure",
                    goldFailed && failedGoldResult == null,
                    ref failures);
                Check(
                    "gold purchase failure preserves offer and online assets",
                    GetOfferRemaining(goldOffer) == 2
                    && lease.Inventory.CountMainItem(0) == InitialGold
                    && lease.Inventory.CountMainItem(RewardItemId) == 0
                    && lease.Inventory.DirtyMainVirtualCountSlots.Count == 0
                    && !lease.Inventory.DirtyListTypes.Contains(
                        InventoryListType.Main),
                    ref failures);
                Check(
                    "gold purchase failure leaves database unchanged",
                    LoadPersistedCount(database, 0) == InitialGold
                    && LoadPersistedCount(database, RewardItemId) == 0,
                    ref failures);

                DropFailureTriggers(databasePath);
                var goldRetried = service.TryPurchase(
                    lease,
                    goldOffer,
                    RewardItemId,
                    1,
                    out var goldRetryResult);
                Check(
                    "gold secret shop purchase retries after recovery",
                    goldRetried
                    && goldRetryResult != null
                    && GetOfferRemaining(goldOffer) == 1
                    && lease.Inventory.CountMainItem(0)
                        == InitialGold - GoldPrice
                    && lease.Inventory.CountMainItem(RewardItemId) == 1,
                    ref failures);
                Check(
                    "gold secret shop retry persists assets",
                    LoadPersistedCount(database, 0)
                        == InitialGold - GoldPrice
                    && LoadPersistedCount(database, RewardItemId) == 1,
                    ref failures);

                var itemOffer = CreateOffer(
                    rawFlag: 1,
                    price: RequiredItemPrice,
                    requiredItemId: RequiredItemId);
                CreatePhysicalUpdateFailureTrigger(databasePath);
                var itemFailed = !service.TryPurchase(
                    lease,
                    itemOffer,
                    RewardItemId,
                    1,
                    out var failedItemResult);
                Check(
                    "item-currency secret shop purchase rejects persistence failure",
                    itemFailed && failedItemResult == null,
                    ref failures);
                Check(
                    "item-currency failure preserves offer and online assets",
                    GetOfferRemaining(itemOffer) == 2
                    && lease.Inventory.CountMainItem(RequiredItemId)
                        == InitialRequiredItemCount
                    && lease.Inventory.CountMainItem(RewardItemId) == 1
                    && !lease.Inventory.DirtyListTypes.Contains(
                        InventoryListType.Main),
                    ref failures);
                Check(
                    "item-currency failure leaves database unchanged",
                    LoadPersistedCount(database, RequiredItemId)
                        == InitialRequiredItemCount
                    && LoadPersistedCount(database, RewardItemId) == 1,
                    ref failures);

                DropFailureTriggers(databasePath);
                var itemRetried = service.TryPurchase(
                    lease,
                    itemOffer,
                    RewardItemId,
                    1,
                    out var itemRetryResult);
                Check(
                    "item-currency secret shop purchase retries after recovery",
                    itemRetried
                    && itemRetryResult != null
                    && GetOfferRemaining(itemOffer) == 1
                    && lease.Inventory.CountMainItem(RequiredItemId)
                        == InitialRequiredItemCount - RequiredItemPrice
                    && lease.Inventory.CountMainItem(RewardItemId) == 2,
                    ref failures);
                Check(
                    "item-currency secret shop retry persists assets",
                    LoadPersistedCount(database, RequiredItemId)
                        == InitialRequiredItemCount - RequiredItemPrice
                    && LoadPersistedCount(database, RewardItemId) == 2,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] secret shop transaction selftest threw: " + ex);
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
                    ? "SecretShopTransactionSelfTest OK"
                    : "SecretShopTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static SecretShopOffer CreateOffer(
            int rawFlag,
            int price,
            int requiredItemId)
        {
            return new SecretShopOffer(
                1002,
                new[]
                {
                    new SecretShopItemCandidate
                    {
                        ItemId = RewardItemId,
                        RawFlag = rawFlag,
                        Price = price,
                        RequiredItemId = requiredItemId,
                        Count = 2,
                        Weight = 1,
                    },
                });
        }

        private static int GetOfferRemaining(SecretShopOffer offer)
        {
            return offer.Items.Count == 1
                ? offer.Items[0].RemainingCount
                : -1;
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
VALUES(@aid, 'secret-shop-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level, town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes("SecretShopTransaction"));
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void CreateRewardInsertFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_secret_shop_reward_insert
BEFORE INSERT ON character_inventory_items
WHEN NEW.character_id = {CharacterId}
 AND NEW.list_type = {(int)InventoryListType.Main}
 AND NEW.slot_index >= {InventoryService.MainSlotStart}
BEGIN
    SELECT RAISE(ABORT, 'injected secret shop reward insert failure');
END;");
        }

        private static void CreatePhysicalUpdateFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_secret_shop_physical_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index >= {InventoryService.MainSlotStart}
BEGIN
    SELECT RAISE(ABORT, 'injected secret shop physical update failure');
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
DROP TRIGGER IF EXISTS fail_secret_shop_reward_insert;
DROP TRIGGER IF EXISTS fail_secret_shop_physical_update;");
            }
            catch
            {
            }
        }

        private static int LoadPersistedCount(
            IGameDatabase database,
            int itemId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    database);
                return inventory.CountMainItem(itemId);
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
