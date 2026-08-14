using System;
using System.Globalization;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    internal static class PetConsumableService
    {
        internal static bool IsPetConsumableSlot(InventoryListType listType, short slotIndex)
        {
            return listType == InventoryListType.Pet
                && slotIndex >= ItemSlotBoundService.PetConsumableSlotStart
                && slotIndex <= ItemSlotBoundService.PetConsumableSlotEnd;
        }

        internal static bool TryUsePetConsumable(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            out InventoryMutationResult result)
        {
            result = null;
            if (!CanUsePetConsumable(
                    inventory,
                    listType,
                    slotIndex,
                    expectedItemTemplateId))
                return false;

            var source = inventory.GetItem(listType, slotIndex);

            var sourceItemId = source.ItemId;
            if (!InventoryDeleteService.TryDecreaseStack(inventory, listType, slotIndex, 1, out var delete)
                || delete == null
                || !delete.Success)
                return false;

            result = new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = sourceItemId,
                RemainingStackCount = delete.RemainingCount,
                InstanceValue = delete.RemainingCount,
                RequestedCount = 1,
                AppliedCount = 1,
            };

            ApplyPetFoodSatiety(inventory, sourceItemId, result);
            return true;
        }

        internal static bool CanUsePetConsumable(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId)
        {
            if (inventory == null || !IsPetConsumableSlot(listType, slotIndex))
                return false;

            if (!inventory.TryGetItem(listType, slotIndex, out var source)
                || source == null
                || source.ItemKind != ItemCore.KindCreatureConsumable
                || source.Count <= 0)
            {
                return false;
            }

            return expectedItemTemplateId <= 0
                || source.ItemId == expectedItemTemplateId;
        }

        private static void ApplyPetFoodSatiety(
            InventoryService inventory,
            int itemTemplateId,
            InventoryMutationResult result)
        {
            var delta = ResolvePetFoodSatietyDelta(itemTemplateId);
            if (delta <= 0)
                return;

            if (!PetInventoryAccessor.TryGetEquippedCreature(inventory, out _, out var detail)
                || detail == null)
                return;

            var before = ClampSatiety(detail.Stomach);
            var after = ClampSatiety(before + delta);
            if (after == before)
                return;

            detail.Stomach = (byte)after;
            inventory.CreatureDetails.PutDirty(detail);

            result.PetCreatureKey = detail.Uid;
            result.PetSatietyBefore = before;
            result.PetSatietyAfter = after;
            result.PetSatietyChanged = true;
        }

        private static int ResolvePetFoodSatietyDelta(int itemTemplateId)
        {
            var stackable = StackableItemProvider.Load(itemTemplateId);
            if (stackable == null)
                return 0;

            var stackableType = StackableItemProvider.NormalizeType(stackable.StackableType);
            if (!stackableType.Equals("[feed]", StringComparison.OrdinalIgnoreCase))
                return 0;

            var values = ScriptValueTokenizer.Tokenize(stackable.IntData);
            if (values.Count < 3
                || !int.TryParse(
                    values[2],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var delta))
                return 0;

            return delta > 0 ? delta : 0;
        }

        private static int ClampSatiety(int value)
        {
            if (value <= 0)
                return 0;
            if (value >= 100)
                return 100;
            return value;
        }
    }
}
