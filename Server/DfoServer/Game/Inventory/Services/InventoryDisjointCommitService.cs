namespace DfoServer.Game.Inventory
{
    internal static class InventoryDisjointCommitService
    {
        internal static bool TryCommitItem(
            InventoryLease lease,
            DisjointItemRequest request,
            out DisjointItemResult result,
            out bool persistenceFailed)
            => TryCommitItem(
                lease,
                request,
                null,
                out result,
                out persistenceFailed);

        internal static bool TryCommitItem(
            InventoryLease lease,
            DisjointItemRequest request,
            TryResolveDisjointMaterials tryResolveMaterials,
            out DisjointItemResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || request == null)
                return false;

            var disjointApplied = false;
            DisjointItemResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "disjoint-item",
                (connection, transaction) =>
                {
                    disjointApplied = tryResolveMaterials == null
                        ? InventoryDisjointService.TryDisjointItem(
                            lease.Inventory,
                            request,
                            out committedResult)
                        : InventoryDisjointService.TryDisjointItem(
                            lease.Inventory,
                            request,
                            tryResolveMaterials,
                            out committedResult);
                    return disjointApplied;
                });

            result = committedResult;
            persistenceFailed = disjointApplied && !committed;
            return disjointApplied && committed;
        }

        internal static bool TryCommitAvatar(
            InventoryLease lease,
            AvatarDisjointRequest request,
            out AvatarDisjointResult result,
            out bool persistenceFailed)
            => TryCommitAvatar(
                lease,
                request,
                null,
                out result,
                out persistenceFailed);

        internal static bool TryCommitAvatar(
            InventoryLease lease,
            AvatarDisjointRequest request,
            TryResolveAvatarDisjointMaterials tryResolveMaterials,
            out AvatarDisjointResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || request == null)
                return false;

            var disjointApplied = false;
            AvatarDisjointResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "disjoint-avatar",
                (connection, transaction) =>
                {
                    disjointApplied = tryResolveMaterials == null
                        ? InventoryAvatarDisjointService.TryDisjointAvatar(
                            lease.Inventory,
                            request,
                            out committedResult)
                        : InventoryAvatarDisjointService.TryDisjointAvatar(
                            lease.Inventory,
                            request,
                            tryResolveMaterials,
                            out committedResult);
                    return disjointApplied;
                });

            result = committedResult;
            persistenceFailed = disjointApplied && !committed;
            return disjointApplied && committed;
        }
    }
}
