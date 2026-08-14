namespace DfoServer.Game.Inventory
{
    internal static class SortItemLockCommitService
    {
        internal static bool TryCommitToggle(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            out SortItemLockEntry entry,
            out bool persistenceFailed)
        {
            entry = null;
            persistenceFailed = false;
            if (lease?.Inventory == null)
                return false;

            var toggleApplied = false;
            SortItemLockEntry committedEntry = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "toggle-sort-item-lock",
                (connection, transaction) =>
                {
                    toggleApplied = InventoryLockService.TryToggleSortItemLock(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        out committedEntry);
                    return toggleApplied;
                });

            entry = committedEntry;
            persistenceFailed = toggleApplied && !committed;
            return toggleApplied && committed;
        }

        internal static bool TryCommitUnlock(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            out bool changed)
        {
            changed = false;
            if (lease?.Inventory == null)
                return false;

            var serviceResult = false;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "unlock-sort-item-lock",
                (connection, transaction) =>
                {
                    serviceResult = InventoryLockService.TryUnlockSortItemLock(
                        lease.Inventory,
                        listType,
                        slotIndex);
                    return true;
                });
            if (!committed)
                return false;

            changed = serviceResult;
            return true;
        }
    }
}
