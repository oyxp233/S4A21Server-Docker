using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryRepairService
    {
        internal static bool TryRepairEquipment(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            bool quickRepair,
            bool freeRepair,
            out RepairEquipmentResult result)
        {
            result = null;
            if (inventory == null)
                return false;

            var gold = inventory.CountMainItem(0);
            FileLogger.Log($"[Repair] online cid={inventory.CharacterId} listType={listType} slot={slotIndex} quick={quickRepair} free={freeRepair} gold={gold}");

            if (slotIndex == -1)
                return TryRepairAll(inventory, gold, quickRepair, freeRepair, out result);

            if (listType != InventoryListType.Main
                && listType != InventoryListType.Equipment
                && listType != InventoryListType.PersonalCargo)
                return false;

            return TryRepairSingle(inventory, listType, slotIndex, gold, quickRepair, freeRepair, out result);
        }

        private static bool TryRepairSingle(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int currentGold,
            bool quickRepair,
            bool freeRepair,
            out RepairEquipmentResult result)
        {
            result = null;
            var item = inventory.GetItem(listType, slotIndex);
            if (item == null || !CanRepairItem(item, repairAll: false, out var maxDurability, out var repairPrice, out var grade))
                return false;

            if (item.Durability >= maxDurability)
            {
                result = new RepairEquipmentResult { SlotIndex = slotIndex, UpdatedGold = currentGold };
                return true;
            }

            var cost = freeRepair
                ? 0
                : EquipmentRepairPriceProvider.CalcRepairCost(
                    repairPrice,
                    grade,
                    maxDurability,
                    item.Durability,
                    item.EnchantUpgradeCount,
                    quickRepair);
            if (!TrySpendGold(inventory, cost, currentGold, out var updatedGold))
                return false;

            var repaired = item.Copy();
            repaired.Durability = (ushort)maxDurability;
            if (!inventory.SetItem(listType, slotIndex, repaired))
                return false;

            result = new RepairEquipmentResult
            {
                SlotIndex = slotIndex,
                UpdatedGold = updatedGold,
                Cost = cost,
            };
            return true;
        }

        private static bool TryRepairAll(
            InventoryService inventory,
            int currentGold,
            bool quickRepair,
            bool freeRepair,
            out RepairEquipmentResult result)
        {
            result = null;
            var candidates = new List<RepairCandidate>();
            var totalCost = 0;

            for (short slot = 11; slot <= 22; slot++)
                AddRepairAllCandidate(inventory, InventoryListType.Equipment, slot, quickRepair, freeRepair, candidates, ref totalCost);

            for (short slot = 3; slot <= 8; slot++)
                AddRepairAllCandidate(inventory, InventoryListType.Main, slot, quickRepair, freeRepair, candidates, ref totalCost);

            FileLogger.Log($"[Repair] online all cid={inventory.CharacterId} count={candidates.Count} totalCost={totalCost} gold={currentGold}");

            if (candidates.Count == 0)
            {
                result = new RepairEquipmentResult { SlotIndex = -1, UpdatedGold = currentGold, Cost = 0 };
                return true;
            }

            if (!TrySpendGold(inventory, totalCost, currentGold, out var updatedGold))
                return false;

            foreach (var candidate in candidates)
            {
                var item = inventory.GetItem(candidate.ListType, candidate.SlotIndex);
                if (item == null)
                    continue;

                var repaired = item.Copy();
                repaired.Durability = candidate.MaxDurability;
                inventory.SetItem(candidate.ListType, candidate.SlotIndex, repaired);
            }

            result = new RepairEquipmentResult
            {
                SlotIndex = -1,
                UpdatedGold = updatedGold,
                Cost = totalCost,
            };
            return true;
        }

        private static void AddRepairAllCandidate(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            bool quickRepair,
            bool freeRepair,
            List<RepairCandidate> candidates,
            ref int totalCost)
        {
            var item = inventory.GetItem(listType, slotIndex);
            if (item == null || !CanRepairItem(item, repairAll: true, out var maxDurability, out var repairPrice, out var grade))
                return;

            if (item.Durability >= maxDurability)
                return;

            var cost = freeRepair
                ? 0
                : EquipmentRepairPriceProvider.CalcRepairCost(
                    repairPrice,
                    grade,
                    maxDurability,
                    item.Durability,
                    item.EnchantUpgradeCount,
                    quickRepair);
            candidates.Add(new RepairCandidate
            {
                ListType = listType,
                SlotIndex = slotIndex,
                MaxDurability = (ushort)maxDurability,
            });
            totalCost += cost;
        }

        private static bool CanRepairItem(
            ItemCore item,
            bool repairAll,
            out int maxDurability,
            out int repairPrice,
            out int grade)
        {
            maxDurability = 0;
            repairPrice = 0;
            grade = 0;
            if (item == null || item.ItemId <= 0)
                return false;

            if (repairAll && !ItemMetadataResolver.IsRepairAllEligible(item.ItemId))
                return false;

            if (!ItemMetadataResolver.TryLoadEquipmentFile(item.ItemId, out var equipment)
                || equipment == null
                || equipment.Durability < 0)
                return false;

            maxDurability = equipment.Durability;
            repairPrice = equipment.RepairPrice;
            grade = equipment.Grade;
            return true;
        }

        private static bool TrySpendGold(
            InventoryService inventory,
            int cost,
            int currentGold,
            out int updatedGold)
        {
            updatedGold = currentGold;
            if (cost <= 0)
                return true;

            if (!inventory.TryConsumeMainItem(0, cost, out var consumeResult) || !consumeResult.Success)
                return false;

            updatedGold = consumeResult.RemainingCount;
            return true;
        }

        private sealed class RepairCandidate
        {
            public InventoryListType ListType { get; set; }
            public short SlotIndex { get; set; }
            public ushort MaxDurability { get; set; }
        }
    }
}
