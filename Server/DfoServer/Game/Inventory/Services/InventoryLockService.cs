using System;
using System.Collections.Generic;
using DfoServer.Game.TitleBook;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryLockService
    {
        private const byte EquipmentLockErrorTitleTradeDelete = 17;
        private const byte EquipmentLockErrorInvalidTarget = 19;
        private const byte EquipmentLockErrorEmptySlot = 21;
        private const byte EquipmentLockErrorNoFreeId = 22;

        internal static bool TryToggleSortItemLock(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            out SortItemLockEntry entry)
        {
            entry = null;
            if (inventory == null || !IsSupportedLockListType(listType) || !CanApplySortItemLock(listType, slotIndex))
                return false;

            var item = inventory.GetItem(listType, slotIndex);
            if (item == null)
                return false;

            var updated = item.Copy();
            updated.SortLockFlag = updated.SortLockFlag == 1 ? (byte)0 : (byte)1;
            if (!inventory.SetItem(listType, slotIndex, updated))
                return false;

            entry = new SortItemLockEntry
            {
                ListType = InventoryRefreshSenderMap(listType),
                SlotIndex = slotIndex,
                State = updated.SortLockFlag,
            };
            return true;
        }

        internal static bool TryUnlockSortItemLock(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex)
        {
            if (inventory == null || !IsSupportedLockListType(listType))
                return false;

            var item = inventory.GetItem(listType, slotIndex);
            if (item == null)
                return false;

            if (!CanApplySortItemLock(listType, slotIndex))
            {
                if (item.SortLockFlag == 0)
                    return false;

                var cleanup = item.Copy();
                cleanup.SortLockFlag = 0;
                inventory.SetItem(listType, slotIndex, cleanup);
                return false;
            }

            if (item.SortLockFlag == 0)
                return true;

            var updated = item.Copy();
            updated.SortLockFlag = 0;
            return inventory.SetItem(listType, slotIndex, updated);
        }

        internal static IReadOnlyList<SortItemLockEntry> LoadSortItemLocks(
            InventoryService inventory,
            InventoryListType? listType = null)
        {
            var entries = new List<SortItemLockEntry>();
            if (inventory == null)
                return entries;

            foreach (var type in EnumerateSortLockListTypes(listType))
            {
                foreach (var pair in inventory.GetItems(type))
                {
                    if (!CanApplySortItemLock(type, pair.Key) || pair.Value.SortLockFlag != 1)
                        continue;

                    entries.Add(new SortItemLockEntry
                    {
                        ListType = InventoryRefreshSenderMap(type),
                        SlotIndex = pair.Key,
                        State = 1,
                    });
                }
            }

            return entries;
        }

        internal static IReadOnlyList<EquipmentItemLockEntry> LoadEquipmentItemLocks(
            InventoryService inventory,
            InventoryListType? listType = null)
        {
            var entries = new List<Tuple<byte, EquipmentItemLockEntry>>();
            if (inventory == null)
                return Array.Empty<EquipmentItemLockEntry>();

            foreach (var type in EnumerateEquipmentLockListTypes(listType))
            {
                foreach (var pair in inventory.GetItems(type))
                {
                    var equipmentLockId = pair.Value.EquipmentLockId;
                    if (equipmentLockId == 0
                        || !inventory.EquipmentLocks.TryGet(equipmentLockId, out var itemLock)
                        || itemLock.State != 1)
                        continue;

                    entries.Add(Tuple.Create(equipmentLockId, new EquipmentItemLockEntry
                    {
                        ListType = type,
                        SlotIndex = pair.Key,
                        State = 1,
                        RemainingSeconds = 0,
                    }));
                }
            }

            foreach (var pair in inventory.TitleBook.GetItems())
            {
                var currentListType = TitleBookModel.GetLockListType(pair.Key.Category);
                if (listType.HasValue && listType.Value != currentListType)
                    continue;

                var equipmentLockId = pair.Value.EquipmentLockId;
                if (equipmentLockId == 0
                    || !inventory.EquipmentLocks.TryGet(equipmentLockId, out var itemLock)
                    || itemLock.State != 1)
                    continue;

                entries.Add(Tuple.Create(equipmentLockId, new EquipmentItemLockEntry
                {
                    ListType = currentListType,
                    SlotIndex = (short)pair.Key.SlotIndex,
                    State = 1,
                    RemainingSeconds = 0,
                }));
            }

            entries.Sort((left, right) => left.Item1.CompareTo(right.Item1));
            var result = new List<EquipmentItemLockEntry>(entries.Count);
            foreach (var entry in entries)
                result.Add(entry.Item2);
            return result;
        }

        internal static bool IsEquipmentItemLocked(
            InventoryService inventory,
            ItemCore item)
        {
            return inventory != null
                && item != null
                && item.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(item.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        internal static bool TryLockEquipmentItem(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            byte equipmentLockId,
            out EquipmentItemLockResult result)
        {
            result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorInvalidTarget);
            if (inventory == null || !IsSupportedEquipmentLockListType(listType))
                return false;

            var item = inventory.GetItem(listType, slotIndex);
            if (item == null)
            {
                result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorEmptySlot);
                return false;
            }

            if (!TryValidateEquipmentLockTarget(item, forLock: true, out var errorCode))
            {
                result = CreateEquipmentLockResult(false, listType, slotIndex, errorCode);
                return false;
            }

            if (item.EquipmentLockId != 0)
            {
                result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorInvalidTarget);
                return false;
            }

            if (equipmentLockId == 0)
            {
                result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorNoFreeId);
                return false;
            }

            var updated = item.Copy();
            updated.EquipmentLockId = equipmentLockId;
            if (!inventory.SetItem(listType, slotIndex, updated))
                return false;

            result = CreateEquipmentLockResult(true, listType, slotIndex, 0, equipmentLockId);
            return true;
        }

        internal static bool TryUnlockEquipmentItem(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            out EquipmentItemLockResult result)
        {
            result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorInvalidTarget);
            if (inventory == null || !IsSupportedEquipmentLockListType(listType))
                return false;

            var item = inventory.GetItem(listType, slotIndex);
            if (item == null)
            {
                result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorEmptySlot);
                return false;
            }

            if (!TryValidateEquipmentLockTarget(item, forLock: false, out var errorCode)
                || item.EquipmentLockId == 0)
            {
                result = CreateEquipmentLockResult(false, listType, slotIndex, errorCode);
                return false;
            }

            var equipmentLockId = item.EquipmentLockId;
            var updated = item.Copy();
            updated.EquipmentLockId = 0;
            if (!inventory.SetItem(listType, slotIndex, updated))
                return false;

            result = CreateEquipmentLockResult(true, listType, slotIndex, 0, equipmentLockId);
            return true;
        }

        internal static bool TryCancelEquipmentItemUnlock(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            out EquipmentItemLockResult result)
        {
            result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorInvalidTarget);
            if (inventory == null || !IsSupportedEquipmentLockListType(listType))
                return false;

            var item = inventory.GetItem(listType, slotIndex);
            if (item == null)
            {
                result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorEmptySlot);
                return false;
            }

            if (!TryValidateEquipmentLockTarget(item, forLock: false, out var errorCode)
                || item.EquipmentLockId == 0)
            {
                result = CreateEquipmentLockResult(false, listType, slotIndex, errorCode);
                return false;
            }

            result = CreateEquipmentLockResult(true, listType, slotIndex, 0, item.EquipmentLockId);
            return true;
        }

        internal static bool SetEquipmentLockId(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            byte equipmentLockId)
        {
            if (inventory == null)
                return false;

            var item = inventory.GetItem(listType, slotIndex);
            if (item == null)
                return false;

            var updated = item.Copy();
            updated.EquipmentLockId = equipmentLockId;
            return inventory.SetItem(listType, slotIndex, updated);
        }

        private static bool TryValidateEquipmentLockTarget(ItemCore item, bool forLock, out byte errorCode)
        {
            errorCode = EquipmentLockErrorInvalidTarget;
            if (item == null || item.IsEmpty)
                return false;

            if (!ItemMetadataResolver.TryLoadEquipmentFile(item.ItemId, out var equipment))
                return false;

            if (forLock
                && IsEquipmentType(equipment.EquipmentType, "creature")
                && item.SealFlag != 0)
                return false;

            if (forLock
                && IsEquipmentType(equipment.EquipmentType, "title name")
                && IsEquipmentLockTradeDeleteAttachType(equipment.AttachType))
            {
                errorCode = EquipmentLockErrorTitleTradeDelete;
                return false;
            }

            return true;
        }

        private static bool IsSupportedLockListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet
                || listType == InventoryListType.AccountCargo;
        }

        private static bool IsSupportedEquipmentLockListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.Pet;
        }

        internal static bool CanApplySortItemLock(InventoryListType listType, short slotIndex)
        {
            if (listType == InventoryListType.Main
                && slotIndex >= 289
                && slotIndex <= 344)
                return false;

            if (listType == InventoryListType.Pet
                && slotIndex >= InventoryService.CreatureSlotStart
                && slotIndex <= 139)
                return false;

            return true;
        }

        private static IEnumerable<InventoryListType> EnumerateSortLockListTypes(InventoryListType? listType)
        {
            if (listType.HasValue)
            {
                if (IsSupportedLockListType(listType.Value))
                    yield return listType.Value;
                yield break;
            }

            yield return InventoryListType.Main;
            yield return InventoryListType.Avatar;
            yield return InventoryListType.PersonalCargo;
            yield return InventoryListType.Pet;
            yield return InventoryListType.AccountCargo;
        }

        private static IEnumerable<InventoryListType> EnumerateEquipmentLockListTypes(InventoryListType? listType)
        {
            if (listType.HasValue)
            {
                if (IsSupportedEquipmentLockListType(listType.Value))
                    yield return listType.Value;
                yield break;
            }

            yield return InventoryListType.Main;
            yield return InventoryListType.PersonalCargo;
            yield return InventoryListType.Equipment;
            yield return InventoryListType.Avatar;
            yield return InventoryListType.Pet;
        }

        private static InventoryListType InventoryRefreshSenderMap(InventoryListType listType)
        {
            return listType == InventoryListType.Equipment ? InventoryListType.Avatar : listType;
        }

        private static EquipmentItemLockResult CreateEquipmentLockResult(
            bool success,
            InventoryListType listType,
            short slotIndex,
            byte errorCode,
            byte equipmentLockId = 0)
        {
            return new EquipmentItemLockResult
            {
                Success = success,
                ErrorCode = errorCode,
                ListType = listType,
                SlotIndex = slotIndex,
                EquipmentLockId = equipmentLockId,
                RemainingSeconds = 0,
            };
        }

        private static bool IsEquipmentType(string equipmentType, string expected)
        {
            return string.Equals(NormalizeEquipmentLockPvfToken(equipmentType), expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEquipmentLockTradeDeleteAttachType(string attachType)
        {
            return string.Equals(NormalizeEquipmentLockPvfToken(attachType), "trade delete", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeEquipmentLockPvfToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim().Trim('`').Trim();
            if (normalized.Length >= 2 && normalized[0] == '[' && normalized[normalized.Length - 1] == ']')
                normalized = normalized.Substring(1, normalized.Length - 2);

            return normalized.Trim();
        }
    }
}
