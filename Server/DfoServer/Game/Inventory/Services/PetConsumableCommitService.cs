namespace DfoServer.Game.Inventory
{
    internal static class PetConsumableCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            out InventoryMutationResult mutation)
        {
            mutation = null;
            if (lease?.Inventory == null)
                return false;

            lock (lease.SyncRoot)
            {
                if (!PetConsumableService.CanUsePetConsumable(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        expectedItemTemplateId))
                {
                    return false;
                }
            }

            InventoryMutationResult committedMutation = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "use-pet-consumable",
                (connection, transaction) =>
                    PetConsumableService.TryUsePetConsumable(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        expectedItemTemplateId,
                        out committedMutation));
            if (!committed || committedMutation == null)
                return false;

            mutation = committedMutation;
            return true;
        }
    }
}
