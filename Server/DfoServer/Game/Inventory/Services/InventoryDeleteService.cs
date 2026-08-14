using System;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryDeleteError
    {
        None = 0,
        InvalidInventory = 1,
        InvalidCount = 2,
        SourceNotFound = 3,
        ItemMismatch = 4,
        NotEnoughCount = 5,
        RemoveFailed = 6,
        UpdateFailed = 7,
        ItemLocked = 8,
        UnsupportedListType = 9,
    }

    internal sealed class InventoryDeleteResult
    {
        public bool Success { get; set; }

        public InventoryDeleteError Error { get; set; }

        public int DeletedCount { get; set; }

        public int RemainingCount { get; set; }

        public ItemCore SourceSnapshot { get; set; }

        public InventoryMutationSet Changes { get; } = new InventoryMutationSet();
    }

    internal static class InventoryDeleteService
    {
        internal static bool TryRemoveSlot(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            out InventoryDeleteResult result)
        {
            result = CreateResult();
            if (inventory == null)
                return Fail(result, InventoryDeleteError.InvalidInventory);

            if (!inventory.TryGetItem(listType, slotIndex, out var source) || source == null)
                return Fail(result, InventoryDeleteError.SourceNotFound);

            var sourceSnapshot = source.Copy();

            if (!inventory.RemoveItem(listType, slotIndex))
                return Fail(result, InventoryDeleteError.RemoveFailed);

            RemoveOwnedDetail(inventory, sourceSnapshot);
            result.Success = true;
            result.DeletedCount = 1;
            result.RemainingCount = 0;
            result.SourceSnapshot = sourceSnapshot;
            result.Changes.AddSlot(listType, slotIndex);
            return true;
        }

        internal static bool TryDecreaseStack(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int count,
            out InventoryDeleteResult result)
        {
            result = CreateResult();
            if (inventory == null)
                return Fail(result, InventoryDeleteError.InvalidInventory);

            if (count <= 0)
                return Fail(result, InventoryDeleteError.InvalidCount);

            if (!inventory.TryGetItem(listType, slotIndex, out var source) || source == null)
                return Fail(result, InventoryDeleteError.SourceNotFound);

            if (!InventoryStackRuleService.IsStackable(source))
                return TryRemoveSlot(inventory, listType, slotIndex, out result);

            if (source.Count < count)
                return Fail(result, InventoryDeleteError.NotEnoughCount);

            var sourceSnapshot = source.Copy();

            if (source.Count == count)
            {
                if (!inventory.RemoveItem(listType, slotIndex))
                    return Fail(result, InventoryDeleteError.RemoveFailed);

                RemoveOwnedDetail(inventory, sourceSnapshot);
                result.Success = true;
                result.DeletedCount = count;
                result.RemainingCount = 0;
                result.SourceSnapshot = sourceSnapshot;
                result.Changes.AddSlot(listType, slotIndex);
                return true;
            }

            var updated = source.Copy();
            updated.Count -= count;
            if (!inventory.SetItem(listType, slotIndex, updated))
                return Fail(result, InventoryDeleteError.UpdateFailed);

            result.Success = true;
            result.DeletedCount = count;
            result.RemainingCount = updated.Count;
            result.SourceSnapshot = sourceSnapshot;
            result.Changes.AddSlot(listType, slotIndex);
            return true;
        }

        internal static bool TryConsumeFromSlot(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemId,
            int count,
            out InventoryDeleteResult result)
        {
            result = CreateResult();
            if (inventory == null)
                return Fail(result, InventoryDeleteError.InvalidInventory);

            if (!inventory.TryGetItem(listType, slotIndex, out var source) || source == null)
                return Fail(result, InventoryDeleteError.SourceNotFound);

            if (source.ItemId != expectedItemId)
                return Fail(result, InventoryDeleteError.ItemMismatch);

            return TryDecreaseStack(inventory, listType, slotIndex, count, out result);
        }

        internal static bool TryDeleteMainItemsByTemplateId(
            InventoryService inventory,
            int itemId,
            int count,
            out InventoryMutationSet changes)
        {
            changes = new InventoryMutationSet();
            if (inventory == null || itemId <= 0 || count <= 0)
                return false;
            if (inventory.CountMainItem(itemId) < count)
                return false;

            var remaining = count;
            foreach (var pair in inventory.GetItems(InventoryListType.Main))
            {
                if (remaining <= 0)
                    break;

                var item = pair.Value;
                if (item == null || item.ItemId != itemId)
                    continue;

                var available = InventoryStackRuleService.IsStackable(item)
                    ? Math.Max(0, item.Count)
                    : 1;
                var deleteCount = Math.Min(remaining, available);
                if (deleteCount <= 0
                    || !TryDecreaseStack(
                        inventory,
                        InventoryListType.Main,
                        pair.Key,
                        deleteCount,
                        out var deleted)
                    || !deleted.Success)
                {
                    return false;
                }

                changes.AddRange(deleted.Changes);
                remaining -= deleted.DeletedCount;
            }

            return remaining == 0;
        }

        internal static bool TryDeleteForClient(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int requestedCount,
            out InventoryMutationResult mutation)
        {
            return TryDeleteForClient(inventory, listType, slotIndex, requestedCount, 0, out mutation);
        }

        internal static bool TryUseStackableForClient(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemId,
            out InventoryMutationResult mutation)
        {
            return TryDeleteForClient(inventory, listType, slotIndex, 1, expectedItemId, out mutation);
        }

        internal static bool CanUseStackableForClient(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemId,
            out int resolvedItemId)
        {
            resolvedItemId = 0;
            if (inventory == null)
                return false;

            if (listType == InventoryListType.Main
                && InventoryService.IsVirtualMainSlot(slotIndex))
            {
                if (!InventoryService.TryResolveMainVirtualItemId(
                        slotIndex,
                        out resolvedItemId)
                    || (expectedItemId > 0
                        && expectedItemId != resolvedItemId))
                {
                    return false;
                }

                return (inventory.GetMainVirtualCount(slotIndex)?.Count ?? 0) > 0;
            }

            if (!IsSupportedClientDeleteListType(listType))
                return false;

            var source = inventory.GetItem(listType, slotIndex);
            if (source == null
                || source.IsEmpty
                || source.Count <= 0
                || !InventoryStackRuleService.IsStackable(source)
                || (expectedItemId > 0 && source.ItemId != expectedItemId)
                || IsItemLocked(inventory, source))
            {
                return false;
            }

            resolvedItemId = source.ItemId;
            return true;
        }

        internal static bool CanDeleteForClient(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int requestedCount)
        {
            if (inventory == null)
                return false;

            if (listType == InventoryListType.Main
                && InventoryService.IsVirtualMainSlot(slotIndex))
            {
                if (!InventoryService.TryResolveMainVirtualItemId(
                        slotIndex,
                        out _)
                    || requestedCount <= 0)
                {
                    return false;
                }

                return (inventory.GetMainVirtualCount(slotIndex)?.Count ?? 0)
                    >= requestedCount;
            }

            if (!IsSupportedClientDeleteListType(listType))
                return false;

            var source = inventory.GetItem(listType, slotIndex);
            return source != null
                && !IsItemLocked(inventory, source)
                && NormalizeClientDeleteCount(source, requestedCount) > 0;
        }

        private static bool TryDeleteForClient(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int requestedCount,
            int expectedItemId,
            out InventoryMutationResult mutation)
        {
            mutation = null;
            if (inventory == null)
                return false;

            if (listType == InventoryListType.Main && InventoryService.IsVirtualMainSlot(slotIndex))
                return TryDeleteMainVirtualForClient(inventory, slotIndex, requestedCount, expectedItemId, out mutation);

            if (!IsSupportedClientDeleteListType(listType))
                return false;

            var source = inventory.GetItem(listType, slotIndex);
            if (source == null)
                return false;

            if (expectedItemId > 0 && source.ItemId != expectedItemId)
                return false;

            if (IsItemLocked(inventory, source))
                return false;

            var deleteCount = NormalizeClientDeleteCount(source, requestedCount);
            if (deleteCount <= 0)
                return false;

            if (!TryDecreaseStack(inventory, listType, slotIndex, deleteCount, out var deleteResult)
                || !deleteResult.Success)
                return false;

            mutation = CreateMutation(
                listType,
                slotIndex,
                deleteResult.SourceSnapshot,
                requestedCount,
                deleteResult);
            return true;
        }

        private static bool TryDeleteMainVirtualForClient(
            InventoryService inventory,
            short slotIndex,
            int requestedCount,
            int expectedItemId,
            out InventoryMutationResult mutation)
        {
            mutation = null;
            if (!InventoryService.TryResolveMainVirtualItemId(slotIndex, out var itemId))
                return false;
            if (expectedItemId > 0 && expectedItemId != itemId)
                return false;
            if (requestedCount <= 0)
                return false;

            var current = inventory.GetMainVirtualCount(slotIndex);
            if (current == null || current.Count < requestedCount)
                return false;

            var remaining = current.Count - requestedCount;
            if (!inventory.SetMainVirtualCount(slotIndex, itemId, remaining))
                return false;

            mutation = new InventoryMutationResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = slotIndex,
                ItemTemplateId = itemId,
                RemainingStackCount = remaining,
                InstanceValue = remaining,
                RequestedCount = (short)Math.Min(short.MaxValue, requestedCount),
                AppliedCount = (short)Math.Min(short.MaxValue, requestedCount),
            };
            return true;
        }

        private static int NormalizeClientDeleteCount(ItemCore source, int requestedCount)
        {
            if (source == null)
                return 0;

            if (!InventoryStackRuleService.IsStackable(source))
                return 1;

            var currentCount = Math.Max(0, source.Count);
            if (requestedCount <= 0 || requestedCount >= currentCount)
                return currentCount;

            return requestedCount;
        }

        private static bool IsSupportedClientDeleteListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet;
        }

        private static bool IsItemLocked(InventoryService inventory, ItemCore core)
        {
            return inventory != null
                && core != null
                && core.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        private static InventoryMutationResult CreateMutation(
            InventoryListType listType,
            short slotIndex,
            ItemCore source,
            int requestedCount,
            InventoryDeleteResult delete)
        {
            var stackable = source != null && InventoryStackRuleService.IsStackable(source);
            return new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = source != null ? source.ItemId : 0,
                RemainingStackCount = delete != null ? delete.RemainingCount : 0,
                InstanceValue = stackable
                    ? (delete != null ? delete.RemainingCount : 0)
                    : (source != null ? source.InstanceValue : 0),
                Durability = source != null ? source.Durability : (ushort)0,
                RequestedCount = (short)Math.Min(short.MaxValue, Math.Max(0, requestedCount)),
                AppliedCount = (short)Math.Min(short.MaxValue, delete != null ? delete.DeletedCount : 0),
            };
        }

        private static void RemoveOwnedDetail(InventoryService inventory, ItemCore source)
        {
            if (inventory == null || source == null)
                return;

            if (source.ItemKind == ItemCore.KindAvatar && source.AvatarUid > 0)
                inventory.AvatarDetails.RemoveDirty(source.AvatarUid);
            else if (source.ItemKind == ItemCore.KindCreature && source.CreatureUid > 0)
                inventory.CreatureDetails.Remove(source.CreatureUid);
        }

        private static InventoryDeleteResult CreateResult()
        {
            return new InventoryDeleteResult
            {
                Success = false,
                Error = InventoryDeleteError.None,
            };
        }

        private static bool Fail(InventoryDeleteResult result, InventoryDeleteError error)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            result.Success = false;
            result.Error = error;
            return false;
        }
    }
}
