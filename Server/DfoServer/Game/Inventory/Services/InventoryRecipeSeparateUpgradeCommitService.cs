using DfoServer.Game.ItemUpgrade;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryRecipeSeparateUpgradeCommitService
    {
        internal static bool TryCommitCompoundItem(
            InventoryLease lease,
            CompoundItemRecipeRequest request,
            out CompoundItemRecipeResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || request == null)
                return false;

            var applied = false;
            CompoundItemRecipeResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "compound-item-recipe",
                (connection, transaction) =>
                {
                    applied = InventoryCompoundItemRecipeService.TryCompoundItemRecipe(
                        lease.Inventory,
                        request,
                        out committedResult);
                    return applied;
                });

            result = committedResult;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }

        internal static bool TryCommitSeparateUpgrade(
            InventoryLease lease,
            SeparateUpgradeCommand command,
            SeparateUpgradeTable table,
            ItemMetadata metadata,
            out SeparateUpgradeResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || command == null || table == null || metadata == null)
                return false;

            var applied = false;
            SeparateUpgradeResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "separate-upgrade",
                (connection, transaction) =>
                {
                    var ticketItem = lease.Inventory.GetItem(
                        InventoryListType.Main,
                        command.MaterialSlotIndex);
                    if (ticketItem != null
                        && SeparateUpgradeTicketDefinition.TryLoad(ticketItem.ItemId, out var ticket))
                    {
                        applied = InventorySeparateUpgradeService.TryApplyTicket(
                            lease.Inventory,
                            command,
                            ticket,
                            table,
                            metadata,
                            out committedResult);
                    }
                    else
                    {
                        applied = InventorySeparateUpgradeService.TryUpgrade(
                            lease.Inventory,
                            command,
                            table,
                            metadata,
                            out committedResult);
                    }

                    return applied;
                });

            result = committedResult;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }
    }
}
