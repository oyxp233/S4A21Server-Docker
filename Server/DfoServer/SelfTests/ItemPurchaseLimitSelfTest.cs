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

                // ── TryRecordPurchase 三态 ────────────────────────────────────
                const int freshLimitNpcId = 599101;
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var outcome = ItemPurchaseLimitService.TryRecordPurchase(
                        purchaseInventory,
                        freshLimitNpcId,
                        accountItemId,
                        accountDefinition.LimitCount + 1,
                        connection,
                        transaction);
                    Check(
                        "全新行购买数超上限返回 LimitExceeded",
                        outcome == PurchaseLimitRecordOutcome.LimitExceeded,
                        ref failures);
                    transaction.Rollback();
                }

                Check(
                    "超上限购买不写入限购行",
                    ReadPurchaseCount(
                        database,
                        purchaseAccountId,
                        GetRowCharacterId(accountDefinition.LimitType, purchaseCharacterId),
                        freshLimitNpcId,
                        accountItemId,
                        accountDefinition.LimitType,
                        accountDefinition.ResetType) == 0,
                    ref failures);

                const int atLimitNpcId = 599102;
                InsertPurchaseLimitRow(
                    database,
                    purchaseAccountId,
                    GetRowCharacterId(accountDefinition.LimitType, purchaseCharacterId),
                    atLimitNpcId,
                    accountItemId,
                    accountDefinition.LimitCount,
                    accountDefinition.LimitType,
                    accountDefinition.ResetType);
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var outcome = ItemPurchaseLimitService.TryRecordPurchase(
                        purchaseInventory,
                        atLimitNpcId,
                        accountItemId,
                        1,
                        connection,
                        transaction);
                    Check(
                        "已达上限再购 1 个返回 LimitExceeded",
                        outcome == PurchaseLimitRecordOutcome.LimitExceeded,
                        ref failures);
                    transaction.Rollback();
                }

                Check(
                    "已达上限的限购行计数不变",
                    ReadPurchaseCount(
                        database,
                        purchaseAccountId,
                        GetRowCharacterId(accountDefinition.LimitType, purchaseCharacterId),
                        atLimitNpcId,
                        accountItemId,
                        accountDefinition.LimitType,
                        accountDefinition.ResetType) == accountDefinition.LimitCount,
                    ref failures);

                const int legalPurchaseNpcId = 599103;
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var outcome = ItemPurchaseLimitService.TryRecordPurchase(
                        purchaseInventory,
                        legalPurchaseNpcId,
                        accountItemId,
                        1,
                        connection,
                        transaction);
                    Check(
                        "未超上限购买返回 Ok",
                        outcome == PurchaseLimitRecordOutcome.Ok,
                        ref failures);
                    transaction.Commit();
                }

                Check(
                    "未超上限购买写入计数",
                    ReadPurchaseCount(
                        database,
                        purchaseAccountId,
                        GetRowCharacterId(accountDefinition.LimitType, purchaseCharacterId),
                        legalPurchaseNpcId,
                        accountItemId,
                        accountDefinition.LimitType,
                        accountDefinition.ResetType) == 1,
                    ref failures);

                if (TryFindUnlimitedShopItem(catalog, out var unlimitedItemId))
                {
                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        var outcome = ItemPurchaseLimitService.TryRecordPurchase(
                            purchaseInventory,
                            accountNpcId,
                            unlimitedItemId,
                            1,
                            connection,
                            transaction);
                        Check(
                            "无购买限制物品返回 Ok",
                            outcome == PurchaseLimitRecordOutcome.Ok,
                            ref failures);
                        transaction.Rollback();
                    }
                }
                else
                {
                    Check("无购买限制物品返回 Ok", false, ref failures);
                }

                // ── TryBuyNpcItem 带出失败原因 ───────────────────────────────
                using (var connection = purchaseInventory.Database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var goldBefore = purchaseInventory.CountMainItem(0);
                    var bought = InventoryShopRuntimeService.TryBuyNpcItem(
                        purchaseInventory,
                        accountItemId,
                        1,
                        accountNpcId,
                        connection,
                        transaction,
                        out var blockedResult,
                        out var blockedReason);
                    Check(
                        "超限购买返回失败并带出 PurchaseLimitExceeded",
                        !bought
                        && blockedResult == null
                        && blockedReason == ShopBuyFailureReason.PurchaseLimitExceeded,
                        ref failures);
                    Check(
                        "超限购买不扣金币且不发放物品",
                        purchaseInventory.CountMainItem(0) == goldBefore
                        && purchaseInventory.CountMainItem(accountItemId) == 0,
                        ref failures);
                    transaction.Rollback();
                }

                // ── 失败回包格式 ────────────────────────────────────────────
                Check(
                    "NPC 商店失败 ack 恒为 [0x00, errorCode] 两字节",
                    VerifyBuyItemErrorAck(),
                    ref failures);
                Check(
                    "限购超限映射到限制购买错误码",
                    BuyItemAckBuilder.ResolveErrorCode(
                        ShopBuyFailureReason.PurchaseLimitExceeded) == BuyItemAckBuilder.ErrorCodePurchaseLimited,
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

                // ── item_purchase_limits 内建跨天 rollover(挂机跨过 06:00) ────
                // 过期 day_id 恒用 1: 不等于任何合法 TodayId(yyyyMMdd)。
                const int staleDayId = 1;
                var today = DailyResetService.TodayId();

                Check(
                    "新库 item_purchase_limits 含 day_id 列",
                    HasTableColumn(database, "item_purchase_limits", "day_id"),
                    ref failures);
                Check(
                    "新库 schema 版本为当前版本",
                    ReadUserVersion(database) == Sqlite.SqliteMigrations.CurrentVersion,
                    ref failures);

                if (!TryFindLimitedItemByResetType(
                        catalog,
                        1,
                        out var rolloverItemId,
                        out var rolloverNpcId,
                        out var rolloverDefinition))
                {
                    Check("找到 reset_type=1 的限购物品", false, ref failures);
                }
                else
                {
                    const int rolloverAccountId = 54001;
                    const int rolloverCharacterId = 54002;
                    SeedAccount(database, rolloverAccountId, "item-limit-rollover-a");
                    SeedCharacter(
                        database,
                        rolloverCharacterId,
                        rolloverAccountId,
                        "item-limit-rollover-c");
                    var rolloverInventory = LoadInventory(
                        database,
                        rolloverCharacterId,
                        rolloverAccountId);
                    var rolloverRowCharacterId = GetRowCharacterId(
                        rolloverDefinition.LimitType,
                        rolloverCharacterId);

                    InsertPurchaseLimitRow(
                        database,
                        rolloverAccountId,
                        rolloverRowCharacterId,
                        rolloverNpcId,
                        rolloverItemId,
                        rolloverDefinition.LimitCount,
                        rolloverDefinition.LimitType,
                        1,
                        dayId: staleDayId);
                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        var outcome = ItemPurchaseLimitService.TryRecordPurchase(
                            rolloverInventory,
                            rolloverNpcId,
                            rolloverItemId,
                            1,
                            connection,
                            transaction);
                        Check(
                            "过期 day_id 的日限购行跨天购买返回 Ok",
                            outcome == PurchaseLimitRecordOutcome.Ok,
                            ref failures);
                        transaction.Commit();
                    }

                    Check(
                        "跨天购买后 buy_count 从本次购买数重计",
                        ReadPurchaseCount(
                            database,
                            rolloverAccountId,
                            rolloverRowCharacterId,
                            rolloverNpcId,
                            rolloverItemId,
                            rolloverDefinition.LimitType,
                            1) == 1,
                        ref failures);
                    Check(
                        "跨天购买后 day_id 更新为当日",
                        ReadPurchaseDayId(
                            database,
                            rolloverAccountId,
                            rolloverRowCharacterId,
                            rolloverNpcId,
                            rolloverItemId,
                            rolloverDefinition.LimitType,
                            1) == today,
                        ref failures);

                    const int sameDayAccountId = 55001;
                    const int sameDayCharacterId = 55002;
                    SeedAccount(database, sameDayAccountId, "item-limit-sameday-a");
                    SeedCharacter(
                        database,
                        sameDayCharacterId,
                        sameDayAccountId,
                        "item-limit-sameday-c");
                    var sameDayInventory = LoadInventory(
                        database,
                        sameDayCharacterId,
                        sameDayAccountId);

                    InsertPurchaseLimitRow(
                        database,
                        sameDayAccountId,
                        GetRowCharacterId(
                            rolloverDefinition.LimitType,
                            sameDayCharacterId),
                        rolloverNpcId,
                        rolloverItemId,
                        rolloverDefinition.LimitCount,
                        rolloverDefinition.LimitType,
                        1,
                        dayId: today);
                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        var outcome = ItemPurchaseLimitService.TryRecordPurchase(
                            sameDayInventory,
                            rolloverNpcId,
                            rolloverItemId,
                            1,
                            connection,
                            transaction);
                        Check(
                            "当日已达上限的日限购行再购返回 LimitExceeded",
                            outcome == PurchaseLimitRecordOutcome.LimitExceeded,
                            ref failures);
                        transaction.Rollback();
                    }

                    Check(
                        "当日已达上限的日限购行计数不变",
                        ReadPurchaseCount(
                            database,
                            sameDayAccountId,
                            GetRowCharacterId(
                                rolloverDefinition.LimitType,
                                sameDayCharacterId),
                            rolloverNpcId,
                            rolloverItemId,
                            rolloverDefinition.LimitType,
                            1) == rolloverDefinition.LimitCount,
                        ref failures);
                }

                if (!TryFindLimitedItemByResetType(
                        catalog,
                        1,
                        out var expiredShopItemId,
                        out var expiredShopNpcId,
                        out var expiredShopDefinition))
                {
                    Check("找到商店在售的 reset_type=1 限购物品", false, ref failures);
                }
                else
                {
                    const int expiredShopAccountId = 56001;
                    const int expiredShopCharacterId = 56002;
                    SeedAccount(database, expiredShopAccountId, "item-limit-expired-shop-a");
                    SeedCharacter(
                        database,
                        expiredShopCharacterId,
                        expiredShopAccountId,
                        "item-limit-expired-shop-c");
                    var expiredShopInventory = LoadInventory(
                        database,
                        expiredShopCharacterId,
                        expiredShopAccountId);
                    var expiredShopRowCharacterId = GetRowCharacterId(
                        expiredShopDefinition.LimitType,
                        expiredShopCharacterId);

                    InsertPurchaseLimitRow(
                        database,
                        expiredShopAccountId,
                        expiredShopRowCharacterId,
                        expiredShopNpcId,
                        expiredShopItemId,
                        expiredShopDefinition.LimitCount,
                        expiredShopDefinition.LimitType,
                        1,
                        dayId: staleDayId);
                    var expiredCounts = ItemPurchaseLimitService.LoadNpcPurchaseCounts(
                        expiredShopInventory,
                        expiredShopNpcId);
                    Check(
                        "过期 day_id 的限购行不出现在商店下发列表",
                        !expiredCounts.Any(entry => entry.ItemId == expiredShopItemId),
                        ref failures);

                    const int currentShopAccountId = 57001;
                    const int currentShopCharacterId = 57002;
                    SeedAccount(database, currentShopAccountId, "item-limit-current-shop-a");
                    SeedCharacter(
                        database,
                        currentShopCharacterId,
                        currentShopAccountId,
                        "item-limit-current-shop-c");
                    var currentShopInventory = LoadInventory(
                        database,
                        currentShopCharacterId,
                        currentShopAccountId);
                    var currentShopRowCharacterId = GetRowCharacterId(
                        expiredShopDefinition.LimitType,
                        currentShopCharacterId);

                    InsertPurchaseLimitRow(
                        database,
                        currentShopAccountId,
                        currentShopRowCharacterId,
                        expiredShopNpcId,
                        expiredShopItemId,
                        1,
                        expiredShopDefinition.LimitType,
                        1,
                        dayId: today);
                    var currentCounts = ItemPurchaseLimitService.LoadNpcPurchaseCounts(
                        currentShopInventory,
                        expiredShopNpcId);
                    Check(
                        "当日限购行仍出现在商店下发列表",
                        currentCounts.Any(
                            entry => entry.ItemId == expiredShopItemId
                                && entry.Value == 1),
                        ref failures);
                }

                if (!TryFindLimitedItemByResetType(
                        catalog,
                        0,
                        out var fixedItemId,
                        out var fixedNpcId,
                        out var fixedDefinition))
                {
                    Check("找到 reset_type=0 的限购物品", false, ref failures);
                }
                else
                {
                    const int fixedAccountId = 58001;
                    const int fixedCharacterId = 58002;
                    SeedAccount(database, fixedAccountId, "item-limit-fixed-a");
                    SeedCharacter(
                        database,
                        fixedCharacterId,
                        fixedAccountId,
                        "item-limit-fixed-c");
                    var fixedInventory = LoadInventory(
                        database,
                        fixedCharacterId,
                        fixedAccountId);
                    var fixedRowCharacterId = GetRowCharacterId(
                        fixedDefinition.LimitType,
                        fixedCharacterId);

                    InsertPurchaseLimitRow(
                        database,
                        fixedAccountId,
                        fixedRowCharacterId,
                        fixedNpcId,
                        fixedItemId,
                        fixedDefinition.LimitCount,
                        fixedDefinition.LimitType,
                        0,
                        dayId: staleDayId);
                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        var outcome = ItemPurchaseLimitService.TryRecordPurchase(
                            fixedInventory,
                            fixedNpcId,
                            fixedItemId,
                            1,
                            connection,
                            transaction);
                        Check(
                            "reset_type=0 的限购行不因 day_id 过期而重置",
                            outcome == PurchaseLimitRecordOutcome.LimitExceeded,
                            ref failures);
                        transaction.Rollback();
                    }

                    Check(
                        "reset_type=0 的限购行计数与 day_id 不变",
                        ReadPurchaseCount(
                            database,
                            fixedAccountId,
                            fixedRowCharacterId,
                            fixedNpcId,
                            fixedItemId,
                            fixedDefinition.LimitType,
                            0) == fixedDefinition.LimitCount
                        && ReadPurchaseDayId(
                            database,
                            fixedAccountId,
                            fixedRowCharacterId,
                            fixedNpcId,
                            fixedItemId,
                            fixedDefinition.LimitType,
                            0) == staleDayId,
                        ref failures);
                }
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

        private static bool VerifyBuyItemErrorAck()
        {
            var codes = new[]
            {
                BuyItemAckBuilder.ErrorCodeInventoryFull,
                BuyItemAckBuilder.ErrorCodeInsufficientGold,
                BuyItemAckBuilder.ErrorCodeCannotBuy,
                BuyItemAckBuilder.ErrorCodePurchaseLimited,
            };

            foreach (var code in codes)
            {
                var body = BuyItemAckBuilder.BuildError(code);
                if (body == null || body.Length != 2 || body[0] != 0x00 || body[1] != code)
                    return false;
            }

            return true;
        }

        private static bool TryFindUnlimitedShopItem(
            ItemShopCatalog catalog,
            out int itemId)
        {
            itemId = 0;
            if (catalog == null)
                return false;

            var seen = new HashSet<int>();
            foreach (var entry in catalog.Entries)
            {
                if (entry?.Shop == null || entry.NpcId <= 0)
                    continue;

                foreach (var candidate in entry.Shop.GetItemIds(true))
                {
                    if (candidate <= 0 || !seen.Add(candidate))
                        continue;

                    if (!ItemPurchaseLimitService.TryResolveDefinition(candidate, out _))
                    {
                        itemId = candidate;
                        return true;
                    }
                }
            }

            return false;
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
                var outcome = ItemPurchaseLimitService.TryRecordPurchase(
                    inventory,
                    npcId,
                    itemId,
                    purchaseCount,
                    connection,
                    transaction);
                if (outcome != PurchaseLimitRecordOutcome.Ok)
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
            int resetType,
            int? dayId = null)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO item_purchase_limits (
    account_id, character_id, npc_id, item_id, buy_count, limit_type, reset_type, day_id, updated_at
) VALUES (
    @aid, @cid, @npcId, @itemId, @buyCount, @limitType, @resetType, @dayId, CURRENT_TIMESTAMP
);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@npcId", npcId);
                command.Parameters.AddWithValue("@itemId", itemId);
                command.Parameters.AddWithValue("@buyCount", buyCount);
                command.Parameters.AddWithValue("@limitType", limitType);
                command.Parameters.AddWithValue("@resetType", resetType);
                command.Parameters.AddWithValue(
                    "@dayId",
                    dayId ?? DailyResetService.TodayId());
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        private static int ReadPurchaseDayId(
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
SELECT day_id
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

        private static bool HasTableColumn(
            IGameDatabase database,
            string tableName,
            string columnName)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA table_info({tableName});";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(1)
                            && string.Equals(
                                reader.GetString(1),
                                columnName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static int ReadUserVersion(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version;";
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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

            var grantProbe = new InventoryService(characterId: 51002, accountId: 51001);
            var seen = new HashSet<int>();
            foreach (var entry in catalog.Entries)
            {
                if (entry?.Shop == null || entry.NpcId <= 0)
                    continue;

                foreach (var itemId in entry.Shop.GetItemIds(true))
                {
                    if (itemId <= 0 || !seen.Add(itemId))
                        continue;

                    if (!ItemPurchaseLimitService.TryResolveDefinition(itemId, out var definition)
                        || !CanGrantToShopBuyer(grantProbe, itemId))
                    {
                        continue;
                    }

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

        private static bool CanGrantToShopBuyer(InventoryService inventory, int itemId)
        {
            var requests = new[]
            {
                InventoryRewardGrantRequest.Create(itemId, 1, ItemCreateReason.NpcShopPurchase),
            };
            return InventoryRewardGrantService.TryPlanBatch(inventory, requests, out var plan)
                && plan != null
                && plan.Success;
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

        private static bool TryFindLimitedItemByResetType(
            ItemShopCatalog catalog,
            int resetType,
            out int itemId,
            out int npcId,
            out ItemPurchaseLimitDefinition definition)
        {
            itemId = 0;
            npcId = 0;
            definition = null;
            if (catalog == null)
                return false;

            var seen = new HashSet<int>();
            foreach (var entry in catalog.Entries)
            {
                if (entry?.Shop == null || entry.NpcId <= 0)
                    continue;

                foreach (var candidate in entry.Shop.GetItemIds(true))
                {
                    if (candidate <= 0 || !seen.Add(candidate))
                        continue;

                    if (!ItemPurchaseLimitService.TryResolveDefinition(
                            candidate,
                            out var resolved)
                        || resolved.ResetType != resetType)
                    {
                        continue;
                    }

                    itemId = candidate;
                    npcId = entry.NpcId;
                    definition = resolved;
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
