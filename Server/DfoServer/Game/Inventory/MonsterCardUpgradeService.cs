using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class MonsterCardUpgradeConfig
    {
        internal const int ProbabilityDenominator = 100000;
        internal int CalculateConst { get; set; }
        internal int GoldCost { get; set; }

        internal int CalculateChance(int targetRarity, int materialRarity, bool sameItem = false)
        {
            if (targetRarity < 0 || targetRarity > 3 || materialRarity < 0 || materialRarity > 3)
                return 0;
            if (sameItem)
                return ProbabilityDenominator;
            if (materialRarity > targetRarity)
                return ProbabilityDenominator;

            var basePercent = 80 - targetRarity * 10;
            var chance = basePercent * 1000L;
            for (var gap = targetRarity - materialRarity; gap > 0; gap--)
                chance /= 2;
            chance = chance * Math.Max(0, CalculateConst) / 10;
            return (int)Math.Min(ProbabilityDenominator, chance);
        }
    }

    internal static class MonsterCardUpgradeConfigProvider
    {
        private const string PvfPath = "character/expertjob/enchanter.exj";
        private static readonly Lazy<MonsterCardUpgradeConfig> Value =
            new Lazy<MonsterCardUpgradeConfig>(Load, true);

        internal static MonsterCardUpgradeConfig Config => Value.Value;

        internal static MonsterCardUpgradeConfig Parse(string content)
        {
            var root = new ScriptParser().Parse(content);
            var config = new MonsterCardUpgradeConfig
            {
                CalculateConst = ReadSingleInt(root, content, "monster card upgrade calculate const"),
                GoldCost = ReadSingleInt(root, content, "card upgrade cost"),
            };
            if (config.CalculateConst <= 0 || config.GoldCost < 0)
                throw new InvalidOperationException($"PVF {PvfPath} has invalid monster card upgrade config");
            return config;
        }

        private static MonsterCardUpgradeConfig Load()
            => Parse(PvfArchiveAccessor.ReadText(PvfPath));

        private static int ReadSingleInt(ScriptNode root, string content, string tag)
        {
            var node = root.GetChild(tag);
            if (node == null
                || !int.TryParse(node.GetFirstDataContent(content).Trim(), out var value))
                throw new InvalidOperationException($"PVF {PvfPath} [{tag}] is invalid");
            return value;
        }
    }

    internal sealed class MonsterCardUpgradeResult
    {
        internal short TargetSlot { get; set; }
        internal int TargetItemId { get; set; }
        internal short MaterialSlot { get; set; }
        internal short ResultSlot { get; set; }
        internal bool Success { get; set; }
        internal byte UpgradeCount { get; set; }
        internal int Chance { get; set; }
        internal int GoldCost { get; set; }
        internal int UpdatedGold { get; set; }
    }

    internal sealed class MonsterCardUpgradeService
    {
        private readonly MonsterCardUpgradeConfig _config;
        private readonly Func<int, int> _next;

        internal MonsterCardUpgradeService()
            : this(MonsterCardUpgradeConfigProvider.Config, ServerRandom.Next)
        {
        }

        internal MonsterCardUpgradeService(MonsterCardUpgradeConfig config, Func<int, int> next)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        internal bool TryUpgrade(
            InventoryService inventory,
            InventoryListType listType,
            short targetSlot,
            short materialSlot,
            short materialCount,
            out MonsterCardUpgradeResult result,
            out string rejection)
        {
            result = null;
            rejection = null;
            if (inventory == null || listType != InventoryListType.Main || materialCount != 1)
                return Reject("invalid request", out rejection);
            if (!inventory.TryGetItem(listType, targetSlot, out var target)
                || !inventory.TryGetItem(listType, materialSlot, out var material))
                return Reject("requested slot is empty", out rejection);
            if (targetSlot == materialSlot && target.Count < 2)
                return Reject("same-slot upgrade requires at least two cards", out rejection);
            if (!TryResolveCard(target.ItemId, out var targetFile, out var targetRarity)
                || !TryResolveCard(material.ItemId, out _, out var materialRarity))
                return Reject("target or material is not a monster card", out rejection);

            var maximumUpgrade = ResolveMaximumUpgrade(targetFile);
            if (maximumUpgrade <= 0 || target.EnchantUpgradeCount >= maximumUpgrade)
                return Reject("target card is already at maximum upgrade", out rejection);

            var currentGold = inventory.GetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
            if (currentGold < _config.GoldCost)
                return Reject("insufficient gold", out rejection);

            // Reserve space for the success path before rolling, so a full inventory
            // cannot be used to reroll successful attempts without paying the cost.
            var successPreflight = InventoryCompoundPlanning.CloneInventory(inventory);
            if (!TryApplyAttempt(
                    successPreflight,
                    targetSlot,
                    materialSlot,
                    success: true,
                    currentGold,
                    out _))
                return Reject("inventory full", out rejection);

            var chance = _config.CalculateChance(
                targetRarity,
                materialRarity,
                target.ItemId == material.ItemId);
            var success = chance >= MonsterCardUpgradeConfig.ProbabilityDenominator
                || (chance > 0 && _next(MonsterCardUpgradeConfig.ProbabilityDenominator) < chance);

            var planning = InventoryCompoundPlanning.CloneInventory(inventory);
            if (!TryApplyAttempt(
                    planning,
                    targetSlot,
                    materialSlot,
                    success,
                    currentGold,
                    out var plannedResultSlot))
                return Reject("upgrade transaction planning failed", out rejection);

            var rollback = InventoryCompoundPlanning.CloneInventory(inventory);
            var actualResultSlot = (short)-1;
            if (!TryApplyAttempt(
                    inventory,
                    targetSlot,
                    materialSlot,
                    success,
                    currentGold,
                    out actualResultSlot)
                || actualResultSlot != plannedResultSlot)
            {
                RestoreSlot(inventory, rollback, targetSlot);
                RestoreSlot(inventory, rollback, materialSlot);
                RestoreSlot(inventory, rollback, plannedResultSlot);
                RestoreSlot(inventory, rollback, actualResultSlot);
                inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    currentGold);
                return Reject("upgrade transaction failed", out rejection);
            }

            result = new MonsterCardUpgradeResult
            {
                TargetSlot = targetSlot,
                TargetItemId = target.ItemId,
                MaterialSlot = materialSlot,
                ResultSlot = actualResultSlot,
                Success = success,
                UpgradeCount = success
                    ? (byte)(target.EnchantUpgradeCount + 1)
                    : target.EnchantUpgradeCount,
                Chance = chance,
                GoldCost = _config.GoldCost,
                UpdatedGold = currentGold - _config.GoldCost,
            };
            return true;
        }

        private bool TryApplyAttempt(
            InventoryService inventory,
            short targetSlot,
            short materialSlot,
            bool success,
            int currentGold,
            out short resultSlot)
        {
            resultSlot = targetSlot;
            if (!inventory.TryGetItem(InventoryListType.Main, targetSlot, out var target)
                || !inventory.TryGetItem(InventoryListType.Main, materialSlot, out var material))
                return false;

            var targetSnapshot = target.Copy();
            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    materialSlot,
                    material.ItemId,
                    1,
                    out var materialDelete)
                || !materialDelete.Success
                || materialDelete.DeletedCount != 1)
                return false;

            if (success)
            {
                var upgraded = targetSnapshot.Copy();
                upgraded.Count = 1;
                upgraded.EnchantUpgradeCount++;

                var remainingTarget = inventory.GetItem(InventoryListType.Main, targetSlot);
                if (remainingTarget == null)
                {
                    if (!inventory.SetItem(InventoryListType.Main, targetSlot, upgraded))
                        return false;
                }
                else if (remainingTarget.Count == 1)
                {
                    if (!inventory.SetItem(InventoryListType.Main, targetSlot, upgraded))
                        return false;
                }
                else
                {
                    if (!InventoryDeleteService.TryConsumeFromSlot(
                            inventory,
                            InventoryListType.Main,
                            targetSlot,
                            targetSnapshot.ItemId,
                            1,
                            out var targetDelete)
                        || !targetDelete.Success
                        || targetDelete.DeletedCount != 1
                        || !InventoryRewardGrantService.TryInsertExisting(
                            inventory,
                            upgraded,
                            1,
                            out var grant)
                        || !grant.Success)
                        return false;
                    resultSlot = grant.SlotIndex;
                }
            }

            return inventory.SetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                currentGold - _config.GoldCost);
        }

        private static void RestoreSlot(
            InventoryService destination,
            InventoryService source,
            short slot)
        {
            if (slot < InventoryService.MainSlotStart || slot > InventoryService.MainSlotEnd)
                return;

            var snapshot = source.GetItem(InventoryListType.Main, slot);
            if (snapshot == null)
                destination.RemoveItem(InventoryListType.Main, slot);
            else
                destination.SetItem(InventoryListType.Main, slot, snapshot.Copy());
        }

        private static int ResolveMaximumUpgrade(StackableItemFile card)
        {
            var maximum = -1;
            foreach (var index in card.EnchantTable)
                maximum = Math.Max(maximum, index);
            return maximum;
        }

        private static bool TryResolveCard(
            int itemId,
            out StackableItemFile card,
            out int rarity)
        {
            rarity = -1;
            if (!ItemMetadataResolver.TryLoadStackableFile(itemId, out card)
                || !ItemMetadataResolver.IsMonsterCard(card)
                || card.Rarity < 0 || card.Rarity > 3)
                return false;
            rarity = card.Rarity;
            return true;
        }

        private static bool Reject(string reason, out string rejection)
        {
            rejection = reason;
            return false;
        }
    }
}
