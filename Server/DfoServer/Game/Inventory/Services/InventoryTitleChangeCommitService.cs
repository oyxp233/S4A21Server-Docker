namespace DfoServer.Game.Inventory
{
    internal static class InventoryTitleChangeCommitService
    {
        internal static bool TryCommitTitleChange(
            InventoryLease lease,
            InventoryTitleChangeRequest request,
            InventoryTitleChangeResolution resolution,
            out InventoryTitleChangeResult result,
            out bool persistenceFailed)
        {
            return TryCommit(
                lease,
                "title-change",
                request,
                resolution,
                out result,
                out persistenceFailed);
        }

        internal static bool TryCommitLimitedCube(
            InventoryLease lease,
            InventoryTitleChangeRequest request,
            InventoryTitleChangeResolution resolution,
            out InventoryTitleChangeResult result,
            out bool persistenceFailed)
        {
            return TryCommit(
                lease,
                "limited-cube-title-change",
                request,
                resolution,
                out result,
                out persistenceFailed);
        }

        private static bool TryCommit(
            InventoryLease lease,
            string operation,
            InventoryTitleChangeRequest request,
            InventoryTitleChangeResolution resolution,
            out InventoryTitleChangeResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || request == null || resolution == null)
                return false;

            var applied = false;
            InventoryTitleChangeResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                operation,
                (connection, transaction) =>
                {
                    applied = InventoryTitleChangeService.TryChange(
                        lease.Inventory,
                        request,
                        resolution,
                        out committedResult);
                    return applied;
                });

            result = committedResult;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }
    }
}
