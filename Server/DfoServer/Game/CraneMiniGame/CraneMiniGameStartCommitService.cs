using DfoServer.Game.Inventory;

namespace DfoServer.Game.CraneMiniGame
{
    internal static class CraneMiniGameStartCommitService
    {
        internal static bool TryStart(
            InventoryLease lease,
            ushort machineId,
            CraneMiniGameStartService startService,
            out CraneMiniGameStartResult result)
        {
            result = null;
            if (lease?.Inventory == null || startService == null)
                return false;

            CraneMiniGameStartResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "crane-start-use",
                (connection, transaction) => startService.TryStart(
                    lease.Inventory,
                    machineId,
                    out committedResult));
            result = committedResult;
            return committed;
        }
    }
}
