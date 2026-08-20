using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class ItemPurchaseLimitSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== ITEM_PURCHASE_LIMIT selftest ===");
            var failures = 0;
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_item_purchase_limit_{Guid.NewGuid():N}.db");

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var dailyResetService = new DailyResetService(database);
                using var archive = PvfArchive.Open(GameWorldConfig.PvfArchivePath);
                var catalog = ItemShopCatalog.Load(archive);

                    if (!TryFindLimitedItems(
                            catalog,
                            out var accountItemId,
                            out var accountDefinition,
                            out var charItemId,
                            out var charDefinition))
                {
                    throw new InvalidOperationException("无法找到可用于限购自检的真实物品");
                }

                if (!TryFindNpcForItem(catalog, accountItemId, out var accountNpcId))
                    throw new InvalidOperationException($"无法找到限购物品 0x{accountItemId:X8} 对应的 npc");
                if (!TryFindNpcForItem(catalog, charItemId, out var charNpcId))
                    throw new InvalidOperationException($"无法找到限购物品 0x{charItemId:X8} 对应的 npc");

                const int purchaseAccountId = 51001;
                const int purchaseCharacterId = 51002;
                const int countsAccountId = 52001;
                const int countsCharacterId = 52002;
                const int resetAccountId = 53001;
                const int resetCharacterId = 53002;

                SeedAccount(database, purchaseAccountId, "item-limit-purchase-a");
                SeedCharacter(database, purchaseCharacterId, purchaseAccountId, "item-limit-purchase-c");
                SeedAccount(database, countsAccountId, "item-limit-counts-a");
                SeedCharacter(database, countsCharacterId, countsAccountId, "item-limit-counts-c");
                SeedAccount(database, resetAccountId, "item-limit-reset-a");
                SeedCharacter(database, resetCharacterId, resetAccountId, "item-limit-reset-c");

                var purchaseInventory = LoadInventory(
                    database,
                    purchaseCharacterId,
                    purchaseAccountId);
                var countsInventory = LoadInventory(
                    database,
                    countsCharacterId,
                    countsAccountId);

                Check(
                    "账号限购定义识别",
                    accountDefinition.LimitType == 0
                    && accountDefinition.LimitCount > 0,
                    ref failures);
                Check(
                    "角色限购定义识别",
                    charDefinition.LimitType == 1
                    && charDefinition.LimitCount > 0,
                    ref failures);

                Check(
                    "账号限购物品首次购买写入成功",
                    TryPurchaseOnce(
                        purchaseInventory,
                        accountNpcId,
                        accountItemId,
                        accountDefinition.LimitCount,
                        accountDefinition,
                        out var accountPurchaseRecord),
                    ref failures);
                Check(
                    "账号限购物品已购次数写入正确",
                    accountPurchaseRecord == accountDefinition.LimitCount,
                    ref failures);
                Check(
                    "账号限购物品超额购买被拦截",
                    !TryPurchaseOnce(
                        purchaseInventory,
                        accountNpcId,
                        accountItemId,
                        1,
                        accountDefinition,
                        out _),
                    ref failures);

                Check(
                    "角色限购物品首次购买写入成功",
                    TryPurchaseOnce(
                        purchaseInventory,
                        charNpcId,
                        charItemId,
                        charDefinition.LimitCount,
                        charDefinition,
                        out var charPurchaseRecord),
                    ref failures);
                Check(
                    "角色限购物品已购次数写入正确",
                    charPurchaseRecord == charDefinition.LimitCount,
                    ref failures);
                Check(
                    "角色限购物品超额购买被拦截",
                    !TryPurchaseOnce(
                        purchaseInventory,
                        charNpcId,
                        charItemId,
                        1,
                        charDefinition,
                        out _),
                    ref failures);

                if (TryFindNpcWithTwoLimitedItems(catalog, out var countsNpcId, out var positiveItemId, out var zeroItemId, out var positiveDefinition, out var zeroDefinition))
                {
                    var positiveCount = Math.Min(3, Math.Max(1, positiveDefinition.LimitCount));
                    var positiveCharacterId = GetRowCharacterId(
                        positiveDefinition.LimitType,
                        countsCharacterId);
                    var zeroCharacterId = GetRowCharacterId(
                        zeroDefinition.LimitType,
                        countsCharacterId);
                    InsertPurchaseLimitRow(
                        database,
                        countsAccountId,
                        positiveCharacterId,
                        countsNpcId,
                        positiveItemId,
                        positiveCount,
                        positiveDefinition.LimitType,
                        positiveDefinition.ResetType);
                    InsertPurchaseLimitRow(
                        database,
                        countsAccountId,
                        zeroCharacterId,
                        countsNpcId,
                        zeroItemId,
                        0,
                        zeroDefinition.LimitType,
                        zeroDefinition.ResetType);

                    var counts = ItemPurchaseLimitService.LoadNpcPurchaseCounts(
                        countsInventory,
                        countsNpcId);
                    Check(
                        "商店限购列表只下发已购买次数>0的物品",
                        counts.Count == 1
                        && counts[0].ItemId == positiveItemId
                        && counts[0].Value == positiveCount,
                        ref failures);
                }
                else
                {
                    InsertPurchaseLimitRow(
                        database,
                        countsAccountId,
                        0,
                        accountNpcId,
                        accountItemId,
                        2,
                        accountDefinition.LimitType,
                        accountDefinition.ResetType);

                    var counts = ItemPurchaseLimitService.LoadNpcPurchaseCounts(
                        countsInventory,
                        accountNpcId);
                    Check(
                        "商店限购列表返回正数次数",
                        counts.Count == 1
                        && counts[0].ItemId == accountItemId
                        && counts[0].Value == 2,
                        ref failures);
                }

                Check(
                    "shop purchase count packet writes second empty block",
                    VerifyShopPurchaseCountPacket(accountItemId, 2),
                    ref failures);
                Check(
                    "buy item ack uses requested purchase count",
                    VerifyBuyItemAckRequestedCount(accountItemId, 5),
                    ref failures);

                var resetItemIdA = charItemId;
                var resetItemIdB = accountItemId;
                var resetRowCharacterIdA = GetRowCharacterId(
                    charDefinition.LimitType,
                    resetCharacterId);
                var resetRowCharacterIdB = GetRowCharacterId(
                    accountDefinition.LimitType,
                    resetCharacterId);
                InsertPurchaseLimitRow(
                    database,
                    resetAccountId,
                    resetRowCharacterIdA,
                    60001,
                    resetItemIdA,
                    7,
                    charDefinition.LimitType,
                    1);
                InsertPurchaseLimitRow(
                    database,
                    resetAccountId,
                    resetRowCharacterIdB,
                    60002,
                    resetItemIdB,
                    5,
                    accountDefinition.LimitType,
                    0);

                Check(
                    "记录日切前的退出时间成功",
                    dailyResetService.TryRecordAccountLogout(
                        resetAccountId,
                        new DateTime(2026, 8, 20, 20, 30, 0, DateTimeKind.Utc)),
                    ref failures);

                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var applied = false;
                    var ok = dailyResetService.TryRunAccountFirstLoginReset(
                        connection,
                        transaction,
                        resetAccountId,
                        new DateTime(2026, 8, 20, 22, 30, 0, DateTimeKind.Utc),
                        (conn, tx) => ItemPurchaseLimitService.ResetPurchasesForAccount(
                            conn,
                            tx,
                            resetAccountId),
                        out applied);
                    Check("账号首次登录时触发限购重置", ok && applied, ref failures);
                    if (ok)
                        transaction.Commit();
                }

                Check(
                    "仅重置 reset_type=1 的记录",
                    ReadPurchaseCount(database, resetAccountId, resetRowCharacterIdA, 60001, resetItemIdA, charDefinition.LimitType, 1) == 0
                    && ReadPurchaseCount(database, resetAccountId, resetRowCharacterIdB, 60002, resetItemIdB, accountDefinition.LimitType, 0) == 5,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ITEM_PURCHASE_LIMIT] EXCEPTION: {ex}");
                failures++;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempDbPath))
                        File.Delete(tempDbPath);
                }
                catch
                {
                }
            }

            Console.WriteLine(
                failures == 0
                    ? "ITEM_PURCHASE_LIMIT selftest passed."
                    : $"ITEM_PURCHASE_LIMIT selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool VerifyShopPurchaseCountPacket(int itemId, int buyCount)
        {
            var body = ShopPurchaseCountPacketBuilder.Build(
                new List<ItemValueEntrySnapshot>
                {
                    new ItemValueEntrySnapshot
                    {
                        ItemId = itemId,
                        Value = buyCount,
                    },
                });

            return body.Length == 17
                && body[0] == 0x01
                && BitConverter.ToInt32(body, 1) == 1
                && BitConverter.ToInt32(body, 5) == itemId
                && BitConverter.ToInt32(body, 9) == buyCount
                && BitConverter.ToInt32(body, 13) == 0;
        }

        private static bool VerifyBuyItemAckRequestedCount(int itemId, int buyCount)
        {
            var body = BuyItemAckBuilder.Build(
                new InventoryMutationResult
                {
                    SlotIndex = 3,
                    ItemTemplateId = itemId,
                    InstanceValue = 8,
                    UpdatedGold = 0,
                    UpdatedSp = 0,
                    UpdatedCoin = 0,
                    RequestedCount = (short)buyCount,
                    CoreSnapshot = new ItemCore
                    {
                        ItemKind = ItemCore.KindConsumable,
                        ItemId = itemId,
                        Value = 8,
                    },
                },
                new List<PurchaseCountUpdate>
                {
                    new PurchaseCountUpdate
                    {
                        ItemTemplateId = itemId,
                        RequestedCount = buyCount,
                    },
                });

            return body.Length >= 32
                && BitConverter.ToInt32(body, 23) == buyCount
                && body.Length >= 9
                && body[body.Length - 9] == 1
                && BitConverter.ToInt32(body, body.Length - 8) == itemId
                && BitConverter.ToInt32(body, body.Length - 4) == buyCount;
        }

        private static bool TryPurchaseOnce(
            InventoryService inventory,
            int npcId,
            int itemId,
            int purchaseCount,
            ItemPurchaseLimitDefinition definition,
            out int recordedCount)
        {
            recordedCount = 0;
            if (inventory == null
                || definition == null
                || purchaseCount <= 0)
            {
                return false;
            }

            using (var connection = inventory.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = ItemPurchaseLimitService.TryRecordPurchase(
                    inventory,
                    npcId,
                    itemId,
                    purchaseCount,
                    connection,
                    transaction);
                if (!ok)
                {
                    transaction.Rollback();
                    return false;
                }

                transaction.Commit();
            }

            recordedCount = ReadPurchaseCount(
                inventory.Database,
                inventory.AccountId,
                definition.LimitType == 0 ? 0 : inventory.CharacterId,
                npcId,
                itemId,
                definition.LimitType,
                definition.ResetType);
            return recordedCount > 0;
        }

        private static InventoryService LoadInventory(
            GameDatabase database,
            int characterId,
            int accountId)
        {
            using (var connection = database.OpenConnection())
            {
                return InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
            }
        }

        private static void SeedAccount(
            GameDatabase database,
            int accountId,
            string mid)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@mid", mid);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        private static void SeedCharacter(
            GameDatabase database,
            int characterId,
            int accountId,
            string name)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@name", name);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        private static void InsertPurchaseLimitRow(
            GameDatabase database,
            int accountId,
            int characterId,
            int npcId,
            int itemId,
            int buyCount,
            int limitType,
            int resetType)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO item_purchase_limits (
    account_id, character_id, npc_id, item_id, buy_count, limit_type, reset_type, updated_at
) VALUES (
    @aid, @cid, @npcId, @itemId, @buyCount, @limitType, @resetType, CURRENT_TIMESTAMP
);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@npcId", npcId);
                command.Parameters.AddWithValue("@itemId", itemId);
                command.Parameters.AddWithValue("@buyCount", buyCount);
                command.Parameters.AddWithValue("@limitType", limitType);
                command.Parameters.AddWithValue("@resetType", resetType);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        private static int ReadPurchaseCount(
            IGameDatabase database,
            int accountId,
            int characterId,
            int npcId,
            int itemId,
            int limitType,
            int resetType)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT buy_count
FROM item_purchase_limits
WHERE account_id = @aid
  AND character_id = @cid
  AND npc_id = @npcId
  AND item_id = @itemId
  AND limit_type = @limitType
  AND reset_type = @resetType;";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@npcId", npcId);
                command.Parameters.AddWithValue("@itemId", itemId);
                command.Parameters.AddWithValue("@limitType", limitType);
                command.Parameters.AddWithValue("@resetType", resetType);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? 0
                    : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        private static bool TryFindLimitedItems(
            ItemShopCatalog catalog,
            out int accountItemId,
            out ItemPurchaseLimitDefinition accountDefinition,
            out int charItemId,
            out ItemPurchaseLimitDefinition charDefinition)
        {
            accountItemId = 0;
            charItemId = 0;
            accountDefinition = null;
            charDefinition = null;

            if (catalog == null)
                return false;

            var seen = new HashSet<int>();
            foreach (var entry in catalog.Entries)
            {
                if (entry?.Shop == null || entry.NpcId <= 0)
                    continue;

                foreach (var itemId in entry.Shop.GetItemIds(true))
                {
                    if (itemId <= 0 || !seen.Add(itemId))
                        continue;

                    if (!ItemPurchaseLimitService.TryResolveDefinition(itemId, out var definition))
                        continue;

                    if (definition.LimitType == 0 && accountItemId <= 0)
                    {
                        accountItemId = itemId;
                        accountDefinition = definition;
                    }
                    else if (definition.LimitType == 1 && charItemId <= 0)
                    {
                        charItemId = itemId;
                        charDefinition = definition;
                    }

                    if (accountItemId > 0 && charItemId > 0)
                        return true;
                }
            }

            return accountItemId > 0 && charItemId > 0;
        }

        private static bool TryFindNpcForItem(
            ItemShopCatalog catalog,
            int itemId,
            out int npcId)
        {
            npcId = 0;
            if (catalog == null || itemId <= 0)
                return false;

            foreach (var entry in catalog.Entries)
            {
                if (entry?.Shop == null || entry.NpcId <= 0)
                    continue;

                if (entry.Shop.GetItemIds(true).Contains(itemId))
                {
                    npcId = entry.NpcId;
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindNpcWithTwoLimitedItems(
            ItemShopCatalog catalog,
            out int npcId,
            out int positiveItemId,
            out int zeroItemId,
            out ItemPurchaseLimitDefinition positiveDefinition,
            out ItemPurchaseLimitDefinition zeroDefinition)
        {
            npcId = 0;
            positiveItemId = 0;
            zeroItemId = 0;
            positiveDefinition = null;
            zeroDefinition = null;
            if (catalog == null)
                return false;

            foreach (var entry in catalog.Entries)
            {
                if (entry?.Shop == null || entry.NpcId <= 0)
                    continue;

                var limitedItems = new List<(int ItemId, ItemPurchaseLimitDefinition Definition)>();
                foreach (var itemId in entry.Shop.GetItemIds(true))
                {
                    if (ItemPurchaseLimitService.TryResolveDefinition(itemId, out var definition))
                        limitedItems.Add((itemId, definition));
                }

                if (limitedItems.Count < 2)
                    continue;

                npcId = entry.NpcId;
                positiveItemId = limitedItems[0].ItemId;
                positiveDefinition = limitedItems[0].Definition;
                zeroItemId = limitedItems[1].ItemId;
                zeroDefinition = limitedItems[1].Definition;
                return true;
            }

            return false;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static int GetRowCharacterId(int limitType, int characterId)
        {
            return limitType == 0 ? 0 : characterId;
        }
    }
}
