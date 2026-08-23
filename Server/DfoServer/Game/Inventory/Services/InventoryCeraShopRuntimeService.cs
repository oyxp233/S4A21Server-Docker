using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DfoServer.Game.Currency;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Shop;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PremiumService = DfoServer.Game.Premium.PremiumService;

namespace DfoServer.Game.Inventory
{
    internal enum CeraShopPurchaseFailure
    {
        Unknown,
        InsufficientCera,
    }

    internal static class InventoryCeraShopRuntimeService
    {
        internal sealed class TransactionContext
        {
            internal TransactionContext(
                SqliteConnection connection,
                SqliteTransaction transaction)
            {
                Connection = connection ?? throw new ArgumentNullException(nameof(connection));
                Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
            }

            internal SqliteConnection Connection { get; }

            internal SqliteTransaction Transaction { get; }
        }

        private enum CeraPayMode
        {
            Default,
            OnlyCera,
            OnlyCeraPoint,
        }

        private enum CeraShopPaymentFailure
        {
            None,
            InsufficientCera,
        }

        private struct CeraShopPaymentPlan
        {
            public bool Ok;
            public CeraShopPaymentFailure Failure;
            public int NewGold;
            public int NewCera;
            public int NewTokenCera;
            public int NewHappyTokenCera;
            public int SpentCera;
            public int SpentTokenCera;
            public int SpentHappyTokenCera;
        }

        private static readonly Dictionary<int, Dictionary<int, int>> AvatarCouponTable = new Dictionary<int, Dictionary<int, int>>
        {
            { 1, new Dictionary<int, int> { { 1, 2681588 }, { 2, 2681589 }, { 3, 2681590 } } },
            { 2, new Dictionary<int, int> { { 1, 2681591 }, { 2, 2681592 }, { 3, 2681593 } } },
            { 3, new Dictionary<int, int> { { 3, 2681594 } } },
        };

        private static readonly int[] AccountCargoCapacityTiers = { 1, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120 };
        private static readonly ushort[] PersonalCargoCapacityTiers =
        {
            24, 40, 56, 72, 88, 104, 120, 136, 152, 168, 184, 200
        };

        internal static bool TryBuyCeraShopItem(
            InventoryService inventory,
            int accountId,
            int productId,
            int buyCount,
            int paymentMode,
            byte attributeValue,
            int purchaseCouponId,
            short purchaseCouponSlot,
            CeraShopPurchaseOptions purchaseOptions,
            MailboxInventoryOverflowRewardSink overflowRewardSink,
            out InventoryMutationResult result,
            out CeraShopPurchaseFailure failure,
            out bool handled,
            TransactionContext transactionContext = null)
        {
            result = null;
            failure = CeraShopPurchaseFailure.Unknown;
            handled = true;

            if (inventory == null || accountId <= 0 || productId <= 0)
                return false;

            if (!CeraShopProductCatalog.TryResolve(productId, out var product)
                || product == null
                || product.ItemTemplateId <= 0)
                return false;

            ItemMetadata metadata;
            try
            {
                metadata = ItemMetadataResolver.Resolve(product.ItemTemplateId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CeraShopRuntime] resolve metadata failed product=0x{productId:X8} item=0x{product.ItemTemplateId:X8}: {ex.Message}");
                return false;
            }

            var itemTemplateId = product.ItemTemplateId;
            var isSkillTreeExpansion = itemTemplateId == SkillTreeExpansionState.ExpansionItemTemplateId;
            var isNameTag = metadata != null
                && !string.Equals(product.Section, "avatar", StringComparison.OrdinalIgnoreCase)
                && string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                && ItemMetadataResolver.IsNameTagItem(itemTemplateId);

            if (metadata == null)
                return false;

            byte itemKind = 0;
            if (!isSkillTreeExpansion
                && !ItemMetadataResolver.TryResolveItemKind(itemTemplateId, metadata, out itemKind))
                return false;

            var isAvatar = !isSkillTreeExpansion
                && (string.Equals(product.Section, "avatar", StringComparison.OrdinalIgnoreCase)
                    || itemKind == ItemCore.KindAvatar);
            var effectiveCount = metadata.IsStackable && !isAvatar
                ? InventoryCeraShopStackPolicy.NormalizeEffectiveStackCount(buyCount, product.Count, metadata.StackLimit)
                : 1;
            if (effectiveCount <= 0)
                return false;

            if (!TryResolveCosts(
                    product,
                    metadata,
                    isAvatar,
                    buyCount,
                    out var avatarDurationDays,
                    out var totalGoldCost,
                    out var totalCeraCost))
                return false;

            var ceraMode = ResolveCeraPayMode(itemTemplateId);
            var couponId = 0;
            if (paymentMode == 1)
            {
                if (!isAvatar || !TryGetAvatarCouponId(metadata.Grade, Math.Max(1, product.Count), out couponId))
                    return false;

                totalCeraCost = 0;
            }
            else if (paymentMode != 0)
            {
                return false;
            }

            if (purchaseCouponId > 0)
            {
                if (paymentMode != 0
                    || purchaseCouponSlot < 0
                    || !IsPurchaseCoupon(purchaseCouponId)
                    || !inventory.TryGetItem(InventoryListType.Main, purchaseCouponSlot, out var couponItem)
                    || couponItem == null
                    || couponItem.ItemId != purchaseCouponId
                    || couponItem.Count <= 0)
                    return false;

                couponId = purchaseCouponId;
            }

            if (!CanSpendInventoryCosts(inventory, totalGoldCost, couponId))
                return false;

            if (isSkillTreeExpansion)
                return TryBuySkillTreeExpansion(
                    inventory,
                    itemTemplateId,
                    buyCount,
                    paymentMode,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    couponId,
                    out result,
                    transactionContext);

            if (isNameTag)
                return TryBuyNameTag(
                    inventory,
                    product,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    couponId,
                    out result,
                    transactionContext);

            if (string.Equals(metadata.ItemKind, "special", StringComparison.Ordinal))
                return false;

            if (TryResolvePersonalCargoUpgradeTarget(itemTemplateId, out var personalCargoTarget))
                return TryBuyPersonalCargoUpgrade(
                    inventory,
                    itemTemplateId,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    couponId,
                    personalCargoTarget,
                    out result,
                    transactionContext);

            if (InventoryCargoUpgradeRule.IsAccountCargoUpgradeToolItem(itemTemplateId))
                return TryBuyAccountCargoUpgrade(
                    inventory,
                    itemTemplateId,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    couponId,
                    out result,
                    transactionContext);

            if (PremiumService.IsContractItem(itemTemplateId))
                return TryBuyConsumedPremium(
                    inventory,
                    itemTemplateId,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    couponId,
                    out result,
                    transactionContext);

            return TryBuyInventoryReward(
                inventory,
                product,
                metadata,
                effectiveCount,
                totalGoldCost,
                totalCeraCost,
                ceraMode,
                couponId,
                isAvatar,
                attributeValue,
                purchaseOptions,
                avatarDurationDays,
                overflowRewardSink,
                out result,
                out failure,
                transactionContext);
        }

        internal static bool IsPurchaseCoupon(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return false;

            try
            {
                var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
                return metadata != null
                    && metadata.IsStackable
                    && metadata.IsPrimaryStackableFamily("coupon");
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBuySkillTreeExpansion(
            InventoryService inventory,
            int itemTemplateId,
            int buyCount,
            int paymentMode,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            out InventoryMutationResult result,
            TransactionContext transactionContext)
        {
            result = null;
            if (buyCount != 1 || paymentMode != 0 || couponId > 0)
                return false;

            if (!TrySpendPaymentAndApplyDbAction(
                    inventory,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    (connection, transaction) =>
                        SkillTreeExpansionService.TryUnlock(
                            connection,
                            transaction,
                            inventory.CharacterId),
                    out var payment,
                    transactionContext))
                return false;

            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            result = new InventoryMutationResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = -1,
                ItemTemplateId = itemTemplateId,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = totalGoldCost > 0,
                RequestedCount = 1,
                AppliedCount = 1,
                ConsumedOnPurchase = true,
            };
            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            FileLogger.Log($"[CeraShopRuntime] skill-tree expansion unlocked cid={inventory.CharacterId} item=0x{itemTemplateId:X8}");
            return true;
        }

        private static bool TryBuyNameTag(
            InventoryService inventory,
            CeraShopProductEntry product,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            out InventoryMutationResult result,
            TransactionContext transactionContext)
        {
            result = null;
            if (inventory == null || product == null || couponId > 0)
                return false;

            var durationDays = Math.Max(0, product.DurationDays);
            var unixNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expireTime = durationDays > 0
                ? (int)Math.Min(int.MaxValue, unixNow + (long)durationDays * 86400L)
                : 0;

            if (!TrySpendPaymentAndApplyDbAction(
                    inventory,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    (connection, transaction) =>
                    {
                        NameTagStateRepository.Upsert(
                            connection,
                            transaction,
                            inventory.CharacterId,
                            product.ItemTemplateId,
                            expireTime);
                        return true;
                    },
                    out var payment,
                    transactionContext))
                return false;

            inventory.NameTag.Set(product.ItemTemplateId, expireTime);
            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            result = new InventoryMutationResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = -1,
                ItemTemplateId = product.ItemTemplateId,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = totalGoldCost > 0,
                RequestedCount = 1,
                AppliedCount = 1,
                ConsumedOnPurchase = true,
                NameTagEquipped = true,
            };
            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            FileLogger.Log($"[CeraShopRuntime] name tag equipped cid={inventory.CharacterId} item=0x{product.ItemTemplateId:X8} expire={expireTime}");
            return true;
        }

        private static bool TryBuyInventoryReward(
            InventoryService inventory,
            CeraShopProductEntry product,
            ItemMetadata metadata,
            int effectiveCount,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            bool isAvatar,
            byte attributeValue,
            CeraShopPurchaseOptions purchaseOptions,
            int avatarDurationDays,
            MailboxInventoryOverflowRewardSink overflowRewardSink,
            out InventoryMutationResult result,
            out CeraShopPurchaseFailure failure,
            TransactionContext transactionContext)
        {
            result = null;
            failure = CeraShopPurchaseFailure.Unknown;
            var requests = BuildGrantRequests(
                inventory,
                product,
                metadata,
                effectiveCount,
                isAvatar,
                attributeValue,
                purchaseOptions,
                avatarDurationDays);
            if (requests.Count == 0)
                return false;

            var planningInventory = InventorySpecialConsumableService.CreatePlanningInventory(inventory);
            if (!InventorySpecialConsumableService.TryPlanDirectRewards(
                    planningInventory,
                    requests,
                    out var directPlan,
                    out var overflowRewards)
                || directPlan == null)
            {
                FileLogger.Log($"[CeraShopRuntime] grant plan failed product=0x{product.ProductId:X8} item=0x{product.ItemTemplateId:X8} rewards={FormatGrantRequests(requests, 12)}");
                return false;
            }

            if (directPlan.Entries.Count == 0 && overflowRewards.Count == 0)
                return false;

            Func<SqliteConnection, SqliteTransaction, bool> mailAction = null;
            if (overflowRewards.Count > 0)
            {
                if (overflowRewardSink == null)
                    return false;
                mailAction = (connection, transaction) =>
                    overflowRewardSink.TryDeliver(
                        connection,
                        transaction,
                        inventory,
                        overflowRewards,
                        null,
                        null,
                        out _);
            }

            if (!TrySpendPayment(
                    inventory,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    mailAction,
                    out var payment,
                    transactionContext))
            {
                if (payment.Failure == CeraShopPaymentFailure.InsufficientCera)
                    failure = CeraShopPurchaseFailure.InsufficientCera;
                return false;
            }

            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            InventoryRewardGrantBatchResult grant = null;
            if (directPlan.Entries.Count > 0)
            {
                if (!InventoryRewardGrantService.TryApplyPreparedBatch(inventory, directPlan, out grant)
                    || grant == null
                    || !grant.Success
                    || grant.Results.Count == 0)
                    return false;

                result = ToMutationResult(inventory, grant.Results[0], payment, effectiveCount, totalGoldCost > 0);
                for (var index = 1; index < grant.Results.Count; index++)
                    result.ExtraResults.Add(ToMutationResult(inventory, grant.Results[index], payment, effectiveCount, false));
            }
            else
            {
                result = new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = -1,
                    ItemTemplateId = product.ItemTemplateId,
                    UpdatedGold = payment.NewGold,
                    UpdatedCoin = payment.NewCera,
                    UpdatedTokenCera = payment.NewTokenCera,
                    UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                    GoldSpent = totalGoldCost > 0,
                    RequestedCount = (short)Math.Min(short.MaxValue, Math.Max(1, effectiveCount)),
                    AppliedCount = 0,
                };
            }

            if (overflowRewards.Count > 0)
            {
                result.DeliveredByMail = true;
                FileLogger.Log(
                    $"[CeraShopRuntime] grant overflow mailed product=0x{product.ProductId:X8} " +
                    $"item=0x{product.ItemTemplateId:X8} direct={directPlan.Entries.Count} " +
                    $"overflow={FormatGrantRequests(overflowRewards, 12)}");
            }

            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            return true;
        }

        private static bool TryBuyConsumedPremium(
            InventoryService inventory,
            int itemTemplateId,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            out InventoryMutationResult result,
            TransactionContext transactionContext)
        {
            result = null;
            if (!TrySpendPayment(
                    inventory,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    out var payment,
                    transactionContext))
                return false;

            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            result = new InventoryMutationResult
            {
                ItemTemplateId = itemTemplateId,
                ConsumedOnPurchase = true,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = totalGoldCost > 0,
                RequestedCount = 1,
                AppliedCount = 1,
            };
            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            return true;
        }

        private static bool TryBuyPersonalCargoUpgrade(
            InventoryService inventory,
            int itemTemplateId,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            ushort targetCapacity,
            out InventoryMutationResult result,
            TransactionContext transactionContext)
        {
            result = null;
            var current = inventory.Cargo.Capacity;
            if (!IsPersonalCargoCapacityTier(targetCapacity) || targetCapacity <= current)
                return false;

            if (!TrySpendPayment(
                    inventory,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    out var payment,
                    transactionContext))
                return false;

            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            inventory.SetListParam16(InventoryListType.PersonalCargo, targetCapacity);
            result = new InventoryMutationResult
            {
                ListType = InventoryListType.PersonalCargo,
                SlotIndex = -1,
                ItemTemplateId = itemTemplateId,
                RemainingStackCount = targetCapacity,
                InstanceValue = targetCapacity,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = totalGoldCost > 0,
                ConsumedOnPurchase = true,
                RequestedCount = 1,
                AppliedCount = 1,
            };
            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            return true;
        }

        private static bool TryBuyAccountCargoUpgrade(
            InventoryService inventory,
            int itemTemplateId,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            out InventoryMutationResult result,
            TransactionContext transactionContext)
        {
            result = null;
            if (!TryGetNextAccountCargoCapacity(inventory.AccountCargo.SelectionKey, out var nextCapacity))
                return false;

            if (!TrySpendPayment(
                    inventory,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    out var payment,
                    transactionContext))
                return false;

            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            inventory.SetListParam16(InventoryListType.AccountCargo, (ushort)nextCapacity);
            result = new InventoryMutationResult
            {
                ListType = InventoryListType.AccountCargo,
                SlotIndex = -1,
                ItemTemplateId = itemTemplateId,
                RemainingStackCount = nextCapacity,
                InstanceValue = nextCapacity,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = totalGoldCost > 0,
                ConsumedOnPurchase = true,
                RequestedCount = 1,
                AppliedCount = 1,
            };
            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            return true;
        }

        private static List<InventoryRewardGrantRequest> BuildGrantRequests(
            InventoryService inventory,
            CeraShopProductEntry product,
            ItemMetadata metadata,
            int effectiveCount,
            bool isAvatar,
            byte attributeValue,
            CeraShopPurchaseOptions purchaseOptions,
            int avatarDurationDays)
        {
            var requests = new List<InventoryRewardGrantRequest>();
            if (TryResolveMallAutoOpenRequests(
                    inventory,
                    product.ItemTemplateId,
                    effectiveCount,
                    purchaseOptions,
                    out var mallRequests))
                return mallRequests;

            requests.Add(InventoryRewardGrantRequest.Create(
                product.ItemTemplateId,
                effectiveCount,
                ItemCreateReason.MallPurchase,
                CreateOptions(isAvatar, attributeValue, avatarDurationDays)));
            return requests;
        }

        private static bool TryResolveMallAutoOpenRequests(
            InventoryService inventory,
            int itemTemplateId,
            int effectiveCount,
            CeraShopPurchaseOptions purchaseOptions,
            out List<InventoryRewardGrantRequest> requests)
        {
            requests = null;
            try
            {
                var stackable = StackableItemProvider.Load(itemTemplateId);
                if (stackable == null)
                    return false;

                var stackableType = InventoryPackageRewardResolver.NormalizeStackableType(stackable.StackableType);
                if (stackableType.Equals("[cera package]", StringComparison.OrdinalIgnoreCase))
                {
                    if (HasPackageSelectionGroups(stackable) || (purchaseOptions != null && purchaseOptions.HasAny))
                    {
                        if (!TryBuildSelectedCeraPackageRequests(
                                itemTemplateId,
                                stackable,
                                purchaseOptions,
                                out var unitRequests))
                            return false;

                        requests = RepeatRequests(unitRequests, effectiveCount);
                        return requests.Count > 0;
                    }
                }

                if (!TryResolveMallAutoOpenRewards(
                        inventory,
                        itemTemplateId,
                        effectiveCount,
                        out var rewards))
                    return false;

                requests = InventorySpecialConsumableService.BuildRewardRequests(
                    rewards,
                    skipExpiredStaticItems: true);
                return requests.Count > 0;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CeraShopRuntime] resolve mall auto-open requests failed item=0x{itemTemplateId:X8}: {ex.Message}");
                return false;
            }
        }

        private static bool TryResolveMallAutoOpenRewards(
            InventoryService inventory,
            int itemTemplateId,
            int effectiveCount,
            out List<PvfLib.BoosterRewardEntry> rewards)
        {
            rewards = null;
            var characterId = inventory?.CharacterId ?? 0;
            try
            {
                var database = inventory?.Database
                    ?? GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        if (!InventoryPackageRewardResolver.TryResolveMallAutoOpenRewards(
                                connection,
                                transaction,
                                characterId,
                                itemTemplateId,
                                out var unitRewards)
                            || unitRewards == null
                            || unitRewards.Count == 0)
                            return false;

                        var allRewards = new List<PvfLib.BoosterRewardEntry>();
                        var repeat = Math.Max(1, effectiveCount);
                        for (var index = 0; index < repeat; index++)
                            allRewards.AddRange(unitRewards);

                        rewards = InventoryPackageRewardResolver.AggregateRewards(allRewards);
                        transaction.Commit();
                        return rewards.Count > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CeraShopRuntime] resolve mall auto-open rewards failed item=0x{itemTemplateId:X8}: {ex.Message}");
                return false;
            }
        }

        private static bool TryBuildSelectedCeraPackageRequests(
            int packageItemTemplateId,
            PvfLib.StackableItemFile stackable,
            CeraShopPurchaseOptions purchaseOptions,
            out List<InventoryRewardGrantRequest> requests)
        {
            requests = new List<InventoryRewardGrantRequest>();
            if (stackable == null)
                return false;

            var avatarChoices = BuildAvatarChoiceMap(purchaseOptions);
            foreach (var reward in stackable.PackageRewards ?? new List<PvfLib.BoosterRewardEntry>())
            {
                if (reward == null || reward.ItemId <= 0 || reward.Count <= 0)
                    continue;

                byte avatarAbilityNo = 0;
                if (IsAvatarReward(reward.ItemId)
                    && avatarChoices.Count > 0
                    && !avatarChoices.TryGetValue(reward.ItemId, out avatarAbilityNo))
                    return false;

                InventorySpecialConsumableService.AddRewardRequest(
                    requests,
                    reward.ItemId,
                    reward.Count,
                    ResolveRewardExpireTime(reward),
                    avatarAbilityNo,
                    skipExpiredStaticItems: true);
            }

            var selectionGroups = ParsePackageSelectionGroups(stackable);
            if (selectionGroups.Count == 0)
                return requests.Count > 0;

            if (purchaseOptions == null || purchaseOptions.SelectionChoices.Count == 0)
                return false;

            var selectedGroups = new HashSet<int>();
            foreach (var choice in purchaseOptions.SelectionChoices)
            {
                if (choice == null)
                    continue;
                if (choice.PackageItemTemplateId > 0 && choice.PackageItemTemplateId != packageItemTemplateId)
                    return false;

                var groupIndex = (int)choice.GroupIndex;
                if (groupIndex < 0 || groupIndex >= selectionGroups.Count)
                    return false;
                if (!selectedGroups.Add(groupIndex))
                    return false;

                var group = selectionGroups[groupIndex];
                var selectionIndex = (int)choice.SelectionIndex;
                if (selectionIndex < 0 || selectionIndex >= group.Count)
                    return false;

                var reward = group[selectionIndex];
                InventorySpecialConsumableService.AddRewardRequest(
                    requests,
                    reward.ItemId,
                    reward.Count,
                    ResolveRewardExpireTime(reward),
                    0,
                    skipExpiredStaticItems: true);
            }

            return selectedGroups.Count == selectionGroups.Count && requests.Count > 0;
        }

        private static Dictionary<int, byte> BuildAvatarChoiceMap(CeraShopPurchaseOptions purchaseOptions)
        {
            var map = new Dictionary<int, byte>();
            if (purchaseOptions == null)
                return map;

            foreach (var choice in purchaseOptions.AvatarChoices)
            {
                if (choice == null || choice.ItemTemplateId <= 0)
                    continue;
                if (!map.ContainsKey(choice.ItemTemplateId))
                    map.Add(choice.ItemTemplateId, choice.OptionValue);
            }

            return map;
        }

        private static bool HasPackageSelectionGroups(PvfLib.StackableItemFile stackable)
        {
            return ParsePackageSelectionGroups(stackable).Count > 0;
        }

        private static List<List<PvfLib.BoosterRewardEntry>> ParsePackageSelectionGroups(
            PvfLib.StackableItemFile stackable)
        {
            var groups = new List<List<PvfLib.BoosterRewardEntry>>();
            if (stackable?.Root == null)
                return groups;

            foreach (var node in stackable.Root.GetChildren("package data selection"))
            {
                var values = ParseInts(node.GetContent(stackable.Content));
                var group = new List<PvfLib.BoosterRewardEntry>();
                for (var index = 0; index + 1 < values.Count; index += 2)
                {
                    if (values[index] <= 0)
                        continue;

                    group.Add(new PvfLib.BoosterRewardEntry
                    {
                        RewardKind = "package selection",
                        ItemId = values[index],
                        Count = Math.Max(1, values[index + 1]),
                    });
                }

                if (group.Count > 0)
                    groups.Add(group);
            }

            return groups;
        }

        private static List<int> ParseInts(string value)
        {
            var values = new List<int>();
            foreach (Match match in Regex.Matches(value ?? string.Empty, "-?\\d+"))
            {
                if (int.TryParse(match.Value, out var parsed))
                    values.Add(parsed);
            }

            return values;
        }

        private static int ResolveRewardExpireTime(PvfLib.BoosterRewardEntry reward)
        {
            return reward != null && reward.UsablePeriodDays > 0
                ? PvfExpirationMetadata.AddDaysFromNow(reward.UsablePeriodDays)
                : 0;
        }

        private static bool IsAvatarReward(int itemTemplateId)
        {
            try
            {
                return SelectablePackageDefinitionResolver.IsAvatarEquipment(itemTemplateId);
            }
            catch
            {
                return false;
            }
        }

        private static List<InventoryRewardGrantRequest> RepeatRequests(
            IReadOnlyList<InventoryRewardGrantRequest> unitRequests,
            int effectiveCount)
        {
            var requests = new List<InventoryRewardGrantRequest>();
            if (unitRequests == null || unitRequests.Count == 0)
                return requests;

            var repeat = Math.Max(1, effectiveCount);
            for (var index = 0; index < repeat; index++)
            {
                foreach (var request in unitRequests)
                {
                    if (request == null)
                        continue;

                    requests.Add(InventoryRewardGrantRequest.Create(
                        request.ItemTemplateId,
                        request.Count,
                        request.Reason,
                        request.CreateOptions));
                }
            }

            return requests;
        }

        private static string FormatGrantRequests(
            IReadOnlyList<InventoryRewardGrantRequest> requests,
            int maxCount)
        {
            if (requests == null || requests.Count == 0)
                return "none";

            var parts = new List<string>();
            var limit = Math.Min(requests.Count, Math.Max(0, maxCount));
            for (var index = 0; index < limit; index++)
            {
                var request = requests[index];
                parts.Add(request == null
                    ? "null"
                    : $"0x{request.ItemTemplateId:X8}x{request.Count}");
            }

            if (requests.Count > limit)
                parts.Add($"...+{requests.Count - limit}");

            return string.Join(",", parts);
        }

        private static bool TryResolveCosts(
            CeraShopProductEntry product,
            ItemMetadata metadata,
            bool isAvatar,
            int buyCount,
            out int avatarDurationDays,
            out int totalGoldCost,
            out int totalCeraCost)
        {
            avatarDurationDays = 0;
            totalGoldCost = 0;
            totalCeraCost = 0;

            var goldPrice = Math.Max(0, product.GoldPrice);
            var ceraPrice = Math.Max(0, product.CoinPrice);
            if (isAvatar)
            {
                goldPrice = 0;
                if (AvatarTypeSelectResolver.TryGetOption(
                        product.ItemTemplateId,
                        Math.Max(1, product.Count),
                        out var durationDays,
                        out var avatarPrice))
                {
                    avatarDurationDays = durationDays;
                    if (avatarPrice > 0)
                        ceraPrice = avatarPrice;
                }
            }

            var perUnit = Math.Max(1, buyCount);
            var gold = (long)goldPrice * perUnit;
            var cera = (long)ceraPrice * perUnit;
            if (gold > int.MaxValue || cera > int.MaxValue)
                return false;

            totalGoldCost = (int)gold;
            totalCeraCost = (int)cera;
            return true;
        }

        private static bool TrySpendPayment(
            InventoryService inventory,
            int goldCost,
            int ceraCost,
            CeraPayMode mode,
            out CeraShopPaymentPlan plan,
            TransactionContext transactionContext = null)
        {
            return TrySpendPayment(
                inventory,
                goldCost,
                ceraCost,
                mode,
                null,
                out plan,
                transactionContext);
        }

        private static bool TrySpendPayment(
            InventoryService inventory,
            int goldCost,
            int ceraCost,
            CeraPayMode mode,
            Func<SqliteConnection, SqliteTransaction, bool> action,
            out CeraShopPaymentPlan plan,
            TransactionContext transactionContext = null)
        {
            plan = default;
            if (transactionContext != null)
            {
                var wallet = CurrencyService.LoadWallet(
                    transactionContext.Connection,
                    transactionContext.Transaction,
                    inventory.CharacterId);
                wallet.Gold = inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart);
                plan = ComputePayment(wallet, goldCost, ceraCost, mode);
                if (!plan.Ok
                    || (action != null && !action(
                        transactionContext.Connection,
                        transactionContext.Transaction))
                    || !ApplyCeraPayment(
                        transactionContext.Connection,
                        transactionContext.Transaction,
                        inventory.CharacterId,
                        plan))
                {
                    return false;
                }

                return true;
            }

            return inventory != null
                && TrySpendPaymentAndApplyDbAction(
                    inventory.CharacterId,
                    inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart),
                    inventory.Database?.ConnectionString
                        ?? GameDatabase.CreateDefault().ConnectionString,
                    goldCost,
                    ceraCost,
                    mode,
                    action,
                    out plan);
        }

        private static bool TrySpendPaymentAndApplyDbAction(
            InventoryService inventory,
            int goldCost,
            int ceraCost,
            CeraPayMode mode,
            Func<SqliteConnection, SqliteTransaction, bool> action,
            out CeraShopPaymentPlan plan,
            TransactionContext transactionContext = null)
        {
            plan = default;
            if (transactionContext != null)
            {
                var wallet = CurrencyService.LoadWallet(
                    transactionContext.Connection,
                    transactionContext.Transaction,
                    inventory.CharacterId);
                wallet.Gold = inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart);
                plan = ComputePayment(wallet, goldCost, ceraCost, mode);
                return plan.Ok
                    && (action == null || action(
                        transactionContext.Connection,
                        transactionContext.Transaction))
                    && ApplyCeraPayment(
                        transactionContext.Connection,
                        transactionContext.Transaction,
                        inventory.CharacterId,
                        plan);
            }

            return inventory != null
                && TrySpendPaymentAndApplyDbAction(
                    inventory.CharacterId,
                    inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart),
                    inventory.Database?.ConnectionString
                        ?? GameDatabase.CreateDefault().ConnectionString,
                    goldCost,
                    ceraCost,
                    mode,
                    action,
                    out plan);
        }

        internal static bool TrySpendCeraPaymentAndApplyDbAction(
            string connectionString,
            int characterId,
            int itemTemplateId,
            int ceraCost,
            Func<SqliteConnection, SqliteTransaction, bool> action,
            out CeraShopPaymentResult result)
        {
            result = null;
            if (characterId <= 0 || itemTemplateId <= 0 || ceraCost < 0)
                return false;

            if (!TrySpendPaymentAndApplyDbAction(
                    characterId,
                    0,
                    connectionString,
                    0,
                    ceraCost,
                    ResolveCeraPayMode(itemTemplateId),
                    action,
                    out var payment))
                return false;

            result = new CeraShopPaymentResult
            {
                NewCera = payment.NewCera,
                NewTokenCera = payment.NewTokenCera,
                NewHappyTokenCera = payment.NewHappyTokenCera,
            };
            return true;
        }

        private static bool TrySpendPaymentAndApplyDbAction(
            int characterId,
            int currentGold,
            string connectionString,
            int goldCost,
            int ceraCost,
            CeraPayMode mode,
            Func<SqliteConnection, SqliteTransaction, bool> action,
            out CeraShopPaymentPlan plan)
        {
            plan = default;
            try
            {
                var resolvedConnectionString = connectionString
                    ?? GameDatabase.CreateDefault().ConnectionString;
                using (var connection = new SqliteConnection(resolvedConnectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        var wallet = CurrencyService.LoadWallet(connection, transaction, characterId);
                        wallet.Gold = currentGold;
                        plan = ComputePayment(wallet, goldCost, ceraCost, mode);
                        if (!plan.Ok
                            || (action != null && !action(connection, transaction))
                            || !ApplyCeraPayment(connection, transaction, characterId, plan))
                            return false;

                        transaction.Commit();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CeraShopRuntime] payment action failed cid={characterId} gold={goldCost} cera={ceraCost}: {ex.Message}");
                return false;
            }
        }

        private static bool ApplyCeraPayment(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            CeraShopPaymentPlan plan)
        {
            if (plan.SpentHappyTokenCera > 0
                && !CurrencyService.TrySpendHappyTokenCera(connection, transaction, characterId, plan.SpentHappyTokenCera))
                return false;
            if (plan.SpentTokenCera > 0
                && !CurrencyService.TrySpendTokenCera(connection, transaction, characterId, plan.SpentTokenCera))
                return false;
            if (plan.SpentCera > 0
                && !CurrencyService.TrySpendCera(connection, transaction, characterId, plan.SpentCera))
                return false;

            return true;
        }

        private static CeraShopPaymentPlan ComputePayment(WalletSnapshot wallet, int goldCost, int ceraCost, CeraPayMode mode)
        {
            var plan = new CeraShopPaymentPlan
            {
                Ok = false,
                NewGold = wallet.Gold,
                NewCera = wallet.Cera,
                NewTokenCera = wallet.TokenCera,
                NewHappyTokenCera = wallet.HappyTokenCera,
            };

            if (goldCost > 0)
            {
                if (wallet.Gold < goldCost)
                    return plan;
                plan.NewGold = wallet.Gold - goldCost;
            }

            if (ceraCost > 0)
            {
                var useHappy = mode != CeraPayMode.OnlyCera;
                var useToken = mode != CeraPayMode.OnlyCera;
                var useCera = mode != CeraPayMode.OnlyCeraPoint;
                var remaining = ceraCost;
                var happy = plan.NewHappyTokenCera;
                var token = plan.NewTokenCera;
                var cera = plan.NewCera;

                if (useHappy && remaining > 0)
                {
                    var spent = Math.Min(remaining, happy);
                    happy -= spent;
                    remaining -= spent;
                    plan.SpentHappyTokenCera = spent;
                }
                if (useToken && remaining > 0)
                {
                    var spent = Math.Min(remaining, token);
                    token -= spent;
                    remaining -= spent;
                    plan.SpentTokenCera = spent;
                }
                if (useCera && remaining > 0)
                {
                    var spent = Math.Min(remaining, cera);
                    cera -= spent;
                    remaining -= spent;
                    plan.SpentCera = spent;
                }
                if (remaining > 0)
                {
                    plan.Failure = CeraShopPaymentFailure.InsufficientCera;
                    return plan;
                }

                plan.NewHappyTokenCera = happy;
                plan.NewTokenCera = token;
                plan.NewCera = cera;
            }

            plan.Ok = true;
            return plan;
        }

        private static bool CanSpendInventoryCosts(InventoryService inventory, int goldCost, int couponId)
        {
            if (inventory == null || goldCost < 0)
                return false;

            if (goldCost > 0 && inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart) < goldCost)
                return false;

            return couponId <= 0 || inventory.CountMainItem(couponId) >= 1;
        }

        private static bool ApplyInventoryCosts(
            InventoryService inventory,
            int goldCost,
            int couponId,
            List<InventoryMutationResult> costMutations)
        {
            if (goldCost > 0)
            {
                if (!inventory.TryConsumeMainItem(
                        InventoryService.MainVirtualCurrencySlotStart,
                        goldCost,
                        out var gold)
                    || !gold.Success)
                    return false;
            }

            if (couponId > 0)
            {
                if (!inventory.TryConsumeMainItem(couponId, 1, out var coupon) || !coupon.Success)
                    return false;

                costMutations?.Add(new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = coupon.SlotIndex,
                    ItemTemplateId = couponId,
                    RemainingStackCount = coupon.RemainingCount,
                    InstanceValue = coupon.RemainingCount,
                    CostItemTemplateId = couponId,
                    CostItemRemainingCount = coupon.RemainingCount,
                    CostItemSlotIndex = coupon.SlotIndex,
                    RequestedCount = 1,
                    AppliedCount = 1,
                });
            }

            return true;
        }

        private static InventoryMutationResult ToMutationResult(
            InventoryService inventory,
            InventoryRewardGrantResult grant,
            CeraShopPaymentPlan payment,
            int requestedCount,
            bool goldSpent)
        {
            var result = new InventoryMutationResult
            {
                ListType = grant.ListType,
                SlotIndex = grant.SlotIndex,
                ItemTemplateId = grant.ItemTemplateId,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = goldSpent,
                RequestedCount = (short)Math.Min(short.MaxValue, Math.Max(1, requestedCount)),
                AppliedCount = (short)Math.Min(short.MaxValue, Math.Max(1, grant.GrantedCount)),
            };

            if (grant.Kind == InventoryRewardGrantKind.Premium)
            {
                result.ConsumedOnPurchase = true;
                return result;
            }

            if (grant.Kind == InventoryRewardGrantKind.MainVirtualCount)
            {
                result.RemainingStackCount = grant.FinalCount;
                result.InstanceValue = grant.FinalCount;
                return result;
            }

            var core = grant.SlotIndex >= 0
                ? inventory.GetItem(grant.ListType, grant.SlotIndex)
                : null;
            if (core == null)
                core = grant.Core;

            if (core != null)
            {
                result.ItemTemplateId = core.ItemId;
                result.RemainingStackCount = InventoryStackRuleService.IsStackable(core)
                    ? core.Count
                    : Math.Max(1, grant.GrantedCount);
                result.InstanceValue = core.Value;
                result.Durability = core.Durability;
                result.ExtData0 = core.Attr;
                result.ExpireTime = core.ExpireTime;
            }

            return result;
        }

        private static InventoryCreateOptions CreateOptions(bool isAvatar, byte attributeValue, int avatarDurationDays)
        {
            if (!isAvatar && avatarDurationDays <= 0 && attributeValue == 0)
                return null;

            var expireTime = avatarDurationDays > 0
                ? PvfExpirationMetadata.AddDaysFromNow(avatarDurationDays)
                : 0;
            return new InventoryCreateOptions
            {
                AvatarAbilityNo = isAvatar ? attributeValue : (ushort)0,
                ExpireTime = expireTime,
            };
        }

        private static CeraPayMode ResolveCeraPayMode(int itemTemplateId)
        {
            if (CeraShopProductCatalog.IsBuyOnlyCera(itemTemplateId))
                return CeraPayMode.OnlyCera;
            if (CeraShopProductCatalog.IsBuyOnlyCeraPoint(itemTemplateId))
                return CeraPayMode.OnlyCeraPoint;
            return CeraPayMode.Default;
        }

        private static bool TryGetAvatarCouponId(int grade, int durIndex, out int couponId)
        {
            couponId = 0;
            if (grade == 3)
            {
                if (durIndex != 3)
                    return false;

                couponId = 2681594;
                return true;
            }

            return AvatarCouponTable.TryGetValue(grade, out var durMap)
                && durMap.TryGetValue(durIndex, out couponId);
        }

        private static bool TryResolvePersonalCargoUpgradeTarget(int itemTemplateId, out ushort targetCapacity)
        {
            return InventoryCargoUpgradeRule.TryResolvePersonalCargoUpgradeTarget(itemTemplateId, out targetCapacity);
        }

        private static bool IsPersonalCargoCapacityTier(ushort capacity)
        {
            for (var index = 0; index < PersonalCargoCapacityTiers.Length; index++)
                if (PersonalCargoCapacityTiers[index] == capacity)
                    return true;

            return false;
        }

        private static bool TryGetNextAccountCargoCapacity(ushort current, out int next)
        {
            next = 0;
            var index = Array.IndexOf(AccountCargoCapacityTiers, (int)current);
            if (index < 0 || index + 1 >= AccountCargoCapacityTiers.Length)
                return false;

            next = AccountCargoCapacityTiers[index + 1];
            return true;
        }

    }

    internal sealed class CeraShopPaymentResult
    {
        public int NewCera { get; set; }

        public int NewTokenCera { get; set; }

        public int NewHappyTokenCera { get; set; }
    }
}
