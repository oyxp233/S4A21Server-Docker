using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventorySortServiceResult
    {
        public bool Success { get; set; }

        public bool Mutated { get; set; }

        public InventoryListType ListType { get; set; }

        public byte Category { get; set; }

        public int AffectedSlotCount { get; set; }

        public InventoryMutationSet Changes { get; } = new InventoryMutationSet();
    }

    internal static class InventorySortService
    {
        internal static bool TrySort(
            InventoryService inventory,
            InventoryListType listType,
            byte category,
            out InventorySortServiceResult result)
        {
            result = new InventorySortServiceResult
            {
                ListType = listType,
                Category = category,
            };

            if (inventory == null || !IsSupportedSortListType(listType))
                return false;

            if (!TryGetSortRange(inventory, listType, category, out var range))
                return SucceedNoOp(result);

            var lockedSlots = new HashSet<short>();
            var original = new Dictionary<short, ItemCore>();
            var movableItems = new List<SortableItem>();

            for (var slot = range.Start; slot <= range.End; slot++)
            {
                var item = inventory.GetItem(listType, slot);
                if (item == null)
                    continue;

                var snapshot = item.Copy();
                original[(short)slot] = snapshot;

                if (IsSortLocked(listType, (short)slot, snapshot))
                {
                    lockedSlots.Add((short)slot);
                    continue;
                }

                movableItems.Add(new SortableItem((short)slot, snapshot));
            }

            movableItems.Sort(CompareSortableItems);

            var targetSlots = new List<short>();
            for (var slot = range.Start; slot <= range.End; slot++)
            {
                if (!lockedSlots.Contains((short)slot))
                    targetSlots.Add((short)slot);
            }

            var assigned = new Dictionary<short, ItemCore>();
            for (var index = 0; index < movableItems.Count && index < targetSlots.Count; index++)
                assigned[targetSlots[index]] = movableItems[index].Item.Copy();

            foreach (var slot in targetSlots)
            {
                assigned.TryGetValue(slot, out var next);
                original.TryGetValue(slot, out var previous);

                if (ItemsEqual(previous, next))
                    continue;

                if (next == null)
                {
                    if (previous != null && !inventory.RemoveItem(listType, slot))
                        return false;
                }
                else if (!inventory.SetItem(listType, slot, next))
                {
                    return false;
                }

                result.Changes.AddSlot(listType, slot);
            }

            result.Success = true;
            result.Mutated = result.Changes.HasChanges;
            result.AffectedSlotCount = result.Changes.Slots.Count;
            return true;
        }

        internal static bool TryGetSortRange(
            InventoryService inventory,
            InventoryListType listType,
            byte category,
            out ItemSlotRange range)
        {
            range = default;
            switch (listType)
            {
                case InventoryListType.Main:
                    return TryGetMainSortRange(inventory, category, out range);
                case InventoryListType.Avatar:
                    if (category != ItemCore.KindAvatar)
                        return false;
                    range = ItemSlotBoundService.GetAvatarOpenRange(inventory.GetListParam16(InventoryListType.Avatar));
                    return range.Count > 0;
                case InventoryListType.Pet:
                    return TryGetPetSortRange(category, out range);
                case InventoryListType.PersonalCargo:
                    if (category != 11)
                        return false;
                    range = ItemSlotBoundService.GetPersonalCargoOpenRange(inventory.GetListParam16(InventoryListType.PersonalCargo));
                    return range.Count > 0;
                case InventoryListType.AccountCargo:
                    range = ItemSlotBoundService.GetAccountCargoOpenRange(inventory.GetListParam16(InventoryListType.AccountCargo));
                    return range.Count > 0;
                default:
                    return false;
            }
        }

        private static bool TryGetMainSortRange(InventoryService inventory, byte category, out ItemSlotRange range)
        {
            range = default;
            byte itemKind;
            switch (category)
            {
                case ItemCore.KindEquipment:
                case ItemCore.KindConsumable:
                case ItemCore.KindMaterial:
                case ItemCore.KindQuest:
                case ItemCore.KindAvatarEmblem:
                case ItemCore.KindExpertJobMaterial:
                    itemKind = category;
                    break;
                default:
                    return false;
            }

            return ItemSlotBoundService.TryGetSlotRange(
                    itemKind,
                    inventory.GetListParam16(InventoryListType.Main),
                    out var listType,
                    out range)
                && listType == InventoryListType.Main
                && range.Count > 0;
        }

        private static bool TryGetPetSortRange(byte category, out ItemSlotRange range)
        {
            range = default;
            byte itemKind;
            switch (category)
            {
                case ItemCore.KindCreature:
                case ItemCore.KindCreatureEquipment:
                case ItemCore.KindCreatureConsumable:
                    itemKind = category;
                    break;
                default:
                    return false;
            }

            return ItemSlotBoundService.TryGetSlotRange(
                    itemKind,
                    ItemSlotBoundService.MainExpandStageFull,
                    out var listType,
                    out range)
                && listType == InventoryListType.Pet
                && range.Count > 0;
        }

        internal static bool IsSortLocked(InventoryListType listType, short slotIndex, ItemCore item)
        {
            return item != null
                && item.SortLockFlag == 1
                && InventoryLockService.CanApplySortItemLock(listType, slotIndex);
        }

        private static int CompareSortableItems(SortableItem left, SortableItem right)
        {
            return CompareItems(
                left.Item,
                left.OriginalSlot,
                right.Item,
                right.OriginalSlot);
        }

        internal static int CompareItems(
            ItemCore left,
            short leftOriginalSlot,
            ItemCore right,
            short rightOriginalSlot)
        {
            var result = left.ItemKind.CompareTo(right.ItemKind);
            if (result != 0)
                return result;

            result = left.ItemId.CompareTo(right.ItemId);
            if (result != 0)
                return result;

            return leftOriginalSlot.CompareTo(rightOriginalSlot);
        }

        private static bool ItemsEqual(ItemCore left, ItemCore right)
        {
            if (left == null || right == null)
                return left == null && right == null;

            var leftBytes = left.ToBytes();
            var rightBytes = right.ToBytes();
            if (leftBytes.Length != rightBytes.Length)
                return false;

            for (var index = 0; index < leftBytes.Length; index++)
            {
                if (leftBytes[index] != rightBytes[index])
                    return false;
            }

            return true;
        }

        private static bool IsSupportedSortListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet
                || listType == InventoryListType.AccountCargo;
        }

        private static bool SucceedNoOp(InventorySortServiceResult result)
        {
            result.Success = true;
            result.Mutated = false;
            return true;
        }

        private readonly struct SortableItem
        {
            public SortableItem(short originalSlot, ItemCore item)
            {
                OriginalSlot = originalSlot;
                Item = item;
            }

            public short OriginalSlot { get; }

            public ItemCore Item { get; }
        }
    }
}
