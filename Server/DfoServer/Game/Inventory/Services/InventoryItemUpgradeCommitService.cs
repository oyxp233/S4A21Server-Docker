using DfoServer.Game.ItemUpgrade;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryItemUpgradeCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            ItemUpgradeCommand command,
            out ItemUpgradeResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || command == null)
                return false;

            var upgradeApplied = false;
            ItemUpgradeResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "upgrade-item",
                (connection, transaction) =>
                {
                    upgradeApplied = InventoryItemUpgradeService.TryUpgradeItem(
                        lease.Inventory,
                        command,
                        out committedResult);
                    return upgradeApplied;
                });

            result = committedResult;
            persistenceFailed = upgradeApplied && !committed;
            return upgradeApplied && committed;
        }
    }
}
