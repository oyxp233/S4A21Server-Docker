using System;

namespace DfoServer.Game.Inventory
{
    internal readonly struct ItemSlotRange
    {
        public ItemSlotRange(short start, short end)
        {
            Start = start;
            End = end;
        }

        public short Start { get; }

        public short End { get; }

        public int Count => End >= Start ? End - Start + 1 : 0;

        public bool Contains(short slotIndex)
        {
            return slotIndex >= Start && slotIndex <= End;
        }
    }

    internal static class ItemSlotBoundService
    {
        internal const int MainExpandStageNone = 0;
        internal const int MainExpandStage1 = 8;
        internal const int MainExpandStage2 = 16;
        internal const int MainExpandStageFull = 24;
        internal const int MainQuickSlotStart = 3;
        internal const int MainQuickSlotEnd = 8;
        internal const int PetInventorySlotStart = 0;
        internal const int PetInventorySlotEnd = 139;
        internal const int PetEquipmentSlotStart = 140;
        internal const int PetEquipmentSlotEnd = 188;
        internal const int PetConsumableSlotStart = 189;
        internal const int PetConsumableSlotEnd = 239;
        internal const int AvatarEmblemSlotStart = 289;
        internal const int AvatarEmblemSlotEnd = 351;

        internal static bool IsMainQuickSlot(int slotIndex)
        {
            return slotIndex >= MainQuickSlotStart && slotIndex <= MainQuickSlotEnd;
        }

        internal static bool TryGetSlotRange(
            byte itemKind,
            int mainExpandStageKey,
            out InventoryListType listType,
            out ItemSlotRange range)
        {
            listType = InventoryListType.Main;
            range = default;

            switch (itemKind)
            {
                case ItemCore.KindUnknown:
                    range = new ItemSlotRange(MainQuickSlotStart, MainQuickSlotEnd);
                    return true;
                case ItemCore.KindEquipment:
                    return TryGetExpandedMainRange(9, 64, mainExpandStageKey, out range);
                case ItemCore.KindConsumable:
                    return TryGetExpandedMainRange(65, 120, mainExpandStageKey, out range);
                case ItemCore.KindMaterial:
                    return TryGetExpandedMainRange(121, 176, mainExpandStageKey, out range);
                case ItemCore.KindQuest:
                    return TryGetExpandedMainRange(177, 232, mainExpandStageKey, out range);
                case ItemCore.KindExpertJobMaterial:
                    return TryGetExpandedMainRange(233, 288, mainExpandStageKey, out range);
                case ItemCore.KindAvatarEmblem:
                    range = new ItemSlotRange(AvatarEmblemSlotStart, AvatarEmblemSlotEnd);
                    return true;
                case ItemCore.KindAvatar:
                    listType = InventoryListType.Avatar;
                    range = GetAvatarOpenRange(0);
                    return true;
                case ItemCore.KindCreature:
                    listType = InventoryListType.Pet;
                    range = new ItemSlotRange(PetInventorySlotStart, PetInventorySlotEnd);
                    return true;
                case ItemCore.KindCreatureEquipment:
                    listType = InventoryListType.Pet;
                    range = new ItemSlotRange(PetEquipmentSlotStart, PetEquipmentSlotEnd);
                    return true;
                case ItemCore.KindCreatureConsumable:
                    listType = InventoryListType.Pet;
                    range = new ItemSlotRange(PetConsumableSlotStart, PetConsumableSlotEnd);
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsValidSlotForKind(
            byte itemKind,
            InventoryListType listType,
            short slotIndex,
            int mainExpandStageKey)
        {
            if (listType == InventoryListType.Equipment)
                return TryGetBodyItemKindBySlot(slotIndex, out var bodyKind) && bodyKind == itemKind;

            return TryGetSlotRange(itemKind, mainExpandStageKey, out var expectedListType, out var range)
                && expectedListType == listType
                && range.Contains(slotIndex);
        }

        internal static bool TryGetItemKindBySlot(
            InventoryListType listType,
            short slotIndex,
            int mainExpandStageKey,
            out byte itemKind)
        {
            itemKind = ItemCore.KindUnknown;

            switch (listType)
            {
                case InventoryListType.Main:
                    return TryGetMainItemKindBySlot(slotIndex, mainExpandStageKey, out itemKind);
                case InventoryListType.Avatar:
                    itemKind = ItemCore.KindAvatar;
                    return GetAvatarOpenRange(0).Contains(slotIndex);
                case InventoryListType.Equipment:
                    return TryGetBodyItemKindBySlot(slotIndex, out itemKind);
                case InventoryListType.Pet:
                    return TryGetPetItemKindBySlot(slotIndex, out itemKind);
                default:
                    return false;
            }
        }

        internal static bool TryResolveItemKindForMigration(
            InventoryListType listType,
            short slotIndex,
            int itemTemplateId,
            out byte itemKind)
        {
            if (listType == InventoryListType.Main
                && (slotIndex >= MainQuickSlotStart && slotIndex <= MainQuickSlotEnd
                    || InExpandedRange(slotIndex, 9, 64, MainExpandStageFull)))
            {
                if (ItemMetadataResolver.TryResolveItemKind(itemTemplateId, out itemKind)
                    && slotIndex >= MainQuickSlotStart
                    && slotIndex <= MainQuickSlotEnd)
                    return true;
            }

            if (listType == InventoryListType.Main && slotIndex >= MainQuickSlotStart && slotIndex <= MainQuickSlotEnd)
                return ItemMetadataResolver.TryResolveItemKind(itemTemplateId, out itemKind);

            if (listType == InventoryListType.PersonalCargo || listType == InventoryListType.AccountCargo)
                return ItemMetadataResolver.TryResolveItemKind(itemTemplateId, out itemKind);

            return TryGetItemKindBySlot(listType, slotIndex, MainExpandStageFull, out itemKind)
                || ItemMetadataResolver.TryResolveItemKind(itemTemplateId, out itemKind);
        }

        internal static bool TryGetItemSpacePhysicalRange(InventoryListType listType, out ItemSlotRange range)
        {
            switch (listType)
            {
                case InventoryListType.Main:
                    range = new ItemSlotRange(3, 351);
                    return true;
                case InventoryListType.Avatar:
                    range = GetAvatarOpenRange(0);
                    return true;
                case InventoryListType.Equipment:
                    range = new ItemSlotRange(0, 29);
                    return true;
                case InventoryListType.Pet:
                    range = new ItemSlotRange(0, 239);
                    return true;
                case InventoryListType.PersonalCargo:
                    range = new ItemSlotRange(CargoModel.SlotStart, CargoModel.SlotEnd);
                    return true;
                case InventoryListType.AccountCargo:
                    range = new ItemSlotRange(AccountCargoModel.SlotStart, AccountCargoModel.SlotEnd);
                    return true;
                default:
                    range = default;
                    return false;
            }
        }

        internal static ItemSlotRange GetAvatarOpenRange(int avatarListParam16)
        {
            return new ItemSlotRange(0, 209);
        }

        internal static ItemSlotRange GetPersonalCargoOpenRange(int personalCargoListParam16)
        {
            var capacity = CargoModel.NormalizeCapacity(ToUInt16Clamped(personalCargoListParam16));
            return CreateOpenRange(CargoModel.SlotStart, capacity);
        }

        internal static ItemSlotRange GetAccountCargoOpenRange(int selectionKey)
        {
            var capacity = AccountCargoModel.NormalizeSelectionKey(ToUInt16Clamped(selectionKey));
            return CreateOpenRange(AccountCargoModel.SlotStart, capacity);
        }

        internal static bool TryNormalizeMainExpandStageKey(int value, out int normalizedValue)
        {
            switch (value)
            {
                case MainExpandStageNone:
                case MainExpandStage1:
                case MainExpandStage2:
                case MainExpandStageFull:
                    normalizedValue = value;
                    return true;
                default:
                    normalizedValue = MainExpandStageNone;
                    return false;
            }
        }

        private static bool TryGetMainItemKindBySlot(short slotIndex, int mainExpandStageKey, out byte itemKind)
        {
            itemKind = ItemCore.KindUnknown;

            if (slotIndex >= MainQuickSlotStart && slotIndex <= MainQuickSlotEnd)
                return true;

            if (!TryNormalizeMainExpandStageKey(mainExpandStageKey, out var normalizedStage))
                return false;

            if (InExpandedRange(slotIndex, 9, 64, normalizedStage))
            {
                itemKind = ItemCore.KindEquipment;
                return true;
            }

            if (InExpandedRange(slotIndex, 65, 120, normalizedStage))
            {
                itemKind = ItemCore.KindConsumable;
                return true;
            }

            if (InExpandedRange(slotIndex, 121, 176, normalizedStage))
            {
                itemKind = ItemCore.KindMaterial;
                return true;
            }

            if (InExpandedRange(slotIndex, 177, 232, normalizedStage))
            {
                itemKind = ItemCore.KindQuest;
                return true;
            }

            if (InExpandedRange(slotIndex, 233, 288, normalizedStage))
            {
                itemKind = ItemCore.KindExpertJobMaterial;
                return true;
            }

            if (slotIndex >= AvatarEmblemSlotStart && slotIndex <= AvatarEmblemSlotEnd)
            {
                itemKind = ItemCore.KindAvatarEmblem;
                return true;
            }

            return false;
        }

        private static bool TryGetBodyItemKindBySlot(short slotIndex, out byte itemKind)
        {
            if (slotIndex >= 0 && slotIndex <= 10)
            {
                itemKind = ItemCore.KindAvatar;
                return true;
            }

            if (slotIndex == PetInventoryLayout.CreatureEquipSlot)
            {
                itemKind = ItemCore.KindCreature;
                return true;
            }

            if (PetInventoryLayout.IsArtifactEquipSlot(slotIndex))
            {
                itemKind = ItemCore.KindCreatureEquipment;
                return true;
            }

            if ((slotIndex >= 11 && slotIndex <= 23) || slotIndex == 29)
            {
                itemKind = ItemCore.KindEquipment;
                return true;
            }

            itemKind = ItemCore.KindUnknown;
            return false;
        }

        private static bool TryGetPetItemKindBySlot(short slotIndex, out byte itemKind)
        {
            if (slotIndex >= PetInventorySlotStart && slotIndex <= PetInventorySlotEnd)
            {
                itemKind = ItemCore.KindCreature;
                return true;
            }

            if (slotIndex >= PetEquipmentSlotStart && slotIndex <= PetEquipmentSlotEnd)
            {
                itemKind = ItemCore.KindCreatureEquipment;
                return true;
            }

            if (slotIndex >= PetConsumableSlotStart && slotIndex <= PetConsumableSlotEnd)
            {
                itemKind = ItemCore.KindCreatureConsumable;
                return true;
            }

            itemKind = ItemCore.KindUnknown;
            return false;
        }

        private static bool TryGetExpandedMainRange(int fullStart, int fullEnd, int mainExpandStageKey, out ItemSlotRange range)
        {
            if (!TryNormalizeMainExpandStageKey(mainExpandStageKey, out var normalizedStage))
            {
                range = default;
                return false;
            }

            range = new ItemSlotRange((short)fullStart, (short)GetExpandedMainEnd(fullEnd, normalizedStage));
            return true;
        }

        private static bool InExpandedRange(short slotIndex, int fullStart, int fullEnd, int mainExpandStageKey)
        {
            var end = GetExpandedMainEnd(fullEnd, mainExpandStageKey);
            return slotIndex >= fullStart && slotIndex <= end;
        }

        private static int GetExpandedMainEnd(int fullEnd, int mainExpandStageKey)
        {
            return fullEnd - (MainExpandStageFull - mainExpandStageKey);
        }

        private static ItemSlotRange CreateOpenRange(short start, ushort capacity)
        {
            if (capacity == 0)
                return new ItemSlotRange(start, (short)(start - 1));

            return new ItemSlotRange(start, (short)(start + capacity - 1));
        }

        private static ushort ToUInt16Clamped(int value)
        {
            if (value <= 0)
                return 0;

            return value > ushort.MaxValue ? ushort.MaxValue : (ushort)value;
        }

    }
}
