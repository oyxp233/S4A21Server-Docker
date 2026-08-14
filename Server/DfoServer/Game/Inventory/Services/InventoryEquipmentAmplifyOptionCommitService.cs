namespace DfoServer.Game.Inventory
{
    internal static class InventoryEquipmentAmplifyOptionCommitService
    {
        internal static bool TryCommitPurify(
            InventoryLease lease,
            PurifyItemRequest request,
            out PurifyItemResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || request == null)
                return false;

            var applied = false;
            PurifyItemResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "purify-item",
                (connection, transaction) =>
                {
                    applied = InventoryEquipmentMutationService.TryPurifyItem(
                        lease.Inventory,
                        request,
                        out committedResult);
                    return applied;
                });

            result = committedResult;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }

        internal static bool TryCommitInvest(
            InventoryLease lease,
            InvestItemAmplifyOptionRequest request,
            out InvestItemAmplifyOptionResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || request == null)
                return false;

            var applied = false;
            InvestItemAmplifyOptionResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "invest-item-amplify-option",
                (connection, transaction) =>
                {
                    applied = InventoryEquipmentMutationService.TryInvestItemAmplifyOption(
                        lease.Inventory,
                        request,
                        out committedResult);
                    return applied;
                });

            result = committedResult;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }
    }
}
