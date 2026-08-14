using System;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryMutationResultFactory
    {
        internal static InventoryMutationResult FromDelete(
            InventoryListType listType,
            short slotIndex,
            ItemCore source,
            InventoryDeleteResult delete)
        {
            var snapshot = delete?.SourceSnapshot ?? source;
            if (snapshot == null || delete == null || !delete.Success)
                return null;

            var stackable = InventoryStackRuleService.IsStackable(snapshot);
            return new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = snapshot.ItemId,
                RemainingStackCount = delete.RemainingCount,
                InstanceValue = stackable
                    ? delete.RemainingCount
                    : snapshot.InstanceValue,
                Durability = snapshot.Durability,
                RequestedCount = ClampCount(delete.DeletedCount),
                AppliedCount = ClampCount(delete.DeletedCount),
            };
        }

        internal static InventoryMutationResult FromGrant(
            InventoryService inventory,
            InventoryRewardGrantResult grant)
        {
            if (inventory == null || grant == null || !grant.Success)
                return null;

            var core = grant.SlotIndex >= 0
                ? inventory.GetItem(grant.ListType, grant.SlotIndex)
                : null;
            if (core == null)
                core = grant.Core;

            var stackable = core != null && InventoryStackRuleService.IsStackable(core);
            var remainingCount = stackable
                ? Math.Max(0, core.Count)
                : Math.Max(0, grant.FinalCount);
            return new InventoryMutationResult
            {
                ListType = grant.ListType,
                SlotIndex = grant.SlotIndex,
                ItemTemplateId = grant.ItemTemplateId,
                RemainingStackCount = remainingCount,
                InstanceValue = stackable
                    ? remainingCount
                    : (core != null ? core.InstanceValue : 0),
                Durability = core != null ? core.Durability : (ushort)0,
                RequestedCount = ClampCount(grant.RequestedCount),
                AppliedCount = ClampCount(grant.GrantedCount),
            };
        }

        private static short ClampCount(int value)
            => checked((short)Math.Min(short.MaxValue, Math.Max(0, value)));
    }
}
