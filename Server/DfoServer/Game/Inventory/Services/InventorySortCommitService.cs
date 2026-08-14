namespace DfoServer.Game.Inventory
{
    internal static class InventorySortCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            InventoryListType listType,
            byte category,
            out InventorySortServiceResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null)
                return false;

            var sortApplied = false;
            InventorySortServiceResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "sort-item-space",
                (connection, transaction) =>
                {
                    sortApplied = InventorySortService.TrySort(
                        lease.Inventory,
                        listType,
                        category,
                        out committedResult);
                    return sortApplied;
                });

            result = committedResult;
            persistenceFailed = sortApplied && !committed;
            return sortApplied && committed;
        }
    }
}
