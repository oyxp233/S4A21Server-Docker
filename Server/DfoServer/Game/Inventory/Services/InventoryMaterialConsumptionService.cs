using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryMaterialRequirement
    {
        internal InventoryMaterialRequirement(int itemTemplateId, int count)
        {
            ItemTemplateId = itemTemplateId;
            Count = count;
        }

        internal int ItemTemplateId { get; }

        internal int Count { get; }
    }

    internal sealed class InventoryMaterialConsumptionEntry
    {
        internal short SlotIndex { get; set; }

        internal int ItemTemplateId { get; set; }

        internal int Count { get; set; }
    }

    internal static class InventoryMaterialConsumptionService
    {
        internal static bool HasEnough(
            InventoryService inventory,
            IReadOnlyList<InventoryMaterialRequirement> requirements)
        {
            if (inventory == null
                || !TryNormalizeRequirements(requirements, out var normalized))
                return false;

            foreach (var requirement in normalized)
            {
                if (inventory.CountMainItem(requirement.ItemTemplateId) < requirement.Count)
                    return false;
            }

            return true;
        }

        internal static bool TryConsume(
            InventoryService inventory,
            IReadOnlyList<InventoryMaterialRequirement> requirements,
            ICollection<InventoryMaterialConsumptionEntry> consumed)
        {
            if (inventory == null
                || !TryNormalizeRequirements(requirements, out var normalized)
                || !HasEnoughNormalized(inventory, normalized))
                return false;

            foreach (var requirement in normalized)
            {
                if (InventoryService.TryResolveMainVirtualSlotByItemId(
                        requirement.ItemTemplateId,
                        out var virtualSlot,
                        out _))
                {
                    if (!inventory.TryConsumeMainItem(
                            requirement.ItemTemplateId,
                            requirement.Count,
                            out var virtualConsume)
                        || !virtualConsume.Success)
                    {
                        return false;
                    }

                    consumed?.Add(new InventoryMaterialConsumptionEntry
                    {
                        SlotIndex = virtualSlot,
                        ItemTemplateId = requirement.ItemTemplateId,
                        Count = virtualConsume.ConsumedCount,
                    });
                    continue;
                }

                var remaining = requirement.Count;
                foreach (var pair in inventory.GetItems(InventoryListType.Main)
                             .Where(candidate => candidate.Value.ItemId == requirement.ItemTemplateId)
                             .OrderBy(candidate => candidate.Key))
                {
                    if (remaining <= 0)
                        break;

                    var item = pair.Value;
                    var available = InventoryStackRuleService.IsStackable(item)
                        ? Math.Max(0, item.Count)
                        : 1;
                    var remove = Math.Min(remaining, available);
                    if (remove <= 0)
                        continue;

                    if (!InventoryDeleteService.TryConsumeFromSlot(
                            inventory,
                            InventoryListType.Main,
                            pair.Key,
                            item.ItemId,
                            remove,
                            out var delete)
                        || !delete.Success)
                    {
                        return false;
                    }

                    consumed?.Add(new InventoryMaterialConsumptionEntry
                    {
                        SlotIndex = pair.Key,
                        ItemTemplateId = item.ItemId,
                        Count = delete.DeletedCount,
                    });
                    remaining -= delete.DeletedCount;
                }

                if (remaining > 0)
                    return false;
            }

            return true;
        }

        private static bool HasEnoughNormalized(
            InventoryService inventory,
            IReadOnlyList<InventoryMaterialRequirement> requirements)
        {
            foreach (var requirement in requirements)
            {
                if (inventory.CountMainItem(requirement.ItemTemplateId) < requirement.Count)
                    return false;
            }
            return true;
        }

        private static bool TryNormalizeRequirements(
            IReadOnlyList<InventoryMaterialRequirement> requirements,
            out List<InventoryMaterialRequirement> normalized)
        {
            normalized = new List<InventoryMaterialRequirement>();
            if (requirements == null)
                return false;

            var totals = new Dictionary<int, long>();
            foreach (var requirement in requirements)
            {
                if (requirement == null
                    || requirement.ItemTemplateId <= 0
                    || requirement.Count <= 0)
                {
                    return false;
                }

                var total = (totals.TryGetValue(requirement.ItemTemplateId, out var current)
                    ? current
                    : 0L) + requirement.Count;
                if (total > int.MaxValue)
                    return false;
                totals[requirement.ItemTemplateId] = total;
            }

            normalized.AddRange(totals
                .OrderBy(pair => pair.Key)
                .Select(pair => new InventoryMaterialRequirement(pair.Key, (int)pair.Value)));
            return true;
        }
    }
}
