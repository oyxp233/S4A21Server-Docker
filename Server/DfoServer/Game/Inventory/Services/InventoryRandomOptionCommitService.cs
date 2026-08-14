namespace DfoServer.Game.Inventory
{
    internal static class InventoryRandomOptionCommitService
    {
        internal static bool TryCommitUnseal(
            InventoryLease lease,
            short targetSlotIndex,
            int targetItemTemplateId,
            out RandomOptionUnsealResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null)
                return false;

            var applied = false;
            RandomOptionUnsealResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "unseal-random-option",
                (connection, transaction) =>
                {
                    applied = InventoryEquipmentMutationService.TryUnsealRandomOption(
                        lease.Inventory,
                        targetSlotIndex,
                        targetItemTemplateId,
                        out committedResult);
                    return applied;
                });

            result = committedResult;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }

        internal static bool TryCommitChange(
            InventoryLease lease,
            short targetSlotIndex,
            int targetItemTemplateId,
            byte requestedOptionIndex,
            out RandomOptionUnsealResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null)
                return false;

            var applied = false;
            RandomOptionUnsealResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "change-random-option",
                (connection, transaction) =>
                {
                    applied = InventoryEquipmentMutationService.TryChangeRandomOption(
                        lease.Inventory,
                        targetSlotIndex,
                        targetItemTemplateId,
                        requestedOptionIndex,
                        out committedResult);
                    return applied;
                });

            result = committedResult;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }
    }
}
