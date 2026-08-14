using System;
using System.IO;
using System.Linq;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.Shop;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.CeraShop;
using DfoServer.Network.Parsers.CeraShop;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // 验证 cerashop.etc 的 [regular package] 段被正确解析: 该段商品(如强化成功/增幅成功幸运礼盒)
    // 曾因 CeraShopProductCatalog 漏解析该段而 TryResolve 恒 false, 购买失败并被客户端显示为
    // "物品栏空间不足"。本自测断言这些商品可解析且点券价读自正确的列(col4)。
    // 依赖: 运行目录 Data/Pvf/Script.pvf (与其它需 PVF 的自测一致)。
    public static class CeraShopSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== CERASHOP selftest ===");
            int pass = 0;
            int fail = 0;

            void Check(string name, bool ok)
            {
                if (ok)
                {
                    pass++;
                    Console.WriteLine($"  [PASS] {name}");
                }
                else
                {
                    fail++;
                    Console.WriteLine($"  [FAIL] {name}");
                }
            }

            void CheckProduct(string label, int productId, int expectedItemId, int expectedCoinPrice)
            {
                if (!CeraShopProductCatalog.TryResolve(productId, out var entry) || entry == null)
                {
                    Check($"{label} (commodityNo {productId}) resolves", false);
                    return;
                }

                Check($"{label} (commodityNo {productId}) resolves", true);
                Check($"{label} itemTemplateId == {expectedItemId} (got {entry.ItemTemplateId})", entry.ItemTemplateId == expectedItemId);
                Check($"{label} coinPrice == {expectedCoinPrice} (got {entry.CoinPrice})", entry.CoinPrice == expectedCoinPrice);
            }

            // 回归: 已解析的 [regular package] 段样本商品(Lv80~84 专用礼包)也应正确读到 col4 价格。
            CheckProduct("Lv80~84专用礼包", 102290, 2683268, 2860);

            // [community package] 段(stride=11, 价格 col4) —— 修复前未解析, 婚庆/社区礼包购买必失败。
            CheckProduct("社区礼包(结婚戒指-男)", 102317, 2683326, 18888);

            Check("name tag state overwrites same character and keeps absolute expire time", CheckNameTagState());
            Check("coupon purchase packet parses coupon item and slot", CheckCouponPurchasePacket());
            Check("package purchase parses trailing coupon after component list", CheckPackageCouponPurchasePacket());
            Check("PVF coupon type is accepted", InventoryCeraShopRuntimeService.IsPurchaseCoupon(10007350));
            Check("ordinary item is rejected as coupon", !InventoryCeraShopRuntimeService.IsPurchaseCoupon(10000006));
            Check("buy-only-cera item reports insufficient cera instead of inventory full", CheckBuyOnlyCeraErrorAck());
            Check("happy-token gift box grants account currency atomically without an inventory item", CheckHappyTokenCeraGiftBox());
            Check("Devil Contract package activates all services without an inventory item", CheckDevilContractPackage());
            Check("contract packages parse and route all services without inventory slots", CheckContractRewardRouting());
            Check("overflow reward split keeps fitting count in inventory and mails remainder", CheckOverflowRewardSplit());
            Check("ordinary cera shop purchase rolls back currency and inventory together", CheckOrdinaryPurchaseAtomicRollback());
            Check("premium payment and activation roll back together", CheckPremiumAtomicRollback());
            Check("multi-item runtime purchase keeps committed items when a later item rolls back", CheckMultiItemPartialCommit());

            Console.WriteLine($"=== result: {pass} PASS, {fail} FAIL ===");
            return fail == 0 ? 0 : 1;
        }

        private static bool CheckCouponPurchasePacket()
        {
            var body = new byte[]
            {
                0x00, 0x00, 0x01, 0x01, 0x00, 0xFF, 0xFF,
                0x3E, 0x9B, 0x01, 0x00, 0x00, 0x00,
                0xA5, 0xB4, 0x98, 0x00, 0x6D, 0x00,
            };

            return CeraShopPurchaseRequest.TryParse(body, out var request)
                && request.ProductId == 105278
                && request.CouponSelected
                && request.CouponItemId == 10007717
                && request.CouponSlot == 109;
        }

        private static bool CheckPackageCouponPurchasePacket()
        {
            var body = new byte[]
            {
                0x00, 0x00, 0x01, 0x01, 0x00, 0xFF, 0xFF, 0x00,
                0x9B, 0x01, 0x00, 0x09, 0xFD, 0x89, 0x0D, 0x06,
                0x00, 0xAA, 0xB1, 0x0D, 0x06, 0x01, 0xC2, 0xD7,
                0x0D, 0x06, 0x01, 0xBA, 0x14, 0x0D, 0x06, 0x00,
                0x5F, 0xC7, 0x0C, 0x06, 0x07, 0x13, 0xEF, 0x0C,
                0x06, 0x01, 0x4A, 0x63, 0x0D, 0x06, 0x01, 0x9B,
                0x3B, 0x0D, 0x06, 0x01, 0x82, 0xFD, 0x0D, 0x06,
                0x00, 0x00, 0x36, 0xB3, 0x98, 0x00, 0x44, 0x00,
            };

            return CeraShopPurchaseRequest.TryParse(body, out var request)
                && request.ProductId == 105216
                && request.CouponSelected
                && request.CouponItemId == 10007350
                && request.CouponSlot == 68;
        }

        private static bool CheckBuyOnlyCeraErrorAck()
        {
            if (!CeraShopProductCatalog.TryFindBuyOnlyCeraProduct(out var product)
                || product == null
                || !CeraShopProductCatalog.TryResolve(product.ProductId, out var resolved)
                || resolved == null
                || resolved.ItemTemplateId != product.ItemTemplateId
                || !CeraShopProductCatalog.IsBuyOnlyCera(product.ItemTemplateId))
                return false;

            var insufficientCera = CeraShopPurchaseAckBuilder.BuildError(
                CeraShopPurchaseAckBuilder.ErrorCodeInsufficientCera);
            var inventoryFull = CeraShopPurchaseAckBuilder.BuildError();
            return insufficientCera.Length == inventoryFull.Length
                && insufficientCera[0] == 0
                && insufficientCera[1] == CeraShopPurchaseAckBuilder.ErrorCodeInsufficientCera
                && inventoryFull[1] == CeraShopPurchaseAckBuilder.ErrorCodeInventoryFull;
        }

        private static bool CheckNameTagState()
        {
            const int accountId = 903001;
            const int characterId = 903002;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "cerashop-name-tag-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT OR IGNORE INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, 'cerashop-name-tag-selftest', '');
INSERT OR IGNORE INTO characters(character_id, account_id, name)
VALUES(@characterId, @accountId, 'cerashop-name-tag');";
                        command.Parameters.AddWithValue("@accountId", accountId);
                        command.Parameters.AddWithValue("@characterId", characterId);
                        command.ExecuteNonQuery();
                    }

                    using (var transaction = connection.BeginTransaction())
                    {
                        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        NameTagStateRepository.Upsert(
                            connection,
                            transaction,
                            characterId,
                            1111111,
                            now + 3600);
                        NameTagStateRepository.Upsert(
                            connection,
                            transaction,
                            characterId,
                            2222222,
                            now + 7200);
                        transaction.Commit();
                    }

                    var state = NameTagStateRepository.Load(connection, characterId);
                    var nowAfterLoad = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return state.ItemId == 2222222
                        && state.ExpireTime > nowAfterLoad
                        && state.ExpireTime <= nowAfterLoad + 7200;
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool CheckHappyTokenCeraGiftBox()
        {
            const int accountId = 903011;
            const int characterId = 903012;
            const short sourceSlot = 40;
            const int giftBoxItemId = 0x0098AAFE;
            const int expectedGrant = 1800;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "cerashop-happy-token-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                var voucher = StackableItemProvider.Load(SpecialRewardRouter.HappyTokenCeraVoucherItemId);
                if (voucher == null
                    || voucher.Name?.Trim('`', ' ', '\t', '\r', '\n') != "欢乐代币券"
                    || voucher.StackableType?.IndexOf("[material]", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
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
VALUES(@accountId, 'cerashop-happy-token-selftest', '');
INSERT INTO characters(character_id, account_id, name)
VALUES(@characterId, @accountId, 'cerashop-happy-token');";
                            command.Parameters.AddWithValue("@accountId", accountId);
                            command.Parameters.AddWithValue("@characterId", characterId);
                            command.ExecuteNonQuery();
                        }

                        var source = ItemCore.Create(ItemCore.KindConsumable, giftBoxItemId);
                        source.Count = 1;
                        InventoryItemRepository.UpsertCharacterSlot(
                            connection,
                            transaction,
                            characterId,
                            InventoryListType.Main,
                            sourceSlot,
                            source);
                        transaction.Commit();
                    }

                    var inventory = InventoryService.LoadFromDb(connection, characterId, accountId);
                    if (!InventorySpecialConsumableService.TryOpenPackage0207(
                            inventory,
                            sourceSlot,
                            Array.Empty<int>(),
                            RejectingInventoryOverflowRewardSink.Instance,
                            out var result)
                        || result?.Rewards.Count != 1
                        || result.Rewards[0].SpecialOutcome?.Kind != SpecialRewardKind.HappyTokenCera
                        || result.Rewards[0].ItemTemplateId != SpecialRewardRouter.HappyTokenCeraVoucherItemId
                        || result.Rewards[0].GrantedCount != expectedGrant
                        || result.Rewards[0].SlotIndex != -1
                        || inventory.PendingHappyTokenCeraGrant != expectedGrant
                        || inventory.CountMainItem(SpecialRewardRouter.HappyTokenCeraVoucherItemId) != 0
                        || inventory.GetItem(InventoryListType.Main, sourceSlot) != null)
                        return false;

                    var lease = new InventoryLease(Guid.NewGuid(), characterId, inventory, 1);
                    using (var transaction = connection.BeginTransaction())
                    {
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, lease)
                            || CurrencyService.LoadWallet(connection, transaction, characterId).HappyTokenCera != expectedGrant
                            || CountSourceRows(connection, transaction, characterId, sourceSlot) != 0)
                            return false;
                    }

                    if (CurrencyService.LoadWallet(connection, null, characterId).HappyTokenCera != 0
                        || InventoryItemRepository.LoadCharacterSlot(
                            connection,
                            characterId,
                            InventoryListType.Main,
                            sourceSlot) == null)
                        return false;

                    using (var transaction = connection.BeginTransaction())
                    {
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, lease))
                            return false;
                        transaction.Commit();
                    }

                    return CurrencyService.LoadWallet(connection, null, characterId).HappyTokenCera == expectedGrant
                        && InventoryItemRepository.LoadCharacterSlot(
                            connection,
                            characterId,
                            InventoryListType.Main,
                            sourceSlot) == null;
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool CheckContractRewardRouting()
        {
            var catalog = DevilContractCatalog.Parse(PvfArchiveAccessor.ReadText("etc/cerashop.etc"));
            if (!catalog.TryFindAllServicePackagePurchase(out _, out var purchase)
                || !catalog.TryResolveServiceGrants(purchase, out var grants)
                || grants.Count != DevilContractCatalog.SlotCount)
                return false;

            var serviceItemTemplateIds = catalog.GetServiceItemTemplateIdsBySlot();
            if (serviceItemTemplateIds.Count != DevilContractCatalog.SlotCount)
                return false;

            for (var slotIndex = 0; slotIndex < serviceItemTemplateIds.Count; slotIndex++)
            {
                var itemTemplateId = serviceItemTemplateIds[slotIndex];
                if (!PremiumService.TryResolveContractItem(itemTemplateId, out var premiumType, out var durationDays)
                    || premiumType != DevilContractCatalog.SlotToPremiumType(slotIndex)
                    || durationDays <= 0)
                    return false;
            }

            var inventory = new InventoryService(903031, 903032);
            if (!InventoryRewardGrantService.TryPlanBatch(
                    inventory,
                    serviceItemTemplateIds
                        .Select(itemId => InventoryRewardGrantRequest.Create(
                            itemId,
                            1,
                            ItemCreateReason.MallPurchase))
                        .ToArray(),
                    out var plan)
                || plan == null
                || !plan.Success
                || plan.Entries.Count != DevilContractCatalog.SlotCount)
                return false;

            return plan.Entries.All(entry => entry.Kind == InventoryRewardGrantKind.Premium);
        }

        private static bool CheckDevilContractPackage()
        {
            const int accountId = 903021;
            const int characterId = 903022;
            const int initialCera = 100;
            const long now = 1720000000;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "cerashop-devil-contract-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                var catalog = DevilContractCatalog.Parse(PvfArchiveAccessor.ReadText("etc/cerashop.etc"));
                if (!catalog.TryFindAllServicePackagePurchase(out var commodityNo, out var purchase)
                    || purchase.CeraPrice <= 0
                    || purchase.DurationDays <= 0)
                    return false;
                if (!CeraShopProductCatalog.TryResolve(commodityNo, out var shopProduct)
                    || shopProduct == null
                    || shopProduct.ItemTemplateId != purchase.ItemTemplateId
                    || shopProduct.CoinPrice != purchase.CeraPrice)
                    return false;
                if (!catalog.TryResolveServiceGrants(purchase, out var grants)
                    || grants.Count != DevilContractCatalog.SlotCount)
                    return false;
                var expectedCeraPrice = purchase.CeraPrice;
                var expectedDurationDays = purchase.DurationDays;
                var initialTokenCera = expectedCeraPrice + 1000;
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash, cera, token_cera)
VALUES(@accountId, 'cerashop-devil-contract-selftest', '', @cera, @tokenCera);
INSERT INTO characters(character_id, account_id, name)
VALUES(@characterId, @accountId, 'cerashop-devil-contract');";
                            command.Parameters.AddWithValue("@accountId", accountId);
                            command.Parameters.AddWithValue("@characterId", characterId);
                            command.Parameters.AddWithValue("@cera", initialCera);
                            command.Parameters.AddWithValue("@tokenCera", initialTokenCera);
                            command.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }

                    DevilContractPurchaseApplication application = null;
                    if (!InventoryCeraShopRuntimeService.TrySpendCeraPaymentAndApplyDbAction(
                            connectionString,
                            characterId,
                            purchase.ItemTemplateId,
                            purchase.CeraPrice,
                            (paymentConnection, paymentTransaction) =>
                                PremiumService.TryActivateDevilContractServices(
                                    paymentConnection,
                                    paymentTransaction,
                                    accountId,
                                    grants,
                                    now,
                                    out application),
                            out var payment)
                        || payment.NewCera != initialCera
                        || payment.NewTokenCera != initialTokenCera - expectedCeraPrice
                        || application.Activations.Count != DevilContractCatalog.SlotCount)
                        return false;

                    for (var slotIndex = 0; slotIndex < DevilContractCatalog.SlotCount; slotIndex++)
                    {
                        var activation = application.Activations[slotIndex];
                        if (activation.PremiumType != DevilContractCatalog.SlotToPremiumType(slotIndex)
                            || activation.RemainingSeconds != expectedDurationDays * 86400L)
                            return false;
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT premium_type, end_time
FROM account_premiums
WHERE account_id = @accountId
ORDER BY premium_type;";
                        command.Parameters.AddWithValue("@accountId", accountId);
                        using (var reader = command.ExecuteReader())
                        {
                            for (var slotIndex = 0; slotIndex < DevilContractCatalog.SlotCount; slotIndex++)
                            {
                                if (!reader.Read()
                                    || reader.GetInt32(0) != DevilContractCatalog.SlotToPremiumType(slotIndex)
                                    || reader.GetInt64(1) != now + expectedDurationDays * 86400L)
                                    return false;
                            }

                            if (reader.Read())
                                return false;
                        }
                    }

                    var wallet = CurrencyService.LoadWallet(connection, null, characterId);
                    return wallet.Cera == initialCera
                        && wallet.TokenCera == initialTokenCera - expectedCeraPrice;
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool CheckOverflowRewardSplit()
        {
            if (!TryFindFiniteStackableItem(out var itemId, out var itemKind, out var stackLimit))
                return false;

            var inventory = new InventoryService(903041, 903042);
            var existing = ItemCore.Create(itemKind, itemId);
            existing.Count = stackLimit - 1;
            inventory.AttachItem(InventoryListType.Main, InventoryService.MainSlotStart, existing);

            var planningInventory = InventorySpecialConsumableService.CreatePlanningInventory(inventory);
            var requests = new[]
            {
                InventoryRewardGrantRequest.Create(
                    itemId,
                    2,
                    ItemCreateReason.MallPurchase),
            };

            return InventorySpecialConsumableService.TryPlanDirectRewards(
                    planningInventory,
                    requests,
                    out var directPlan,
                    out var overflowRewards)
                && directPlan != null
                && directPlan.Success
                && directPlan.Entries.Count == 1
                && directPlan.Entries[0].ItemTemplateId == itemId
                && directPlan.Entries[0].GrantedCount == 1
                && overflowRewards.Count == 1
                && overflowRewards[0].ItemTemplateId == itemId
                && overflowRewards[0].Count == 1;
        }

        private static bool CheckOrdinaryPurchaseAtomicRollback()
        {
            const int accountId = 903051;
            const int characterId = 903052;
            const int commodityNo = 102290;
            const int initialCera = 10000;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "cerashop-atomic-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                if (!CeraShopProductCatalog.TryResolve(commodityNo, out var product)
                    || product == null
                    || product.CoinPrice <= 0
                    || PremiumService.IsContractItem(product.ItemTemplateId))
                    return false;

                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var setup = connection.BeginTransaction())
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = setup;
                            command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash, cera)
VALUES(@accountId, 'cerashop-atomic-selftest', '', @cera);
INSERT INTO characters(character_id, account_id, name)
VALUES(@characterId, @accountId, 'cerashop-atomic');";
                            command.Parameters.AddWithValue("@accountId", accountId);
                            command.Parameters.AddWithValue("@characterId", characterId);
                            command.Parameters.AddWithValue("@cera", initialCera);
                            command.ExecuteNonQuery();
                        }

                        setup.Commit();
                    }

                    var database = new GameDatabase(
                        databasePath,
                        ServerPaths.SchemaFilePath);
                    var inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                    var lease = new InventoryLease(
                        Guid.NewGuid(),
                        characterId,
                        inventory,
                        1);

                    using (var transaction = connection.BeginTransaction())
                    {
                        var context = new InventoryCeraShopRuntimeService.TransactionContext(
                            connection,
                            transaction);
                        if (!InventoryCeraShopRuntimeService.TryBuyCeraShopItem(
                                inventory,
                                accountId,
                                commodityNo,
                                1,
                                0,
                                0,
                                0,
                                -1,
                                null,
                                null,
                                out var result,
                                out _,
                                out _,
                                context)
                            || result == null
                            || !InventoryPersistenceService.SaveDirtyInTransaction(
                                connection,
                                transaction,
                                lease))
                            return false;

                        // Deliberately omit Commit: both currency and inventory must roll back.
                    }

                    var wallet = CurrencyService.LoadWallet(connection, null, characterId);
                    var reloaded = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                    return wallet.Cera == initialCera
                        && reloaded.CountMainItem(product.ItemTemplateId) == 0;
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool CheckPremiumAtomicRollback()
        {
            const int accountId = 903061;
            const int characterId = 903062;
            const int initialCera = 100;
            const int initialTokenCera = 1000;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "cerashop-premium-atomic-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                var catalog = DevilContractCatalog.Parse(
                    PvfArchiveAccessor.ReadText("etc/cerashop.etc"));
                if (!catalog.TryFindAllServicePackagePurchase(
                        out _,
                        out var purchase)
                    || purchase == null
                    || purchase.CeraPrice <= 0
                    || !catalog.TryResolveServiceGrants(purchase, out var grants)
                    || grants == null
                    || grants.Count == 0)
                    return false;

                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var setup = connection.BeginTransaction())
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = setup;
                            command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash, cera, token_cera)
VALUES(@accountId, 'cerashop-premium-atomic-selftest', '', @cera, @tokenCera);
INSERT INTO characters(character_id, account_id, name)
VALUES(@characterId, @accountId, 'cerashop-premium-atomic');";
                            command.Parameters.AddWithValue("@accountId", accountId);
                            command.Parameters.AddWithValue("@characterId", characterId);
                            command.Parameters.AddWithValue("@cera", initialCera);
                            command.Parameters.AddWithValue("@tokenCera", initialTokenCera + purchase.CeraPrice);
                            command.ExecuteNonQuery();
                        }

                        setup.Commit();
                    }

                    using (var transaction = connection.BeginTransaction())
                    {
                        var payment = CurrencyService.LoadWallet(
                            connection,
                            transaction,
                            characterId);
                        var spend = purchase.CeraPrice;
                        if (payment.TokenCera < spend)
                            return false;

                        if (!CurrencyService.TrySpendTokenCera(
                                connection,
                                transaction,
                                characterId,
                                spend)
                            || !PremiumService.TryActivateDevilContractServices(
                                connection,
                                transaction,
                                accountId,
                                grants,
                                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                out _))
                            return false;

                        // Deliberately omit Commit: payment and premium activation must roll back together.
                    }

                    var wallet = CurrencyService.LoadWallet(connection, null, characterId);
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT COUNT(*)
FROM account_premiums
WHERE account_id = @accountId;";
                        command.Parameters.AddWithValue("@accountId", accountId);
                        var premiumRows = Convert.ToInt32(command.ExecuteScalar());
                        return wallet.Cera == initialCera
                            && wallet.TokenCera == initialTokenCera + purchase.CeraPrice
                            && premiumRows == 0;
                    }
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool CheckMultiItemPartialCommit()
        {
            const int accountId = 903071;
            const int characterId = 903072;
            const int commodityNo = 102290;
            const int initialCera = 10000;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "cerashop-multi-partial-" + Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;

            try
            {
                if (!CeraShopProductCatalog.TryResolve(commodityNo, out var product)
                    || product == null
                    || product.CoinPrice <= 0
                    || PremiumService.IsContractItem(product.ItemTemplateId))
                {
                    return false;
                }

                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                database.Write((connection, transaction) =>
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash, cera)
VALUES(@accountId, 'cerashop-multi-partial-selftest', '', @cera);
INSERT INTO characters(character_id, account_id, name)
VALUES(@characterId, @accountId, 'cerashop-multi-partial');";
                        command.Parameters.AddWithValue("@accountId", accountId);
                        command.Parameters.AddWithValue("@characterId", characterId);
                        command.Parameters.AddWithValue("@cera", initialCera);
                        command.ExecuteNonQuery();
                    }
                });

                using (var connection = database.OpenConnection())
                {
                    var inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                    lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                }

                if (!CeraShopRuntimePurchaseCommitService.TryPurchase(
                        lease,
                        accountId,
                        commodityNo,
                        paymentMode: 0,
                        attributeValue: 0,
                        couponItemId: 0,
                        couponSlot: -1,
                        itemOptions: null,
                        overflowRewardSink: null,
                        out var first,
                        out _)
                    || first == null)
                {
                    return false;
                }

                database.Write((connection, transaction) =>
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = $@"
CREATE TRIGGER fail_second_cera_item
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {characterId}
BEGIN
    SELECT RAISE(ABORT, 'injected second CeraShop item failure');
END;";
                        command.ExecuteNonQuery();
                    }
                });

                var secondCommitted = CeraShopRuntimePurchaseCommitService.TryPurchase(
                    lease,
                    accountId,
                    commodityNo,
                    paymentMode: 0,
                    attributeValue: 0,
                    couponItemId: 0,
                    couponSlot: -1,
                    itemOptions: null,
                    overflowRewardSink: null,
                    out _,
                    out _);

                using (var connection = database.OpenConnection())
                {
                    var wallet = CurrencyService.LoadWallet(
                        connection,
                        null,
                        characterId);
                    var reloaded = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                    return !secondCommitted
                        && wallet.Cera == initialCera - product.CoinPrice
                        && reloaded.CountMainItem(product.ItemTemplateId) == 1
                        && lease.Inventory.CountMainItem(product.ItemTemplateId) == 1;
                }
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(
                        lease.SessionId,
                        lease.CharacterId);
                SqliteConnection.ClearAllPools();
                foreach (var path in new[]
                {
                    databasePath,
                    databasePath + "-wal",
                    databasePath + "-shm",
                })
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool TryFindFiniteStackableItem(
            out int itemId,
            out byte itemKind,
            out int stackLimit)
        {
            var candidates = new[] { 10000006, 10007350, 10007282, 10007717, 10007836 };
            foreach (var candidate in candidates)
            {
                if (!ItemMetadataResolver.TryResolveItemKind(candidate, out itemKind))
                    continue;

                var core = ItemCore.Create(itemKind, candidate);
                if (!InventoryStackRuleService.IsStackable(core)
                    || !InventoryStackRuleService.TryGetStackLimit(core, out stackLimit)
                    || stackLimit <= 1
                    || stackLimit == int.MaxValue)
                    continue;

                itemId = candidate;
                return true;
            }

            itemId = 0;
            itemKind = 0;
            stackLimit = 0;
            return false;
        }

        private static int CountSourceRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short sourceSlot)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COUNT(*)
FROM character_inventory_items
WHERE character_id = @characterId
  AND list_type = @listType
  AND slot_index = @sourceSlot;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.Main);
                command.Parameters.AddWithValue("@sourceSlot", sourceSlot);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }
    }
}
