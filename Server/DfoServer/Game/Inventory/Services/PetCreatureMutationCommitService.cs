namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureMutationCommitService
    {
        internal static bool TryCommitHatch(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            out CreatureHatchResult result)
        {
            result = null;
            if (lease?.Inventory == null)
                return false;

            lock (lease.SyncRoot)
            {
                if (!PetCreatureEggService.CanHatchCreatureEgg(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        expectedItemTemplateId))
                {
                    return false;
                }
            }

            CreatureHatchResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "hatch-pet-creature",
                (connection, transaction) =>
                    PetCreatureEggService.TryHatchCreatureEgg(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        expectedItemTemplateId,
                        out committedResult));
            if (!committed || committedResult == null)
                return false;

            result = committedResult;
            return true;
        }

        internal static bool TryCommitRename(
            InventoryLease lease,
            PetCreatureRenameRequest request,
            out PetCreatureRenameResult result)
        {
            result = null;
            if (lease?.Inventory == null)
                return false;

            lock (lease.SyncRoot)
            {
                if (!PetCreatureRenameService.CanRenameEquippedPetCreature(
                        lease.Inventory,
                        request))
                {
                    return false;
                }
            }

            PetCreatureRenameResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "rename-pet-creature",
                (connection, transaction) =>
                    PetCreatureRenameService.TryRenameEquippedPetCreature(
                        lease.Inventory,
                        request,
                        out committedResult));
            if (!committed || committedResult == null)
                return false;

            result = committedResult;
            return true;
        }
    }
}
