namespace DfoServer.Game.Inventory
{
    internal static class InventoryDeleteCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int requestedCount,
            out InventoryMutationResult mutation)
        {
            mutation = null;
            if (lease?.Inventory == null)
                return false;

            lock (lease.SyncRoot)
            {
                if (!InventoryDeleteService.CanDeleteForClient(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        requestedCount))
                {
                    return false;
                }
            }

            InventoryMutationResult committedMutation = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "delete-item",
                (connection, transaction) =>
                    InventoryDeleteService.TryDeleteForClient(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        requestedCount,
                        out committedMutation));
            if (!committed || committedMutation == null)
                return false;

            mutation = committedMutation;
            return true;
        }

        internal static bool TryCommitStackableUse(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int expectedItemId,
            out InventoryMutationResult mutation)
        {
            mutation = null;
            if (lease?.Inventory == null)
                return false;

            var resolvedItemId = 0;
            lock (lease.SyncRoot)
            {
                if (!InventoryDeleteService.CanUseStackableForClient(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        expectedItemId,
                        out resolvedItemId))
                {
                    return false;
                }
            }

            InventoryMutationResult committedMutation = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "use-stackable",
                (connection, transaction) =>
                {
                    if (!UsableCountLimitService.TryRecordUseIfLimited(
                            connection,
                            transaction,
                            lease.Inventory.CharacterId,
                            resolvedItemId,
                            1,
                            out var usableCountState))
                    {
                        return false;
                    }

                    if (!InventoryDeleteService.TryUseStackableForClient(
                            lease.Inventory,
                            listType,
                            slotIndex,
                            resolvedItemId,
                            out committedMutation))
                    {
                        return false;
                    }

                    if (committedMutation != null)
                        committedMutation.UsableCountState = usableCountState;
                    return true;
                });
            if (!committed || committedMutation == null)
                return false;

            mutation = committedMutation;
            return true;
        }
    }
}
