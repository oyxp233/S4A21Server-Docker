using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryAvatarCompoundError
    {
        None = 0,
        InvalidInventory = 1,
        InvalidRequest = 2,
        MissingAvatar = 3,
        AvatarLocked = 4,
        MissingConsumable = 5,
        ResolveFailed = 6,
        InvalidResultItem = 7,
        NoEmptyAvatarSlot = 8,
        DeleteFailed = 9,
        ConsumeFailed = 10,
        CreateFailed = 11,
        InsertFailed = 12,
        ItemMismatch = 13,
    }

    internal sealed class InventoryAvatarCompoundRequest
    {
        public short ConsumeSlot { get; set; }

        public short Slot1 { get; set; }

        public short Slot2 { get; set; }

        public int RequestedItemId { get; set; }

        public ushort AbilityNo { get; set; }
    }

    internal sealed class InventoryAvatarCompoundSetRequest
    {
        public short ConsumeSlot { get; set; }

        public short[] ConsumeSlots { get; set; }

        public int[] ExpectedItemIds { get; set; }

        public int RequestedItemId { get; set; }

        public ushort AbilityNo { get; set; }
    }

    internal sealed class InventoryAvatarCompoundResult
    {
        public bool Success { get; set; }

        public InventoryAvatarCompoundError Error { get; set; }

        public short ConsumeSlot { get; set; }

        public short Slot1 { get; set; }

        public short Slot2 { get; set; }

        public ushort AbilityNo { get; set; }

        public int OldItemId1 { get; set; }

        public int OldItemId2 { get; set; }

        public List<int> OldItemIds { get; } = new List<int>();

        public List<short> NewSlots { get; } = new List<short>();

        public List<int> NewItemIds { get; } = new List<int>();

        public int ConsumedItemTemplateId { get; set; }

        public int ConsumedItemRemainingCount { get; set; }

        public InventoryMutationSet Changes { get; } = new InventoryMutationSet();
    }

    internal static class InventoryAvatarCompoundService
    {
        internal static bool TryCompoundAvatar(
            InventoryService inventory,
            InventoryAvatarCompoundRequest request,
            Func<int, int, int, IReadOnlyList<int>> resolveNewItemIds,
            out InventoryAvatarCompoundResult result)
            => TryCompoundAvatar(
                inventory,
                request,
                resolveNewItemIds,
                null,
                out result);

        internal static bool TryCompoundAvatar(
            InventoryService inventory,
            InventoryAvatarCompoundRequest request,
            Func<int, int, int, IReadOnlyList<int>> resolveNewItemIds,
            Func<long> allocateAvatarUid,
            out InventoryAvatarCompoundResult result)
        {
            result = CreateResult(request);
            if (inventory == null)
                return Fail(result, InventoryAvatarCompoundError.InvalidInventory);
            if (request == null
                || request.Slot1 == request.Slot2
                || request.RequestedItemId <= 0
                || resolveNewItemIds == null)
                return Fail(result, InventoryAvatarCompoundError.InvalidRequest);

            if (!TryGetAvatarMaterial(inventory, request.Slot1, out var item1)
                || !TryGetAvatarMaterial(inventory, request.Slot2, out var item2))
                return Fail(result, InventoryAvatarCompoundError.MissingAvatar);
            if (IsLocked(inventory, item1) || IsLocked(inventory, item2))
                return Fail(result, InventoryAvatarCompoundError.AvatarLocked);
            if (!TryGetConsumable(inventory, request.ConsumeSlot, out var consumeItem))
                return Fail(result, InventoryAvatarCompoundError.MissingConsumable);

            var newItemIds = resolveNewItemIds(item1.ItemId, item2.ItemId, consumeItem.ItemId);
            if (!NormalizeNewAvatarItemIds(newItemIds, out var normalizedNewItemIds))
                return Fail(result, InventoryAvatarCompoundError.ResolveFailed);
            if (!ValidateNewAvatarItems(normalizedNewItemIds))
                return Fail(result, InventoryAvatarCompoundError.InvalidResultItem);

            var consumedSlots = new HashSet<short> { request.Slot1, request.Slot2 };
            if (!TryPlanAvatarInsertSlots(inventory, consumedSlots, normalizedNewItemIds.Count, out var newSlots))
                return Fail(result, InventoryAvatarCompoundError.NoEmptyAvatarSlot);
            if (!TryPrepareNewAvatars(
                    inventory,
                    normalizedNewItemIds,
                    newSlots,
                    request.AbilityNo,
                    allocateAvatarUid,
                    result,
                    out var preparedAvatars))
                return false;

            result.OldItemId1 = item1.ItemId;
            result.OldItemId2 = item2.ItemId;
            result.OldItemIds.Add(item1.ItemId);
            result.OldItemIds.Add(item2.ItemId);
            result.ConsumedItemTemplateId = consumeItem.ItemId;

            if (!RemoveAvatarSlot(inventory, request.Slot1, result)
                || !RemoveAvatarSlot(inventory, request.Slot2, result))
            {
                DetachPreparedAvatars(inventory, preparedAvatars);
                return Fail(result, InventoryAvatarCompoundError.DeleteFailed);
            }
            if (!ConsumeMainItem(inventory, request.ConsumeSlot, consumeItem.ItemId, result))
            {
                DetachPreparedAvatars(inventory, preparedAvatars);
                return Fail(result, InventoryAvatarCompoundError.ConsumeFailed);
            }
            if (!InsertPreparedAvatars(inventory, preparedAvatars, result))
                return false;

            result.Success = true;
            result.Error = InventoryAvatarCompoundError.None;
            return true;
        }

        internal static bool TryCompoundAvatarSet(
            InventoryService inventory,
            InventoryAvatarCompoundSetRequest request,
            Func<int, int> resolveNewItemId,
            out InventoryAvatarCompoundResult result)
            => TryCompoundAvatarSet(
                inventory,
                request,
                resolveNewItemId,
                null,
                out result);

        internal static bool TryCompoundAvatarSet(
            InventoryService inventory,
            InventoryAvatarCompoundSetRequest request,
            Func<int, int> resolveNewItemId,
            Func<long> allocateAvatarUid,
            out InventoryAvatarCompoundResult result)
        {
            result = CreateResult(request);
            if (inventory == null)
                return Fail(result, InventoryAvatarCompoundError.InvalidInventory);
            if (request == null
                || request.ConsumeSlots == null
                || request.ConsumeSlots.Length == 0
                || request.RequestedItemId <= 0
                || resolveNewItemId == null)
                return Fail(result, InventoryAvatarCompoundError.InvalidRequest);

            var uniqueSlots = new HashSet<short>();
            var consumedSlots = new HashSet<short>();
            var materials = new List<ItemCore>();
            for (var index = 0; index < request.ConsumeSlots.Length; index++)
            {
                var slot = request.ConsumeSlots[index];
                if (!uniqueSlots.Add(slot))
                    return Fail(result, InventoryAvatarCompoundError.InvalidRequest);
                if (!TryGetAvatarMaterial(inventory, slot, out var material))
                    return Fail(result, InventoryAvatarCompoundError.MissingAvatar);
                if (request.ExpectedItemIds != null
                    && index < request.ExpectedItemIds.Length
                    && request.ExpectedItemIds[index] > 0
                    && request.ExpectedItemIds[index] != material.ItemId)
                    return Fail(result, InventoryAvatarCompoundError.ItemMismatch);
                if (IsLocked(inventory, material))
                    return Fail(result, InventoryAvatarCompoundError.AvatarLocked);

                consumedSlots.Add(slot);
                materials.Add(material);
            }

            if (!TryGetConsumable(inventory, request.ConsumeSlot, out var consumeItem))
                return Fail(result, InventoryAvatarCompoundError.MissingConsumable);

            var newItemId = resolveNewItemId(consumeItem.ItemId);
            if (newItemId <= 0)
                return Fail(result, InventoryAvatarCompoundError.ResolveFailed);
            if (!ValidateNewAvatarItems(new[] { newItemId }))
                return Fail(result, InventoryAvatarCompoundError.InvalidResultItem);
            if (!TryPlanAvatarInsertSlots(inventory, consumedSlots, 1, out var newSlots))
                return Fail(result, InventoryAvatarCompoundError.NoEmptyAvatarSlot);
            if (!TryPrepareNewAvatars(
                    inventory,
                    new[] { newItemId },
                    newSlots,
                    request.AbilityNo,
                    allocateAvatarUid,
                    result,
                    out var preparedAvatars))
                return false;

            foreach (var material in materials)
                result.OldItemIds.Add(material.ItemId);
            result.ConsumedItemTemplateId = consumeItem.ItemId;

            for (var index = 0; index < request.ConsumeSlots.Length; index++)
            {
                if (!RemoveAvatarSlot(inventory, request.ConsumeSlots[index], result))
                {
                    DetachPreparedAvatars(inventory, preparedAvatars);
                    return Fail(result, InventoryAvatarCompoundError.DeleteFailed);
                }
            }

            if (!ConsumeMainItem(inventory, request.ConsumeSlot, consumeItem.ItemId, result))
            {
                DetachPreparedAvatars(inventory, preparedAvatars);
                return Fail(result, InventoryAvatarCompoundError.ConsumeFailed);
            }
            if (!InsertPreparedAvatars(inventory, preparedAvatars, result))
                return false;

            result.Success = true;
            result.Error = InventoryAvatarCompoundError.None;
            return true;
        }

        private static bool TryGetAvatarMaterial(InventoryService inventory, short slotIndex, out ItemCore item)
        {
            item = null;
            if (inventory == null)
                return false;

            item = inventory.GetItem(InventoryListType.Avatar, slotIndex);
            return item != null && item.ItemKind == ItemCore.KindAvatar && item.ItemId > 0;
        }

        private static bool TryGetConsumable(InventoryService inventory, short slotIndex, out ItemCore item)
        {
            item = null;
            if (inventory == null)
                return false;

            item = inventory.GetItem(InventoryListType.Main, slotIndex);
            return item != null
                && item.ItemId > 0
                && InventoryStackRuleService.IsStackable(item)
                && item.Count > 0;
        }

        private static bool NormalizeNewAvatarItemIds(
            IReadOnlyList<int> newItemIds,
            out List<int> normalizedNewItemIds)
        {
            normalizedNewItemIds = new List<int>();
            if (newItemIds == null || newItemIds.Count == 0)
                return false;

            for (var index = 0; index < newItemIds.Count; index++)
            {
                if (newItemIds[index] <= 0)
                    return false;

                normalizedNewItemIds.Add(newItemIds[index]);
            }

            return normalizedNewItemIds.Count > 0;
        }

        private static bool ValidateNewAvatarItems(IReadOnlyList<int> newItemIds)
        {
            if (newItemIds == null || newItemIds.Count == 0)
                return false;

            for (var index = 0; index < newItemIds.Count; index++)
            {
                if (!ItemMetadataResolver.TryResolveItemKind(newItemIds[index], out var itemKind)
                    || itemKind != ItemCore.KindAvatar)
                    return false;
            }

            return true;
        }

        private static bool TryPlanAvatarInsertSlots(
            InventoryService inventory,
            HashSet<short> removingSlots,
            int insertCount,
            out List<short> newSlots)
        {
            newSlots = new List<short>();
            if (inventory == null || insertCount <= 0)
                return false;

            var range = ItemSlotBoundService.GetAvatarOpenRange(inventory.GetListParam16(InventoryListType.Avatar));
            for (var slot = range.Start; slot <= range.End; slot++)
            {
                if (removingSlots != null && removingSlots.Contains(slot))
                {
                    newSlots.Add(slot);
                }
                else if (inventory.GetItem(InventoryListType.Avatar, slot) == null)
                {
                    newSlots.Add(slot);
                }

                if (newSlots.Count >= insertCount)
                    return true;
            }

            return false;
        }

        private static bool RemoveAvatarSlot(
            InventoryService inventory,
            short slotIndex,
            InventoryAvatarCompoundResult result)
        {
            if (!InventoryDeleteService.TryRemoveSlot(
                    inventory,
                    InventoryListType.Avatar,
                    slotIndex,
                    out var deleteResult)
                || !deleteResult.Success)
                return false;

            result.Changes.AddRange(deleteResult.Changes);
            return true;
        }

        private static bool ConsumeMainItem(
            InventoryService inventory,
            short slotIndex,
            int expectedItemId,
            InventoryAvatarCompoundResult result)
        {
            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    slotIndex,
                    expectedItemId,
                    1,
                    out var consumeResult)
                || !consumeResult.Success)
                return false;

            result.ConsumedItemRemainingCount = consumeResult.RemainingCount;
            result.Changes.AddRange(consumeResult.Changes);
            return true;
        }

        private sealed class PreparedAvatar
        {
            public short SlotIndex { get; set; }

            public int ItemId { get; set; }

            public ItemCore Core { get; set; }

            public InventoryCreateResult CreateResult { get; set; }
        }

        private static bool TryPrepareNewAvatars(
            InventoryService inventory,
            IReadOnlyList<int> newItemIds,
            IReadOnlyList<short> newSlots,
            ushort abilityNo,
            Func<long> allocateAvatarUid,
            InventoryAvatarCompoundResult result,
            out List<PreparedAvatar> preparedAvatars)
        {
            preparedAvatars = new List<PreparedAvatar>();
            for (var index = 0; index < newItemIds.Count; index++)
            {
                var itemId = newItemIds[index];
                var slotIndex = newSlots[index];
                var options = new InventoryCreateOptions
                {
                    AvatarAbilityNo = abilityNo,
                };
                var core = InventoryCreateService.CreateCore(
                    ItemCore.KindAvatar,
                    itemId,
                    ItemCreateReason.PackageOpen,
                    1,
                    options);
                if (allocateAvatarUid != null && core.AvatarUid <= 0)
                {
                    var avatarUid = allocateAvatarUid();
                    if (avatarUid <= 0 || avatarUid > int.MaxValue)
                    {
                        DetachPreparedAvatars(inventory, preparedAvatars);
                        return Fail(result, InventoryAvatarCompoundError.CreateFailed);
                    }

                    core.AvatarUid = (int)avatarUid;
                }

                if (!InventoryCreateService.TryCreateDetails(
                        inventory,
                        core,
                        ItemCreateReason.PackageOpen,
                        options,
                        out var createResult)
                    || createResult == null
                    || createResult.AvatarDetail == null)
                {
                    DetachPreparedAvatars(inventory, preparedAvatars);
                    return Fail(result, InventoryAvatarCompoundError.CreateFailed);
                }

                preparedAvatars.Add(new PreparedAvatar
                {
                    SlotIndex = slotIndex,
                    ItemId = itemId,
                    Core = core,
                    CreateResult = createResult,
                });
            }

            return true;
        }

        private static bool InsertPreparedAvatars(
            InventoryService inventory,
            IReadOnlyList<PreparedAvatar> preparedAvatars,
            InventoryAvatarCompoundResult result)
        {
            for (var index = 0; index < preparedAvatars.Count; index++)
            {
                var prepared = preparedAvatars[index];
                if (!inventory.SetItem(InventoryListType.Avatar, prepared.SlotIndex, prepared.Core))
                {
                    DetachPreparedAvatars(inventory, preparedAvatars);
                    return Fail(result, InventoryAvatarCompoundError.InsertFailed);
                }

                result.NewItemIds.Add(prepared.ItemId);
                result.NewSlots.Add(prepared.SlotIndex);
                result.Changes.AddSlot(InventoryListType.Avatar, prepared.SlotIndex);
            }

            return true;
        }

        private static void DetachPreparedAvatars(
            InventoryService inventory,
            IReadOnlyList<PreparedAvatar> preparedAvatars)
        {
            if (inventory == null || preparedAvatars == null)
                return;

            for (var index = 0; index < preparedAvatars.Count; index++)
                InventoryCreateService.DetachCreatedDetails(inventory, preparedAvatars[index].CreateResult);
        }

        private static bool IsLocked(InventoryService inventory, ItemCore item)
        {
            return inventory != null
                && item != null
                && item.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(item.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        private static InventoryAvatarCompoundResult CreateResult(InventoryAvatarCompoundRequest request)
        {
            return new InventoryAvatarCompoundResult
            {
                Error = InventoryAvatarCompoundError.None,
                ConsumeSlot = request != null ? request.ConsumeSlot : (short)-1,
                Slot1 = request != null ? request.Slot1 : (short)-1,
                Slot2 = request != null ? request.Slot2 : (short)-1,
                AbilityNo = request != null ? request.AbilityNo : (ushort)0,
            };
        }

        private static InventoryAvatarCompoundResult CreateResult(InventoryAvatarCompoundSetRequest request)
        {
            return new InventoryAvatarCompoundResult
            {
                Error = InventoryAvatarCompoundError.None,
                ConsumeSlot = request != null ? request.ConsumeSlot : (short)-1,
                Slot1 = request != null && request.ConsumeSlots != null && request.ConsumeSlots.Length > 0
                    ? request.ConsumeSlots[0]
                    : (short)-1,
                Slot2 = request != null && request.ConsumeSlots != null && request.ConsumeSlots.Length > 1
                    ? request.ConsumeSlots[1]
                    : (short)-1,
                AbilityNo = request != null ? request.AbilityNo : (ushort)0,
            };
        }

        private static bool Fail(InventoryAvatarCompoundResult result, InventoryAvatarCompoundError error)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            result.Success = false;
            result.Error = error;
            return false;
        }
    }
}
