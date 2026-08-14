namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureExperienceCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            int consumedFatigue,
            out PetCreatureExperienceUpdate update)
        {
            update = PetCreatureExperienceUpdate.Noop(
                lease?.CharacterId ?? 0);
            if (lease?.Inventory == null || consumedFatigue <= 0)
                return false;

            var committedUpdate = update;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "grant-pet-room-clear-experience",
                (connection, transaction) =>
                {
                    committedUpdate = PetCreatureExperienceService
                        .ApplyDungeonClearExperience(
                            lease.Inventory,
                            consumedFatigue);
                    return true;
                });
            if (!committed)
                return false;

            update = committedUpdate;
            return true;
        }
    }
}
