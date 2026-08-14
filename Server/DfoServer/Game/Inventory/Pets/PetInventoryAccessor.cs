using System;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Inventory
{
    internal static class PetInventoryAccessor
    {
        internal static bool TryGetEquippedCreature(
            InventoryService inventory,
            out ItemCore core,
            out CreatureDetail detail)
        {
            core = null;
            detail = null;
            if (inventory == null)
                return false;

            core = inventory.GetItem(InventoryListType.Equipment, PetInventoryLayout.CreatureEquipSlot);
            if (core == null || core.ItemKind != ItemCore.KindCreature || core.Value <= 0)
            {
                core = null;
                return false;
            }

            detail = inventory.CreatureDetails.GetDetail(core.Value);
            return detail != null;
        }

        internal static int ResolveEquippedCreatureKey(InventoryService inventory)
        {
            return TryGetEquippedCreature(inventory, out var core, out _)
                ? core.Value
                : 0;
        }

        internal static int NextCreatureKey(InventoryService inventory)
        {
            if (inventory == null)
                return 1;

            var max = 0;
            foreach (var pair in inventory.GetItems(InventoryListType.Pet))
            {
                var core = pair.Value;
                if (core != null && core.ItemKind == ItemCore.KindCreature && core.Value > max)
                    max = core.Value;
            }

            var equipped = inventory.GetItem(InventoryListType.Equipment, PetInventoryLayout.CreatureEquipSlot);
            if (equipped != null && equipped.ItemKind == ItemCore.KindCreature && equipped.Value > max)
                max = equipped.Value;

            foreach (var detail in inventory.CreatureDetails.Details)
                if (detail != null && detail.Uid > max)
                    max = detail.Uid;

            return max >= int.MaxValue ? int.MaxValue : max + 1;
        }

        internal static int ResolveEquippedCreatureFoodConsumeRatePercent(InventoryService inventory)
        {
            if (inventory == null)
                return 0;

            var total = 0;
            foreach (var slot in PetInventoryLayout.ArtifactEquipSlots)
            {
                var item = inventory.GetItem(InventoryListType.Equipment, slot);
                if (item == null || item.ItemId <= 0)
                    continue;

                try
                {
                    if (ItemMetadataResolver.TryLoadEquipmentFile(item.ItemId, out var equipment)
                        && equipment != null)
                    {
                        total += equipment.CreatureFoodConsumeRate;
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[PetInventoryAccessor] food consume rate fallback item=0x{item.ItemId:X8}: {ex.Message}");
                }
            }

            return total;
        }

        internal static CreatureItemListSnapshot BuildCreatureItemListSnapshot(InventoryService inventory)
        {
            var snapshot = new CreatureItemListSnapshot();
            if (inventory == null)
                return snapshot;

            foreach (var detail in inventory.CreatureDetails.Details)
            {
                if (detail == null || detail.Uid <= 0)
                    continue;

                snapshot.Entries.Add(BuildCreatureItemEntry(detail));
            }

            return snapshot;
        }

        internal static bool TryBuildCreatureItemEntry(
            InventoryService inventory,
            int creatureKey,
            out CreatureItemEntrySnapshot entry)
        {
            entry = null;
            if (inventory == null || creatureKey <= 0)
                return false;

            var detail = inventory.CreatureDetails.GetDetail(creatureKey);
            if (detail == null)
                return false;

            entry = BuildCreatureItemEntry(detail);
            return true;
        }

        private static CreatureItemEntrySnapshot BuildCreatureItemEntry(CreatureDetail detail)
        {
            return new CreatureItemEntrySnapshot
            {
                CreatureKey = detail.Uid,
                Field04 = detail.Field04,
                ModeFlag = detail.ModeFlag,
                ProgressValue32 = detail.ProgressValue32,
                FieldAfterValue32 = ClampByte(detail.FieldAfterValue32),
                CreatureTextBytes = detail.NameBytes,
            };
        }

        private static byte ClampByte(int value)
        {
            if (value <= byte.MinValue)
                return byte.MinValue;
            if (value >= byte.MaxValue)
                return byte.MaxValue;
            return (byte)value;
        }
    }
}
