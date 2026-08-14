namespace DfoServer.Game.Inventory
{
    internal static class InventoryEquipmentRegenerationCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            EquipmentRegenerationRequest request,
            out EquipmentRegenerationResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || request == null)
                return false;

            var regenerationApplied = false;
            EquipmentRegenerationResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "regenerate-equipment",
                (connection, transaction) =>
                {
                    regenerationApplied = InventoryEquipmentRegenerationService.TryRegenerate(
                        lease.Inventory,
                        request,
                        out committedResult);
                    return regenerationApplied;
                });

            result = committedResult;
            persistenceFailed = regenerationApplied && !committed;
            return regenerationApplied && committed;
        }
    }
}
