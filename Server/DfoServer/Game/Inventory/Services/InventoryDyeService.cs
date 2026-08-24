using System;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryDyeError
    {
        None = 0,
        InvalidInventory = 1,
        InvalidRequest = 2,
        DyeMissing = 3,
        DyeExpired = 4,
        NotDyeItem = 5,
        TargetMissing = 6,
        NotAvatar = 7,
        TargetDyeDisabled = 8,
        TargetExpired = 9,
        AvatarDetailMissing = 10,
        CooltimeActive = 11,
        InvalidLifecycle = 12,
        ConsumeFailed = 13,
    }

    internal sealed class InventoryDyeRequest
    {
        public short DyeSlotIndex { get; set; }

        public short AvatarSlotIndex { get; set; }
    }

    internal sealed class InventoryDyeResult
    {
        public bool Success { get; set; }

        public InventoryDyeError Error { get; set; }

        public InventoryDyeRequest Request { get; set; }

        public int DyeItemTemplateId { get; set; }

        public ushort DyeId { get; set; }

        public ushort Color1 { get; set; }

        public ushort Color2 { get; set; }

        public int AvatarItemTemplateId { get; set; }

        public int DyeRemainingCount { get; set; }

        public bool SourceExpiredDeleted { get; set; }

        public InventoryMutationSet Changes { get; } = new InventoryMutationSet();
    }

    internal static class InventoryDyeService
    {
        internal static bool TryUse(
            InventoryService inventory,
            InventoryDyeRequest request,
            long nowUnixSeconds,
            out InventoryDyeResult result)
        {
            return TryUse(
                inventory,
                request,
                nowUnixSeconds,
                LoadStackable,
                LoadEquipment,
                out result);
        }

        internal static bool TryUse(
            InventoryService inventory,
            InventoryDyeRequest request,
            long nowUnixSeconds,
            Func<int, StackableItemFile> stackableLoader,
            out InventoryDyeResult result)
        {
            return TryUse(
                inventory,
                request,
                nowUnixSeconds,
                stackableLoader,
                LoadEquipment,
                out result);
        }

        internal static bool TryUse(
            InventoryService inventory,
            InventoryDyeRequest request,
            long nowUnixSeconds,
            Func<int, StackableItemFile> stackableLoader,
            Func<int, EquipmentFile> equipmentLoader,
            out InventoryDyeResult result)
        {
            result = CreateResult(request);
            if (inventory == null)
                return Fail(result, InventoryDyeError.InvalidInventory);
            if (request == null
                || request.DyeSlotIndex < 0
                || request.AvatarSlotIndex < 0)
                return Fail(result, InventoryDyeError.InvalidRequest);

            if (!InventoryDeleteService.CanUseStackableForClient(
                    inventory,
                    InventoryListType.Main,
                    request.DyeSlotIndex,
                    0,
                    out var dyeItemTemplateId))
            {
                return Fail(result, InventoryDyeError.DyeMissing);
            }

            result.DyeItemTemplateId = dyeItemTemplateId;
            if (InventoryItemLifecycleService.TryRemoveExpiredSource(
                    inventory,
                    InventoryListType.Main,
                    request.DyeSlotIndex,
                    dyeItemTemplateId,
                    nowUnixSeconds,
                    out var expiredMutation))
            {
                result.SourceExpiredDeleted = true;
                result.Changes.AddSlot(InventoryListType.Main, request.DyeSlotIndex);
                return Fail(result, InventoryDyeError.DyeExpired);
            }

            var stackable = stackableLoader?.Invoke(dyeItemTemplateId);
            if (!TryResolveDyeId(stackable, out var dyeId))
                return Fail(result, InventoryDyeError.NotDyeItem);
            result.DyeId = dyeId;

            var lifecyclePlan = InventoryItemLifecycleService.PrepareUseWithDefinition(
                inventory,
                InventoryListType.Main,
                request.DyeSlotIndex,
                dyeItemTemplateId,
                nowUnixSeconds,
                1,
                stackable,
                checkEffectMaintenance: false,
                checkCooltimeMaintenance: true);
            if (!lifecyclePlan.Success)
            {
                ApplyLifecycleFailure(result, lifecyclePlan);
                return true;
            }

            var avatar = inventory.GetItem(InventoryListType.Avatar, request.AvatarSlotIndex);
            if (avatar == null || avatar.ItemId <= 0)
                return Fail(result, InventoryDyeError.TargetMissing);
            result.AvatarItemTemplateId = avatar.ItemId;
            if (avatar.ItemKind != ItemCore.KindAvatar)
                return Fail(result, InventoryDyeError.NotAvatar);

            var equipment = equipmentLoader?.Invoke(avatar.ItemId);
            if (equipment == null || !equipment.IsDyeEnabled)
                return Fail(result, InventoryDyeError.TargetDyeDisabled);

            var detail = inventory.AvatarDetails.GetDetail(avatar.AvatarUid);
            if (InventoryItemExpirationService.IsExpired(avatar, detail, nowUnixSeconds))
                return Fail(result, InventoryDyeError.TargetExpired);

            if (detail == null)
            {
                detail = inventory.AvatarDetails.CreateDetail(
                    avatar,
                    inventory.AccountId,
                    inventory.CharacterId,
                    persistImmediately: false);
                if (detail == null)
                    return Fail(result, InventoryDyeError.AvatarDetailMissing);
                inventory.MarkDirty(InventoryListType.Avatar, request.AvatarSlotIndex);
            }

            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    request.DyeSlotIndex,
                    dyeItemTemplateId,
                    1,
                    out var consumeResult)
                || consumeResult == null
                || !consumeResult.Success)
            {
                return Fail(result, InventoryDyeError.ConsumeFailed);
            }

            detail.Color1 = dyeId;
            result.Color1 = detail.Color1;
            result.Color2 = detail.Color2;
            inventory.AvatarDetails.MarkDirty(detail.AvatarUid);
            InventoryItemLifecycleService.ApplyUseSuccess(inventory, lifecyclePlan);

            result.Success = true;
            result.Error = InventoryDyeError.None;
            result.DyeRemainingCount = inventory.GetItem(
                InventoryListType.Main,
                request.DyeSlotIndex)?.Count ?? 0;
            result.Changes.AddRange(consumeResult.Changes);
            result.Changes.AddSlot(InventoryListType.Avatar, request.AvatarSlotIndex);
            return true;
        }

        private static StackableItemFile LoadStackable(int itemTemplateId)
        {
            return ItemMetadataResolver.TryLoadStackableFile(
                itemTemplateId,
                out var stackable)
                ? stackable
                : null;
        }

        private static EquipmentFile LoadEquipment(int itemTemplateId)
        {
            return ItemMetadataResolver.TryLoadEquipmentFile(
                itemTemplateId,
                out var equipment)
                ? equipment
                : null;
        }

        private static bool TryResolveDyeId(
            StackableItemFile stackable,
            out ushort dyeId)
        {
            dyeId = 0;
            if (stackable == null
                || !stackable.HasDyeInfo
                || stackable.DyeId <= 0
                || stackable.DyeId > ushort.MaxValue)
            {
                return false;
            }

            dyeId = (ushort)stackable.DyeId;
            return true;
        }

        private static void ApplyLifecycleFailure(
            InventoryDyeResult result,
            InventoryItemLifecycleUsePlan lifecyclePlan)
        {
            if (lifecyclePlan == null)
            {
                Fail(result, InventoryDyeError.InvalidLifecycle);
                return;
            }

            if (lifecyclePlan.SourceExpiredDeleted)
            {
                result.SourceExpiredDeleted = true;
                result.Changes.AddSlot(lifecyclePlan.ListType, lifecyclePlan.SlotIndex);
                Fail(result, InventoryDyeError.DyeExpired);
                return;
            }

            if (lifecyclePlan.Status == InventoryItemLifecycleStatus.CooltimeActive)
                Fail(result, InventoryDyeError.CooltimeActive);
            else if (lifecyclePlan.Status == InventoryItemLifecycleStatus.SourceMissing
                || lifecyclePlan.Status == InventoryItemLifecycleStatus.SourceChanged
                || lifecyclePlan.Status == InventoryItemLifecycleStatus.SourceEmpty)
                Fail(result, InventoryDyeError.DyeMissing);
            else
                Fail(result, InventoryDyeError.InvalidLifecycle);
        }

        private static InventoryDyeResult CreateResult(
            InventoryDyeRequest request)
        {
            return new InventoryDyeResult
            {
                Request = request ?? new InventoryDyeRequest(),
            };
        }

        private static bool Fail(
            InventoryDyeResult result,
            InventoryDyeError error)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            result.Success = false;
            result.Error = error;
            return true;
        }
    }
}
