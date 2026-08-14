using System;
using DfoServer.Game.Currency;
using DfoServer.Game.ItemUpgrade;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryInsertMode
    {
        Default = 0,
        PersonalCargo = 1,
        AccountCargo = 2,
    }

    internal enum InventoryInsertError
    {
        None = 0,
        InvalidInventory = 1,
        InvalidItem = 2,
        InvalidCount = 3,
        InvalidTargetList = 4,
        InvalidTargetSlot = 5,
        StaticItemNotFound = 6,
        ItemExpired = 7,
        ItemKindRejected = 8,
        VirtualItemRejected = 9,
        AttachTypeRejected = 10,
        TradeRestrictionRejected = 11,
        ItemLockRejected = 12,
        SlotKindMismatch = 13,
        SlotOccupied = 14,
        CannotStack = 15,
        NoEmptySlot = 16,
        UpdateFailed = 17,
        ImpossibleContentRejected = 18,
    }

    internal sealed class InventoryInsertPlan
    {
        public bool Success { get; set; }

        public InventoryInsertError Error { get; set; }

        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int InsertedCount { get; set; }

        public int RemainingCount { get; set; }

        public bool MergeIntoExistingSlot { get; set; }
    }

    internal sealed class InventoryInsertResult
    {
        public bool Success { get; set; }

        public InventoryInsertError Error { get; set; }

        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int InsertedCount { get; set; }

        public int RemainingCount { get; set; }

        public InventoryMutationSet Changes { get; } = new InventoryMutationSet();
    }

    internal static class InventoryInsertService
    {
        private static readonly ItemSlotRange QuickSlotRange = new ItemSlotRange(3, 8);

        internal static bool TryPlanInsertByDefaultRule(
            InventoryService inventory,
            ItemCore item,
            int count,
            out InventoryInsertPlan plan)
        {
            plan = CreatePlan();
            if (!TryValidateInsertBase(inventory, item, count, out _, plan))
                return false;

            if (!ItemSlotBoundService.TryGetSlotRange(
                    item.ItemKind,
                    inventory.GetListParam16(InventoryListType.Main),
                    out var listType,
                    out var range))
                return Fail(plan, InventoryInsertError.InvalidTargetList);

            var insertCount = InventoryStackRuleService.NormalizeInsertCount(item, count);
            if (listType == InventoryListType.Main && InventoryStackRuleService.IsStackable(item))
            {
                if (!TryValidateSingleStackInsert(item, insertCount, plan))
                    return false;

                if (TryFindSameItemSlot(inventory, InventoryListType.Main, item, out var sameSlot))
                {
                    var sameItem = inventory.GetItem(InventoryListType.Main, sameSlot);
                    return TryPlanMergeIntoOccupiedSlot(item, insertCount, sameItem, InventoryListType.Main, sameSlot, plan);
                }

                var canUseQuickSlot = CanUseMainQuickSlot(item);
                var emptyFirst = canUseQuickSlot && item.ItemKind == ItemCore.KindConsumable ? QuickSlotRange : range;
                var emptySecond = canUseQuickSlot && item.ItemKind == ItemCore.KindConsumable ? range : QuickSlotRange;
                if (TryFindFirstEmptySlot(inventory, item, InventoryListType.Main, emptyFirst, out var slotIndex)
                    || (canUseQuickSlot && TryFindFirstEmptySlot(inventory, item, InventoryListType.Main, emptySecond, out slotIndex)))
                    return TryPlanInsertIntoEmptySlotCore(inventory, item, insertCount, InventoryListType.Main, slotIndex, plan);

                plan.RemainingCount = insertCount;
                return Fail(plan, InventoryInsertError.NoEmptySlot);
            }

            return TryPlanInsertIntoRangeCore(inventory, item, insertCount, listType, range, InventoryInsertMode.Default, plan);
        }

        internal static bool TryInsertByDefaultRule(
            InventoryService inventory,
            ItemCore item,
            int count,
            out InventoryInsertResult result)
        {
            if (!TryPlanInsertByDefaultRule(inventory, item, count, out var plan))
            {
                result = CreateResult(plan);
                return false;
            }

            return TryApplyInsertPlan(inventory, item, plan, out result);
        }

        internal static bool TryPlanInsertIntoSlot(
            InventoryService inventory,
            ItemCore item,
            int count,
            InventoryListType targetListType,
            short targetSlotIndex,
            out InventoryInsertPlan plan)
        {
            plan = CreatePlan();
            if (!TryValidateInsertBase(inventory, item, count, out _, plan))
                return false;

            var mode = ResolveInsertMode(targetListType);
            var insertCount = InventoryStackRuleService.NormalizeInsertCount(item, count);
            return TryPlanInsertIntoSlotCore(inventory, item, insertCount, targetListType, targetSlotIndex, mode, plan);
        }

        internal static bool TryInsertIntoSlot(
            InventoryService inventory,
            ItemCore item,
            int count,
            InventoryListType targetListType,
            short targetSlotIndex,
            out InventoryInsertResult result)
        {
            if (!TryPlanInsertIntoSlot(inventory, item, count, targetListType, targetSlotIndex, out var plan))
            {
                result = CreateResult(plan);
                return false;
            }

            return TryApplyInsertPlan(inventory, item, plan, out result);
        }

        internal static bool TryPlanInsertIntoRange(
            InventoryService inventory,
            ItemCore item,
            int count,
            InventoryListType targetListType,
            ItemSlotRange range,
            out InventoryInsertPlan plan)
        {
            plan = CreatePlan();
            if (!TryValidateInsertBase(inventory, item, count, out _, plan))
                return false;

            var mode = ResolveInsertMode(targetListType);
            var insertCount = InventoryStackRuleService.NormalizeInsertCount(item, count);
            return TryPlanInsertIntoRangeCore(inventory, item, insertCount, targetListType, range, mode, plan);
        }

        internal static bool TryInsertIntoRange(
            InventoryService inventory,
            ItemCore item,
            int count,
            InventoryListType targetListType,
            ItemSlotRange range,
            out InventoryInsertResult result)
        {
            if (!TryPlanInsertIntoRange(inventory, item, count, targetListType, range, out var plan))
            {
                result = CreateResult(plan);
                return false;
            }

            return TryApplyInsertPlan(inventory, item, plan, out result);
        }

        internal static bool TryApplyInsertPlan(
            InventoryService inventory,
            ItemCore item,
            InventoryInsertPlan plan,
            out InventoryInsertResult result)
        {
            result = CreateResult();
            if (plan == null || !plan.Success)
                return Fail(result, plan != null ? plan.Error : InventoryInsertError.InvalidItem);

            if (plan.MergeIntoExistingSlot)
                return TryApplyMergePlan(inventory, item, plan, result);

            return TryApplyEmptySlotPlan(inventory, item, plan, result);
        }

        internal static bool ValidatePersonalCargoInsert(ItemCore item, out InventoryInsertError error)
        {
            return ValidateCargoInsertCore(item, accountCargo: false, out error);
        }

        internal static bool ValidateAccountCargoInsert(ItemCore item, out InventoryInsertError error)
        {
            return ValidateCargoInsertCore(item, accountCargo: true, out error);
        }

        private static bool TryPlanInsertIntoRangeCore(
            InventoryService inventory,
            ItemCore item,
            int count,
            InventoryListType targetListType,
            ItemSlotRange range,
            InventoryInsertMode mode,
            InventoryInsertPlan plan)
        {
            if (!TryValidateTargetRange(inventory, item, targetListType, range, mode, plan))
                return false;

            if (InventoryStackRuleService.IsStackable(item))
            {
                if (!TryValidateSingleStackInsert(item, count, plan))
                    return false;

                if (TryFindSameItemSlot(inventory, targetListType, item, out var sameSlot))
                {
                    var sameItem = inventory.GetItem(targetListType, sameSlot);
                    return TryPlanMergeIntoOccupiedSlot(item, count, sameItem, targetListType, sameSlot, plan);
                }
            }

            if (TryFindFirstEmptySlot(inventory, item, targetListType, range, out var slotIndex))
                return TryPlanInsertIntoEmptySlotCore(inventory, item, count, targetListType, slotIndex, plan);

            plan.RemainingCount = count;
            return Fail(plan, InventoryInsertError.NoEmptySlot);
        }

        private static bool TryPlanInsertIntoSlotCore(
            InventoryService inventory,
            ItemCore item,
            int count,
            InventoryListType targetListType,
            short targetSlotIndex,
            InventoryInsertMode mode,
            InventoryInsertPlan plan)
        {
            if (!TryValidateTargetSlot(inventory, item, targetListType, targetSlotIndex, mode, plan))
                return false;

            var destination = inventory.GetItem(targetListType, targetSlotIndex);
            if (destination != null)
                return TryPlanMergeIntoOccupiedSlot(item, count, destination, targetListType, targetSlotIndex, plan);

            if (InventoryStackRuleService.IsStackable(item))
            {
                if (!TryValidateSingleStackInsert(item, count, plan))
                    return false;

                if (TryFindSameItemSlot(inventory, targetListType, item, out _))
                    return Fail(plan, InventoryInsertError.CannotStack);
            }

            return CompletePlan(plan, targetListType, targetSlotIndex, count, 0, mergeIntoExistingSlot: false);
        }

        private static bool TryPlanInsertIntoEmptySlotCore(
            InventoryService inventory,
            ItemCore item,
            int count,
            InventoryListType targetListType,
            short targetSlotIndex,
            InventoryInsertPlan plan)
        {
            if (!TryValidateTargetSlot(inventory, item, targetListType, targetSlotIndex, ResolveInsertMode(targetListType), plan))
                return false;

            if (inventory.GetItem(targetListType, targetSlotIndex) != null)
                return Fail(plan, InventoryInsertError.SlotOccupied);

            return CompletePlan(plan, targetListType, targetSlotIndex, count, 0, mergeIntoExistingSlot: false);
        }

        private static bool TryValidateSingleStackInsert(
            ItemCore item,
            int count,
            InventoryInsertPlan plan)
        {
            if (count <= 0)
                return Fail(plan, InventoryInsertError.InvalidCount);

            if (!InventoryStackRuleService.TryGetStackLimit(item, out var stackLimit))
                return Fail(plan, InventoryInsertError.StaticItemNotFound);

            if (count > stackLimit)
                return Fail(plan, InventoryInsertError.CannotStack);

            return true;
        }

        private static bool TryFindSameItemSlot(
            InventoryService inventory,
            InventoryListType targetListType,
            ItemCore item,
            out short slotIndex)
        {
            slotIndex = -1;
            if (!TryGetUniqueScanRange(inventory, targetListType, out var range))
                return false;

            for (var slot = range.Start; slot <= range.End; slot++)
            {
                var existing = inventory.GetItem(targetListType, slot);
                if (!InventoryStackRuleService.CanShareStack(item, existing))
                    continue;

                slotIndex = slot;
                return true;
            }

            return false;
        }

        private static bool TryFindFirstEmptySlot(
            InventoryService inventory,
            ItemCore item,
            InventoryListType targetListType,
            ItemSlotRange range,
            out short slotIndex)
        {
            slotIndex = -1;
            for (var slot = range.Start; slot <= range.End; slot++)
            {
                if (inventory.GetItem(targetListType, slot) != null)
                    continue;

                if (!IsSlotWritableForItem(inventory, item, targetListType, slot))
                    continue;

                slotIndex = slot;
                return true;
            }

            return false;
        }

        private static bool TryGetUniqueScanRange(
            InventoryService inventory,
            InventoryListType targetListType,
            out ItemSlotRange range)
        {
            switch (targetListType)
            {
                case InventoryListType.PersonalCargo:
                    range = ItemSlotBoundService.GetPersonalCargoOpenRange(inventory.GetListParam16(targetListType));
                    return range.Count > 0;
                case InventoryListType.AccountCargo:
                    range = ItemSlotBoundService.GetAccountCargoOpenRange(inventory.GetListParam16(targetListType));
                    return range.Count > 0;
                default:
                    return ItemSlotBoundService.TryGetItemSpacePhysicalRange(targetListType, out range);
            }
        }

        private static bool IsSlotWritableForItem(
            InventoryService inventory,
            ItemCore item,
            InventoryListType targetListType,
            short slotIndex)
        {
            if (targetListType == InventoryListType.Main
                && (InventoryService.IsVirtualMainSlot(slotIndex) || InventoryService.IsReservedMainSlot(slotIndex)))
                return false;

            if (targetListType == InventoryListType.PersonalCargo)
                return inventory.Cargo.IsOpenSlot(slotIndex);

            if (targetListType == InventoryListType.AccountCargo)
                return inventory.AccountCargo.IsOpenSlot(slotIndex);

            if (targetListType == InventoryListType.Main
                && slotIndex >= QuickSlotRange.Start
                && slotIndex <= QuickSlotRange.End)
                return CanPlaceInMainQuickSlot(inventory, item, slotIndex);

            return ItemSlotBoundService.IsValidSlotForKind(
                item.ItemKind,
                targetListType,
                slotIndex,
                inventory.GetListParam16(InventoryListType.Main));
        }

        private static bool TryPlanMergeIntoOccupiedSlot(
            ItemCore item,
            int count,
            ItemCore destination,
            InventoryListType targetListType,
            short targetSlotIndex,
            InventoryInsertPlan plan)
        {
            if (!InventoryStackRuleService.TryPlanMerge(
                    item,
                    destination,
                    count,
                    out var stackPlan,
                    out var rejectReason))
            {
                return Fail(plan, rejectReason == InventoryStackRejectReason.NotStackable
                    || rejectReason == InventoryStackRejectReason.DifferentItem
                        ? InventoryInsertError.SlotOccupied
                        : InventoryInsertError.CannotStack);
            }

            return CompletePlan(
                plan,
                targetListType,
                targetSlotIndex,
                stackPlan.AcceptedCount,
                stackPlan.RemainingCount,
                mergeIntoExistingSlot: true);
        }

        private static bool TryApplyMergePlan(
            InventoryService inventory,
            ItemCore item,
            InventoryInsertPlan plan,
            InventoryInsertResult result)
        {
            if (!TryValidateApplyBase(inventory, item, plan, result))
                return false;

            var destination = inventory.GetItem(plan.ListType, plan.SlotIndex);
            if (destination == null)
                return Fail(result, InventoryInsertError.SlotOccupied);

            if (!InventoryStackRuleService.TryPlanMerge(
                    item,
                    destination,
                    plan.InsertedCount,
                    out var stackPlan,
                    out var rejectReason)
                || stackPlan.AcceptedCount != plan.InsertedCount
                || stackPlan.RemainingCount != plan.RemainingCount)
            {
                return Fail(result, rejectReason == InventoryStackRejectReason.NotStackable
                    || rejectReason == InventoryStackRejectReason.DifferentItem
                        ? InventoryInsertError.SlotOccupied
                        : InventoryInsertError.CannotStack);
            }

            var updated = destination.Copy();
            updated.Count += plan.InsertedCount;
            if (!inventory.SetItem(plan.ListType, plan.SlotIndex, updated))
                return Fail(result, InventoryInsertError.UpdateFailed);

            return CompleteInsert(result, plan.ListType, plan.SlotIndex, plan.InsertedCount, plan.RemainingCount);
        }

        private static bool TryApplyEmptySlotPlan(
            InventoryService inventory,
            ItemCore item,
            InventoryInsertPlan plan,
            InventoryInsertResult result)
        {
            if (!TryValidateApplyBase(inventory, item, plan, result))
                return false;

            if (!TryValidateTargetSlot(
                    inventory,
                    item,
                    plan.ListType,
                    plan.SlotIndex,
                    ResolveInsertMode(plan.ListType),
                    result))
                return false;

            if (inventory.GetItem(plan.ListType, plan.SlotIndex) != null)
                return Fail(result, InventoryInsertError.SlotOccupied);

            if (InventoryStackRuleService.IsStackable(item)
                && TryFindSameItemSlot(inventory, plan.ListType, item, out _))
                return Fail(result, InventoryInsertError.CannotStack);

            var insertItem = item.Copy();
            if (InventoryStackRuleService.IsStackable(insertItem))
                insertItem.Count = plan.InsertedCount;

            if (!inventory.SetItem(plan.ListType, plan.SlotIndex, insertItem))
                return Fail(result, InventoryInsertError.UpdateFailed);

            return CompleteInsert(result, plan.ListType, plan.SlotIndex, plan.InsertedCount, plan.RemainingCount);
        }

        private static bool TryValidateApplyBase(
            InventoryService inventory,
            ItemCore item,
            InventoryInsertPlan plan,
            InventoryInsertResult result)
        {
            if (plan == null || !plan.Success)
                return Fail(result, plan != null ? plan.Error : InventoryInsertError.InvalidItem);

            if (!TryValidateInsertBase(inventory, item, plan.InsertedCount, out _, result))
                return false;

            return true;
        }

        private static bool TryValidateInsertBase(
            InventoryService inventory,
            ItemCore item,
            int count,
            out ItemMetadata metadata,
            InventoryInsertPlan plan)
        {
            metadata = null;
            if (inventory == null)
                return Fail(plan, InventoryInsertError.InvalidInventory);

            if (item == null)
                return Fail(plan, InventoryInsertError.InvalidItem);

            if (IsVirtualInsertItemId(item.ItemId))
                return Fail(plan, InventoryInsertError.VirtualItemRejected);

            if (item.IsEmpty || item.ItemId <= 0)
                return Fail(plan, InventoryInsertError.InvalidItem);

            var insertCount = InventoryStackRuleService.NormalizeInsertCount(item, count);
            if (insertCount <= 0)
                return Fail(plan, InventoryInsertError.InvalidCount);

            if (!TryResolveStaticItem(item.ItemId, out metadata))
                return Fail(plan, InventoryInsertError.StaticItemNotFound);

            if (IsExpired(item))
                return Fail(plan, InventoryInsertError.ItemExpired);

            return true;
        }

        private static bool TryValidateInsertBase(
            InventoryService inventory,
            ItemCore item,
            int count,
            out ItemMetadata metadata,
            InventoryInsertResult result)
        {
            metadata = null;
            if (inventory == null)
                return Fail(result, InventoryInsertError.InvalidInventory);

            if (item == null)
                return Fail(result, InventoryInsertError.InvalidItem);

            if (IsVirtualInsertItemId(item.ItemId))
                return Fail(result, InventoryInsertError.VirtualItemRejected);

            if (item.IsEmpty || item.ItemId <= 0)
                return Fail(result, InventoryInsertError.InvalidItem);

            var insertCount = InventoryStackRuleService.NormalizeInsertCount(item, count);
            if (insertCount <= 0)
                return Fail(result, InventoryInsertError.InvalidCount);

            if (!TryResolveStaticItem(item.ItemId, out metadata))
                return Fail(result, InventoryInsertError.StaticItemNotFound);

            if (IsExpired(item))
                return Fail(result, InventoryInsertError.ItemExpired);

            return true;
        }

        private static bool TryValidateTargetRange(
            InventoryService inventory,
            ItemCore item,
            InventoryListType targetListType,
            ItemSlotRange range,
            InventoryInsertMode mode,
            InventoryInsertPlan plan)
        {
            if (range.Count <= 0)
                return Fail(plan, InventoryInsertError.InvalidTargetSlot);

            if (!TryValidateCargoPolicy(item, mode, plan))
                return false;

            if (!ItemSlotBoundService.TryGetItemSpacePhysicalRange(targetListType, out var physicalRange))
                return Fail(plan, InventoryInsertError.InvalidTargetList);

            if (range.Start < physicalRange.Start || range.End > physicalRange.End)
                return Fail(plan, InventoryInsertError.InvalidTargetSlot);

            if (targetListType == InventoryListType.PersonalCargo
                || targetListType == InventoryListType.AccountCargo)
                return true;

            if (targetListType == InventoryListType.Main && (range.Start < InventoryService.MainSlotStart || range.End > InventoryService.MainSlotEnd))
                return Fail(plan, InventoryInsertError.InvalidTargetSlot);

            return true;
        }

        private static bool TryValidateTargetSlot(
            InventoryService inventory,
            ItemCore item,
            InventoryListType targetListType,
            short targetSlotIndex,
            InventoryInsertMode mode,
            InventoryInsertPlan plan)
        {
            if (!TryValidateCargoPolicy(item, mode, plan))
                return false;

            if (targetListType == InventoryListType.Main
                && (InventoryService.IsVirtualMainSlot(targetSlotIndex) || InventoryService.IsReservedMainSlot(targetSlotIndex)))
                return Fail(plan, InventoryInsertError.InvalidTargetSlot);

            if (!ItemSlotBoundService.TryGetItemSpacePhysicalRange(targetListType, out var physicalRange)
                || !physicalRange.Contains(targetSlotIndex))
                return Fail(plan, InventoryInsertError.InvalidTargetSlot);

            if (targetListType == InventoryListType.PersonalCargo && !inventory.Cargo.IsOpenSlot(targetSlotIndex))
                return Fail(plan, InventoryInsertError.InvalidTargetSlot);

            if (targetListType == InventoryListType.AccountCargo && !inventory.AccountCargo.IsOpenSlot(targetSlotIndex))
                return Fail(plan, InventoryInsertError.InvalidTargetSlot);

            if (targetListType == InventoryListType.PersonalCargo
                || targetListType == InventoryListType.AccountCargo)
                return true;

            if (targetListType == InventoryListType.Main && targetSlotIndex >= QuickSlotRange.Start && targetSlotIndex <= QuickSlotRange.End)
            {
                if (!CanPlaceInMainQuickSlot(inventory, item, targetSlotIndex))
                    return Fail(plan, InventoryInsertError.SlotKindMismatch);

                return true;
            }

            if (!ItemSlotBoundService.IsValidSlotForKind(
                    item.ItemKind,
                    targetListType,
                    targetSlotIndex,
                    inventory.GetListParam16(InventoryListType.Main)))
                return Fail(plan, InventoryInsertError.SlotKindMismatch);

            return true;
        }

        private static bool TryValidateTargetSlot(
            InventoryService inventory,
            ItemCore item,
            InventoryListType targetListType,
            short targetSlotIndex,
            InventoryInsertMode mode,
            InventoryInsertResult result)
        {
            if (!TryValidateCargoPolicy(item, mode, result))
                return false;

            if (targetListType == InventoryListType.Main
                && (InventoryService.IsVirtualMainSlot(targetSlotIndex) || InventoryService.IsReservedMainSlot(targetSlotIndex)))
                return Fail(result, InventoryInsertError.InvalidTargetSlot);

            if (!ItemSlotBoundService.TryGetItemSpacePhysicalRange(targetListType, out var physicalRange)
                || !physicalRange.Contains(targetSlotIndex))
                return Fail(result, InventoryInsertError.InvalidTargetSlot);

            if (targetListType == InventoryListType.PersonalCargo && !inventory.Cargo.IsOpenSlot(targetSlotIndex))
                return Fail(result, InventoryInsertError.InvalidTargetSlot);

            if (targetListType == InventoryListType.AccountCargo && !inventory.AccountCargo.IsOpenSlot(targetSlotIndex))
                return Fail(result, InventoryInsertError.InvalidTargetSlot);

            if (targetListType == InventoryListType.PersonalCargo
                || targetListType == InventoryListType.AccountCargo)
                return true;

            if (targetListType == InventoryListType.Main && targetSlotIndex >= QuickSlotRange.Start && targetSlotIndex <= QuickSlotRange.End)
            {
                if (!CanPlaceInMainQuickSlot(inventory, item, targetSlotIndex))
                    return Fail(result, InventoryInsertError.SlotKindMismatch);

                return true;
            }

            if (!ItemSlotBoundService.IsValidSlotForKind(
                    item.ItemKind,
                    targetListType,
                    targetSlotIndex,
                    inventory.GetListParam16(InventoryListType.Main)))
                return Fail(result, InventoryInsertError.SlotKindMismatch);

            return true;
        }

        private static bool TryValidateCargoPolicy(
            ItemCore item,
            InventoryInsertMode mode,
            InventoryInsertPlan plan)
        {
            if (mode == InventoryInsertMode.Default)
                return true;

            if (mode == InventoryInsertMode.PersonalCargo)
            {
                if (!ValidatePersonalCargoInsert(item, out var error))
                    return Fail(plan, error);
                return true;
            }

            if (!ValidateAccountCargoInsert(item, out var accountError))
                return Fail(plan, accountError);
            return true;
        }

        private static bool TryValidateCargoPolicy(
            ItemCore item,
            InventoryInsertMode mode,
            InventoryInsertResult result)
        {
            if (mode == InventoryInsertMode.Default)
                return true;

            if (mode == InventoryInsertMode.PersonalCargo)
            {
                if (!ValidatePersonalCargoInsert(item, out var error))
                    return Fail(result, error);
                return true;
            }

            if (!ValidateAccountCargoInsert(item, out var accountError))
                return Fail(result, accountError);
            return true;
        }

        private static bool ValidateCargoInsertCore(ItemCore item, bool accountCargo, out InventoryInsertError error)
        {
            error = InventoryInsertError.None;
            if (item == null)
            {
                error = InventoryInsertError.InvalidItem;
                return false;
            }

            if (IsVirtualInsertItemId(item.ItemId))
            {
                error = InventoryInsertError.VirtualItemRejected;
                return false;
            }

            if (item.IsEmpty || item.ItemId <= 0)
            {
                error = InventoryInsertError.InvalidItem;
                return false;
            }

            if (!TryResolveStaticItem(item.ItemId, out var metadata))
            {
                error = InventoryInsertError.StaticItemNotFound;
                return false;
            }

            if (IsRejectedCargoItemKind(item.ItemKind))
            {
                error = InventoryInsertError.ItemKindRejected;
                return false;
            }

            if (IsExpired(item))
            {
                error = InventoryInsertError.ItemExpired;
                return false;
            }

            if (HasImpossibleCargoContent(metadata, accountCargo))
            {
                error = InventoryInsertError.ImpossibleContentRejected;
                return false;
            }

            var attachType = NormalizeAttachType(metadata.AttachType);
            if (accountCargo)
            {
                if (item.EquipmentLockId != 0)
                {
                    error = InventoryInsertError.ItemLockRejected;
                    return false;
                }

                if (!IsAccountCargoAttachTypeAllowed(attachType))
                {
                    error = InventoryInsertError.AttachTypeRejected;
                    return false;
                }

                if (item.TradeRestriction == 1)
                {
                    error = InventoryInsertError.TradeRestrictionRejected;
                    return false;
                }

                return true;
            }

            if (string.Equals(attachType, "trade delete", StringComparison.OrdinalIgnoreCase))
            {
                error = InventoryInsertError.AttachTypeRejected;
                return false;
            }

            return true;
        }

        private static bool TryResolveStaticItem(int itemId, out ItemMetadata metadata)
        {
            metadata = null;
            try
            {
                metadata = ItemMetadataResolver.Resolve(itemId);
            }
            catch
            {
                return false;
            }

            return metadata != null
                && !string.Equals(metadata.ItemKind, "special", StringComparison.Ordinal);
        }

        private static InventoryInsertMode ResolveInsertMode(InventoryListType targetListType)
        {
            if (targetListType == InventoryListType.PersonalCargo)
                return InventoryInsertMode.PersonalCargo;
            if (targetListType == InventoryListType.AccountCargo)
                return InventoryInsertMode.AccountCargo;
            return InventoryInsertMode.Default;
        }

        private static bool IsRejectedCargoItemKind(byte itemKind)
        {
            return itemKind == ItemCore.KindQuest
                || itemKind == ItemCore.KindCreature
                || itemKind == ItemCore.KindCreatureEquipment
                || itemKind == ItemCore.KindCreatureConsumable
                || itemKind == ItemCore.KindAvatar
                || itemKind == ItemCore.KindAvatarEmblem;
        }

        private static bool CanUseMainQuickSlot(ItemCore item)
        {
            if (item == null)
                return false;

            return item.ItemKind != ItemCore.KindCreature
                && item.ItemKind != ItemCore.KindCreatureEquipment
                && item.ItemKind != ItemCore.KindCreatureConsumable
                && item.ItemKind != ItemCore.KindAvatar
                && item.ItemKind != ItemCore.KindAvatarEmblem;
        }

        private static bool CanPlaceInMainQuickSlot(InventoryService inventory, ItemCore item, short targetSlotIndex)
        {
            if (!CanUseMainQuickSlot(item))
                return false;

            if (!IsCharmEquipment(item))
                return true;

            for (var slot = QuickSlotRange.Start; slot <= QuickSlotRange.End; slot++)
            {
                if (slot == targetSlotIndex)
                    continue;

                if (IsCharmEquipment(inventory.GetItem(InventoryListType.Main, slot)))
                    return false;
            }

            return true;
        }

        private static bool IsCharmEquipment(ItemCore item)
        {
            if (item == null || !item.IsEquipmentItem())
                return false;

            try
            {
                return EquipmentTypeInfo.ParseOrUnknown(ItemMetadataResolver.ResolveEquipmentType(item.ItemId)) == EquipmentType.Charm;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsVirtualInsertItemId(int itemId)
        {
            return itemId == 0
                || itemId == 1
                || itemId == 2
                || CurrencyService.IsCubeFragment(itemId);
        }

        private static bool IsAccountCargoAttachTypeAllowed(string attachType)
        {
            return string.Equals(attachType, "free", StringComparison.OrdinalIgnoreCase)
                || string.Equals(attachType, "account", StringComparison.OrdinalIgnoreCase)
                || string.Equals(attachType, "sealing", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAttachType(string attachType)
        {
            if (string.IsNullOrWhiteSpace(attachType))
                return string.Empty;

            var value = attachType.Replace("`", string.Empty).Trim();
            if (value.Length >= 2 && value[0] == '[')
            {
                var end = value.IndexOf(']', 1);
                if (end > 1)
                    value = value.Substring(1, end - 1);
            }

            return value.Trim().ToLowerInvariant();
        }

        private static bool HasImpossibleCargoContent(ItemMetadata metadata, bool accountCargo)
        {
            if (metadata?.ImpossibleContents == null)
                return false;

            foreach (var item in metadata.ImpossibleContents)
            {
                var content = NormalizePvfLabel(item);
                if (!accountCargo && string.Equals(content, "charac cargo", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (accountCargo && string.Equals(content, "account cargo", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string NormalizePvfLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Replace("`", string.Empty).Trim();
            if (normalized.Length >= 2 && normalized[0] == '[')
            {
                var end = normalized.IndexOf(']', 1);
                if (end > 1)
                    normalized = normalized.Substring(1, end - 1);
            }

            return normalized.Trim().ToLowerInvariant();
        }

        private static bool IsExpired(ItemCore item)
        {
            if (item == null || item.ExpireTime <= 0)
                return false;

            return item.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds();
        }

        private static InventoryInsertPlan CreatePlan()
        {
            return new InventoryInsertPlan
            {
                Success = false,
                Error = InventoryInsertError.None,
                SlotIndex = -1,
            };
        }

        private static InventoryInsertResult CreateResult()
        {
            return new InventoryInsertResult
            {
                Success = false,
                Error = InventoryInsertError.None,
                SlotIndex = -1,
            };
        }

        private static InventoryInsertResult CreateResult(InventoryInsertPlan plan)
        {
            var result = CreateResult();
            if (plan == null)
                return result;

            result.Success = plan.Success;
            result.Error = plan.Error;
            result.ListType = plan.ListType;
            result.SlotIndex = plan.SlotIndex;
            result.InsertedCount = plan.InsertedCount;
            result.RemainingCount = plan.RemainingCount;
            return result;
        }

        private static bool CompletePlan(
            InventoryInsertPlan plan,
            InventoryListType listType,
            short slotIndex,
            int insertedCount,
            int remainingCount,
            bool mergeIntoExistingSlot)
        {
            plan.Success = true;
            plan.Error = InventoryInsertError.None;
            plan.ListType = listType;
            plan.SlotIndex = slotIndex;
            plan.InsertedCount = Math.Max(0, insertedCount);
            plan.RemainingCount = remainingCount;
            plan.MergeIntoExistingSlot = mergeIntoExistingSlot;
            return true;
        }

        private static bool CompleteInsert(
            InventoryInsertResult result,
            InventoryListType listType,
            short slotIndex,
            int insertedCount,
            int remainingCount)
        {
            result.Success = true;
            result.Error = InventoryInsertError.None;
            result.ListType = listType;
            result.SlotIndex = slotIndex;
            result.InsertedCount += Math.Max(0, insertedCount);
            result.RemainingCount = remainingCount;
            result.Changes.AddSlot(listType, slotIndex);
            return true;
        }

        private static bool Fail(InventoryInsertPlan plan, InventoryInsertError error)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));

            plan.Success = false;
            plan.Error = error;
            return false;
        }

        private static bool Fail(InventoryInsertResult result, InventoryInsertError error)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            result.Success = false;
            result.Error = error;
            return false;
        }
    }
}
