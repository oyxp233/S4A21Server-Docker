using DfoServer.Game.ItemUpgrade;
using System;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryDurabilityService
    {
        internal const ushort DefaultDecreaseAmount = 1;

        internal static bool TryDecreaseEquippedDurability(
            InventoryService inventory,
            short slotIndex,
            out EquipmentDurabilityDecreaseResult result)
        {
            result = EquipmentDurabilityDecreaseResult.Noop(slotIndex, "invalid_inventory");
            if (inventory == null)
                return false;

            if (!IsSupportedDurabilitySlot(slotIndex))
            {
                result = EquipmentDurabilityDecreaseResult.Noop(slotIndex, "unsupported_slot");
                return true;
            }

            var item = inventory.GetItem(InventoryListType.Equipment, slotIndex);
            if (item == null || item.ItemId <= 0)
            {
                result = EquipmentDurabilityDecreaseResult.Noop(slotIndex, "empty_slot");
                return true;
            }

            if (item.ItemKind != ItemCore.KindEquipment)
            {
                result = EquipmentDurabilityDecreaseResult.Noop(slotIndex, "not_equipment");
                return true;
            }

            if (item.Durability == 0)
            {
                result = EquipmentDurabilityDecreaseResult.Noop(slotIndex, "zero_durability");
                result.ItemTemplateId = item.ItemId;
                result.CurrentDurability = 0;
                return true;
            }

            var updated = item.Copy();
            updated.Durability = (ushort)Math.Max(0, item.Durability - DefaultDecreaseAmount);
            if (!inventory.SetItem(InventoryListType.Equipment, slotIndex, updated))
                return false;

            result = new EquipmentDurabilityDecreaseResult
            {
                SlotIndex = slotIndex,
                ItemTemplateId = item.ItemId,
                PreviousDurability = item.Durability,
                CurrentDurability = updated.Durability,
                Changed = true,
                Reason = "decreased",
            };
            return true;
        }

        internal static bool IsSupportedDurabilitySlot(short slotIndex)
        {
            var type = (EquipmentType)slotIndex;
            return EquipmentTypeInfo.IsWeapon(type)
                || EquipmentTypeInfo.IsArmor(type)
                || type == EquipmentType.SupportWeapon
                || type == EquipmentType.Charm;
        }
    }
}
