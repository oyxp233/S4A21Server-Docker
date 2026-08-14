using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureEggService
    {
        internal static bool TryHatchCreatureEgg(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            out CreatureHatchResult result)
        {
            result = null;
            if (!TryResolveHatch(
                    inventory,
                    listType,
                    slotIndex,
                    expectedItemTemplateId,
                    out var source,
                    out var hatchedItemTemplateId,
                    out var creatureKey))
                return false;

            var updated = source.Copy();
            updated.ItemId = hatchedItemTemplateId;
            updated.Value = creatureKey;
            updated.SealFlag = 0;
            if (!inventory.SetItem(listType, slotIndex, updated))
                return false;

            var detail = inventory.CreatureDetails.GetDetail(creatureKey) ?? CreateDefaultCreatureDetail(creatureKey, hatchedItemTemplateId);
            if (detail.ExpireDate <= 0)
                detail.ExpireDate = CreatureDetail.GetExpireDate(hatchedItemTemplateId);
            if (!inventory.CreatureDetails.PutDirty(detail))
                return false;

            result = new CreatureHatchResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                EggItemTemplateId = source.ItemId,
                HatchedItemTemplateId = hatchedItemTemplateId,
                PetSerialOrHandle = creatureKey,
            };
            FileLogger.Log($"[PetCreatureEgg] hatch cid={inventory.CharacterId} slot={slotIndex} egg=0x{source.ItemId:X8} pet=0x{hatchedItemTemplateId:X8} key=0x{creatureKey:X8}");
            return true;
        }

        internal static bool CanHatchCreatureEgg(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId)
        {
            return TryResolveHatch(
                inventory,
                listType,
                slotIndex,
                expectedItemTemplateId,
                out _,
                out _,
                out _);
        }

        private static bool TryResolveHatch(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            out ItemCore source,
            out int hatchedItemTemplateId,
            out int creatureKey)
        {
            source = null;
            hatchedItemTemplateId = 0;
            creatureKey = 0;
            if (inventory == null || listType != InventoryListType.Pet)
                return false;

            if (!inventory.TryGetItem(listType, slotIndex, out source)
                || source == null
                || source.ItemKind != ItemCore.KindCreature)
            {
                return false;
            }

            if (expectedItemTemplateId > 0
                && source.ItemId != expectedItemTemplateId)
            {
                return false;
            }

            if (!CreatureEggResolver.TryResolveHatchedCreatureItemId(
                    source.ItemId,
                    out hatchedItemTemplateId))
            {
                return false;
            }

            creatureKey = source.Value > 0
                ? source.Value
                : PetInventoryAccessor.NextCreatureKey(inventory);
            return creatureKey > 0;
        }

        internal static CreatureDetail CreateDefaultCreatureDetail(int creatureKey, int itemTemplateId)
        {
            return new CreatureDetail
            {
                Uid = creatureKey,
                Field04 = 100,
                ModeFlag = 0,
                ProgressValue32 = 0,
                FieldAfterValue32 = 1,
                ExpireDate = CreatureDetail.GetExpireDate(itemTemplateId),
            };
        }
    }
}
