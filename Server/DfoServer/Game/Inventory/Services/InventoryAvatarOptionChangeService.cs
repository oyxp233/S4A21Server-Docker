using System;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryAvatarOptionChangeError
    {
        None = 0,
        InvalidInventory = 1,
        InvalidRequest = 2,
        SourceNotFound = 3,
        SourceItemMismatch = 4,
        TargetNotFound = 5,
        TargetItemMismatch = 6,
        NotAvatar = 7,
        NotOptionChangeBox = 8,
        AvatarMetadataMissing = 9,
        AvatarGradeMismatch = 10,
        ConsumeFailed = 11,
        UpdateFailed = 12,
    }

    internal sealed class InventoryAvatarOptionChangeRequest
    {
        public short SourceSlotIndex { get; set; }
        public int SourceItemId { get; set; }
        public short TargetSlotIndex { get; set; }
        public int TargetItemId { get; set; }
        public ushort AbilityNo { get; set; }
    }

    internal sealed class InventoryAvatarOptionChangeResult
    {
        public bool Success { get; set; }
        public InventoryAvatarOptionChangeError Error { get; set; }
        public int SourceItemId { get; set; }
        public int SourceRemainingCount { get; set; }
        public int TargetItemId { get; set; }
        public ushort AbilityNo { get; set; }
        public InventoryMutationSet Changes { get; } = new InventoryMutationSet();
    }

    internal static class InventoryAvatarOptionChangeService
    {
        internal static bool TryChange(
            InventoryService inventory,
            InventoryAvatarOptionChangeRequest request,
            out InventoryAvatarOptionChangeResult result)
        {
            result = CreateResult(request);
            if (inventory == null)
                return Fail(result, InventoryAvatarOptionChangeError.InvalidInventory);
            if (request == null
                || request.SourceSlotIndex < 0
                || request.TargetSlotIndex < 0
                || request.SourceItemId <= 0
                || request.TargetItemId <= 0)
                return Fail(result, InventoryAvatarOptionChangeError.InvalidRequest);

            var source = inventory.GetItem(InventoryListType.Main, request.SourceSlotIndex);
            if (source == null || source.ItemId <= 0)
                return Fail(result, InventoryAvatarOptionChangeError.SourceNotFound);
            if (source.ItemId != request.SourceItemId)
                return Fail(result, InventoryAvatarOptionChangeError.SourceItemMismatch);
            if (!InventoryStackRuleService.IsStackable(source) || source.Count < 1)
                return Fail(result, InventoryAvatarOptionChangeError.SourceNotFound);

            if (!ItemMetadataResolver.TryLoadStackableFile(source.ItemId, out var stackable)
                || stackable == null
                || !stackable.HasAvatarSelectAbilityChange)
                return Fail(result, InventoryAvatarOptionChangeError.NotOptionChangeBox);

            var target = inventory.GetItem(InventoryListType.Avatar, request.TargetSlotIndex);
            if (target == null || target.ItemId <= 0)
                return Fail(result, InventoryAvatarOptionChangeError.TargetNotFound);
            if (target.ItemId != request.TargetItemId)
                return Fail(result, InventoryAvatarOptionChangeError.TargetItemMismatch);
            if (target.ItemKind != ItemCore.KindAvatar)
                return Fail(result, InventoryAvatarOptionChangeError.NotAvatar);

            if (!ItemMetadataResolver.TryLoadEquipmentFile(target.ItemId, out var equipment)
                || equipment == null)
                return Fail(result, InventoryAvatarOptionChangeError.AvatarMetadataMissing);
            if (!CanChangeAvatarGrade(stackable, equipment.Grade))
                return Fail(result, InventoryAvatarOptionChangeError.AvatarGradeMismatch);

            var sourceSnapshot = source.Copy();
            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    request.SourceSlotIndex,
                    request.SourceItemId,
                    1,
                    out var consumeResult)
                || consumeResult == null
                || !consumeResult.Success)
                return Fail(result, InventoryAvatarOptionChangeError.ConsumeFailed);

            var updatedTarget = target.Copy();
            updatedTarget.AbilityNo = request.AbilityNo;
            if (!inventory.SetItem(InventoryListType.Avatar, request.TargetSlotIndex, updatedTarget))
            {
                inventory.SetItem(InventoryListType.Main, request.SourceSlotIndex, sourceSnapshot);
                return Fail(result, InventoryAvatarOptionChangeError.UpdateFailed);
            }

            result.Success = true;
            result.Error = InventoryAvatarOptionChangeError.None;
            result.SourceItemId = source.ItemId;
            result.TargetItemId = target.ItemId;
            result.AbilityNo = request.AbilityNo;
            result.SourceRemainingCount = inventory.GetItem(InventoryListType.Main, request.SourceSlotIndex)?.Count ?? 0;
            result.Changes.AddRange(consumeResult.Changes);
            result.Changes.AddSlot(InventoryListType.Avatar, request.TargetSlotIndex);
            return true;
        }

        private static bool CanChangeAvatarGrade(StackableItemFile stackable, int avatarGrade)
        {
            if (stackable == null || stackable.AvatarSelectAbilityChanges == null)
                return false;

            foreach (var entry in stackable.AvatarSelectAbilityChanges)
            {
                if (entry != null && entry.AvatarGrade == avatarGrade)
                    return true;
            }

            return false;
        }

        private static InventoryAvatarOptionChangeResult CreateResult(InventoryAvatarOptionChangeRequest request)
        {
            return new InventoryAvatarOptionChangeResult
            {
                SourceItemId = request != null ? request.SourceItemId : 0,
                TargetItemId = request != null ? request.TargetItemId : 0,
                AbilityNo = request != null ? request.AbilityNo : (ushort)0,
            };
        }

        private static bool Fail(
            InventoryAvatarOptionChangeResult result,
            InventoryAvatarOptionChangeError error)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            result.Success = false;
            result.Error = error;
            return false;
        }
    }
}
