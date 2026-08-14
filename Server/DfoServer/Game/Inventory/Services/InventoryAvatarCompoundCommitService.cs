using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryAvatarCompoundCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            InventoryAvatarCompoundRequest request,
            Func<int, int, int, IReadOnlyList<int>> resolveNewItemIds,
            out InventoryAvatarCompoundResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null
                || request == null
                || resolveNewItemIds == null)
            {
                return false;
            }

            var compoundApplied = false;
            InventoryAvatarCompoundResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "compound-avatar",
                (connection, transaction) =>
                {
                    compoundApplied = InventoryAvatarCompoundService.TryCompoundAvatar(
                        lease.Inventory,
                        request,
                        resolveNewItemIds,
                        () => AvatarDetailRepository.AllocateAvatarUid(
                            connection,
                            transaction),
                        out committedResult);
                    return compoundApplied;
                });

            result = committedResult;
            persistenceFailed = compoundApplied && !committed;
            return compoundApplied && committed;
        }

        internal static bool TryCommitSet(
            InventoryLease lease,
            InventoryAvatarCompoundSetRequest request,
            Func<int, int> resolveNewItemId,
            out InventoryAvatarCompoundResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null
                || request == null
                || resolveNewItemId == null)
            {
                return false;
            }

            var compoundApplied = false;
            InventoryAvatarCompoundResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "compound-avatar-set",
                (connection, transaction) =>
                {
                    compoundApplied = InventoryAvatarCompoundService.TryCompoundAvatarSet(
                        lease.Inventory,
                        request,
                        resolveNewItemId,
                        () => AvatarDetailRepository.AllocateAvatarUid(
                            connection,
                            transaction),
                        out committedResult);
                    return compoundApplied;
                });

            result = committedResult;
            persistenceFailed = compoundApplied && !committed;
            return compoundApplied && committed;
        }
    }
}
