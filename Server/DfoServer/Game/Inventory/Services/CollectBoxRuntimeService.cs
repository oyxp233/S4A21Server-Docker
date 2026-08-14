using System;

namespace DfoServer.Game.Inventory
{
    internal sealed class CollectBoxMutationResult
    {
        public bool Success { get; set; }

        public byte ErrorCode { get; set; } = 0x12;

        public int BoxIndex { get; set; }

        public int SlotIndex { get; set; }

        public int ItemId { get; set; }

        public InventoryMutationResult InventoryItem { get; set; }
    }

    internal static class CollectBoxRuntimeService
    {
        private const byte ErrorCodeItemMismatch = 0x12;

        internal static bool TryPutItem(
            InventoryService inventory,
            int boxIndex,
            int sourceSlotIndex,
            int itemId,
            out CollectBoxMutationResult result)
        {
            result = CreateResult(boxIndex, 0, itemId);
            if (inventory == null || itemId <= 0)
                return false;

            if (!TryResolveCollectBoxSlotIndex(boxIndex, itemId, out var collectBoxSlotIndex))
                return false;

            result.SlotIndex = collectBoxSlotIndex;
            if (inventory.CollectBox.GetItemId(boxIndex, collectBoxSlotIndex) > 0
                || inventory.CollectBox.HasItemInBox(boxIndex, itemId))
                return false;

            if (!TryFindMainSourceSlot(inventory, itemId, sourceSlotIndex, out var sourceSlot, out var source)
                || IsItemLocked(inventory, source))
                return false;

            var sourceBefore = source.Copy();
            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    sourceSlot,
                    1,
                    out var deleteResult)
                || !deleteResult.Success)
                return false;

            if (!inventory.CollectBox.SetItem(boxIndex, collectBoxSlotIndex, itemId))
            {
                inventory.SetItem(InventoryListType.Main, sourceSlot, sourceBefore);
                return false;
            }

            result.Success = true;
            result.InventoryItem = CreateDeleteMutation(sourceSlot, sourceBefore, deleteResult);
            return true;
        }

        internal static bool TryTakeItem(
            InventoryService inventory,
            int itemId,
            out CollectBoxMutationResult result)
        {
            result = CreateResult(0, 0, itemId);
            if (inventory == null || itemId <= 0)
                return false;

            if (!inventory.CollectBox.TryFindSlotByItem(itemId, out var boxIndex, out var slotIndex))
                return false;

            result.BoxIndex = boxIndex;
            result.SlotIndex = slotIndex;

            if (!InventoryRewardGrantService.TryCreateAndInsert(
                    inventory,
                    itemId,
                    ItemCreateReason.Unknown,
                    1,
                    out var grantResult)
                || grantResult == null
                || !grantResult.Success)
                return false;

            if (!inventory.CollectBox.ClearSlot(boxIndex, slotIndex))
            {
                RollbackGrantedItem(inventory, grantResult);
                return false;
            }

            result.Success = true;
            result.InventoryItem = CreateGrantMutation(inventory, grantResult);
            return true;
        }

        private static bool TryResolveCollectBoxSlotIndex(int boxIndex, int itemId, out int slotIndex)
        {
            slotIndex = 0;
            var entry = CollectBoxDataService.GetByIndex(boxIndex);
            if (entry == null || itemId <= 0)
                return false;

            for (var index = 0; index < entry.Slots.Count; index++)
            {
                if (entry.Slots[index].ItemId != itemId)
                    continue;

                slotIndex = index;
                return true;
            }

            return false;
        }

        private static bool TryFindMainSourceSlot(
            InventoryService inventory,
            int itemId,
            int sourceSlotIndex,
            out short slotIndex,
            out ItemCore source)
        {
            slotIndex = -1;
            source = null;

            if (sourceSlotIndex < short.MinValue || sourceSlotIndex > short.MaxValue)
                return false;

            var sourceSlot = (short)sourceSlotIndex;
            var item = inventory.GetItem(InventoryListType.Main, sourceSlot);
            if (!IsAvailableMainSource(item, itemId))
                return false;

            slotIndex = sourceSlot;
            source = item;
            return true;
        }

        private static bool IsAvailableMainSource(ItemCore item, int itemId)
        {
            return item != null
                && item.ItemId == itemId
                && (!InventoryStackRuleService.IsStackable(item) || item.Count > 0);
        }

        private static InventoryMutationResult CreateDeleteMutation(
            short slotIndex,
            ItemCore source,
            InventoryDeleteResult delete)
        {
            var stackable = InventoryStackRuleService.IsStackable(source);
            return new InventoryMutationResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = slotIndex,
                ItemTemplateId = source != null ? source.ItemId : 0,
                RemainingStackCount = delete != null ? delete.RemainingCount : 0,
                InstanceValue = stackable
                    ? (delete != null ? delete.RemainingCount : 0)
                    : (source != null ? source.InstanceValue : 0),
                Durability = source != null ? source.Durability : (ushort)0,
                RequestedCount = 1,
                AppliedCount = (short)Math.Min(short.MaxValue, delete != null ? delete.DeletedCount : 0),
            };
        }

        private static InventoryMutationResult CreateGrantMutation(
            InventoryService inventory,
            InventoryRewardGrantResult grant)
        {
            var core = grant.Core;
            if (grant.Kind == InventoryRewardGrantKind.InventoryItem)
                core = inventory.GetItem(grant.ListType, grant.SlotIndex) ?? core;

            var stackable = InventoryStackRuleService.IsStackable(core);
            return new InventoryMutationResult
            {
                ListType = grant.ListType,
                SlotIndex = grant.SlotIndex,
                ItemTemplateId = grant.ItemTemplateId,
                RemainingStackCount = grant.Kind == InventoryRewardGrantKind.MainVirtualCount
                    ? grant.FinalCount
                    : stackable
                        ? (core != null ? core.Count : grant.GrantedCount)
                        : grant.GrantedCount,
                InstanceValue = grant.Kind == InventoryRewardGrantKind.MainVirtualCount
                    ? grant.FinalCount
                    : stackable
                        ? (core != null ? core.Count : grant.GrantedCount)
                        : (core != null ? core.InstanceValue : grant.GrantedCount),
                Durability = core != null ? core.Durability : (ushort)0,
                RequestedCount = (short)Math.Min(short.MaxValue, Math.Max(0, grant.RequestedCount)),
                AppliedCount = (short)Math.Min(short.MaxValue, Math.Max(0, grant.GrantedCount)),
            };
        }

        private static void RollbackGrantedItem(InventoryService inventory, InventoryRewardGrantResult grant)
        {
            if (inventory == null || grant == null)
                return;

            if (grant.Kind == InventoryRewardGrantKind.InventoryItem && grant.SlotIndex >= 0)
                InventoryDeleteService.TryDecreaseStack(inventory, grant.ListType, grant.SlotIndex, grant.GrantedCount, out _);
            else if (grant.Kind == InventoryRewardGrantKind.MainVirtualCount && grant.SlotIndex >= 0)
            {
                var current = inventory.GetMainVirtualCount(grant.SlotIndex);
                var next = Math.Max(0, (current != null ? current.Count : 0) - grant.GrantedCount);
                inventory.SetMainVirtualCount(grant.SlotIndex, next);
            }
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

        private static CollectBoxMutationResult CreateResult(int boxIndex, int slotIndex, int itemId)
        {
            return new CollectBoxMutationResult
            {
                Success = false,
                ErrorCode = ErrorCodeItemMismatch,
                BoxIndex = boxIndex,
                SlotIndex = slotIndex,
                ItemId = itemId,
            };
        }
    }
}
