namespace DfoServer.Game.Inventory
{
    internal static class InventorySpaceCheckService
    {
        internal const int RequiredFreeMaterialRewardSlots = 3;
        internal const int RequiredFreeAvatarDisjointRewardSlots = 5;

        internal static bool HasEnoughMaterialFreeSlots(
            InventoryService inventory,
            out int freeSlotCount,
            out ItemSlotRange range)
        {
            return HasEnoughMainFreeSlots(
                inventory,
                ItemCore.KindMaterial,
                RequiredFreeMaterialRewardSlots,
                out freeSlotCount,
                out range);
        }

        internal static bool HasEnoughAvatarEmblemFreeSlots(
            InventoryService inventory,
            out int freeSlotCount,
            out ItemSlotRange range)
        {
            return HasEnoughMainFreeSlots(
                inventory,
                ItemCore.KindAvatarEmblem,
                RequiredFreeAvatarDisjointRewardSlots,
                out freeSlotCount,
                out range);
        }

        private static bool HasEnoughMainFreeSlots(
            InventoryService inventory,
            byte itemKind,
            int requiredCount,
            out int freeSlotCount,
            out ItemSlotRange range)
        {
            freeSlotCount = 0;
            range = default;

            if (inventory == null || requiredCount <= 0)
                return false;

            if (!ItemSlotBoundService.TryGetSlotRange(
                    itemKind,
                    inventory.GetListParam16(InventoryListType.Main),
                    out var listType,
                    out range)
                || listType != InventoryListType.Main)
                return false;

            for (var slotIndex = range.Start; slotIndex <= range.End; slotIndex++)
            {
                if (inventory.GetItem(InventoryListType.Main, slotIndex) != null)
                    continue;

                freeSlotCount++;
                if (freeSlotCount >= requiredCount)
                    return true;
            }

            return false;
        }
    }
}
