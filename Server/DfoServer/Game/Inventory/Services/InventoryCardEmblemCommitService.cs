namespace DfoServer.Game.Inventory
{
    internal static class InventoryCardEmblemCommitService
    {
        internal static bool TryCommitEmblemCompound(
            InventoryLease lease,
            EmblemCompoundRequest request,
            out EmblemCompoundResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || request == null)
                return false;

            var applied = false;
            EmblemCompoundResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "compound-emblem",
                (connection, transaction) =>
                {
                    applied = InventoryEmblemCompoundService.TryCompoundEmblems(
                        lease.Inventory,
                        request,
                        out committedResult);
                    return applied;
                });

            result = committedResult;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }

        internal static bool TryCommitMonsterCardBind(
            InventoryLease lease,
            MonsterCardBindService service,
            short binderSlot,
            short firstSlot,
            short secondSlot,
            out MonsterCardBindResult result,
            out string rejection,
            out bool persistenceFailed)
        {
            result = null;
            rejection = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || service == null)
                return false;

            var applied = false;
            MonsterCardBindResult committedResult = null;
            string committedRejection = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "bind-monster-card",
                (connection, transaction) =>
                {
                    applied = service.TryBind(
                        lease.Inventory,
                        binderSlot,
                        firstSlot,
                        secondSlot,
                        out committedResult,
                        out committedRejection);
                    return applied;
                });

            result = committedResult;
            rejection = committedRejection;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }

        internal static bool TryCommitMonsterCardUpgrade(
            InventoryLease lease,
            MonsterCardUpgradeService service,
            InventoryListType listType,
            short targetSlot,
            short materialSlot,
            short materialCount,
            out MonsterCardUpgradeResult result,
            out string rejection,
            out bool persistenceFailed)
        {
            result = null;
            rejection = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || service == null)
                return false;

            var applied = false;
            MonsterCardUpgradeResult committedResult = null;
            string committedRejection = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "upgrade-monster-card",
                (connection, transaction) =>
                {
                    applied = service.TryUpgrade(
                        lease.Inventory,
                        listType,
                        targetSlot,
                        materialSlot,
                        materialCount,
                        out committedResult,
                        out committedRejection);
                    return applied;
                });

            result = committedResult;
            rejection = committedRejection;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }
    }
}
