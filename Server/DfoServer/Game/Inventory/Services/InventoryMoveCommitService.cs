namespace DfoServer.Game.Inventory
{
    internal static class InventoryMoveCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            InventoryMoveRequest request,
            byte characterJob,
            int characterGrowType,
            out InventoryMoveServiceResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || request == null)
                return false;

            var moveApplied = false;
            InventoryMoveServiceResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "move-item-space",
                (connection, transaction) =>
                {
                    moveApplied = InventoryMoveService.TryMove(
                        lease.Inventory,
                        request,
                        characterJob,
                        characterGrowType,
                        out committedResult);
                    return moveApplied;
                });

            result = committedResult;
            persistenceFailed = moveApplied && !committed;
            return moveApplied && committed;
        }
    }
}
