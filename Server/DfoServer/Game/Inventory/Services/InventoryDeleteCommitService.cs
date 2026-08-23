using System;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryDeleteCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int requestedCount,
            out InventoryMutationResult mutation)
        {
            return TryCommit(
                lease,
                listType,
                slotIndex,
                requestedCount,
                "delete-item",
                afterDelete: null,
                out mutation);
        }

        internal static bool TryCommit(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int requestedCount,
            string operation,
            Func<InventoryMutationResult, bool> afterDelete,
            out InventoryMutationResult mutation)
        {
            mutation = null;
            if (lease?.Inventory == null)
                return false;

            lock (lease.SyncRoot)
            {
                if (!InventoryDeleteService.CanDeleteForClient(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        requestedCount))
                {
                    return false;
                }
            }

            InventoryMutationResult committedMutation = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                operation,
                (connection, transaction) =>
                {
                    if (!InventoryDeleteService.TryDeleteForClient(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        requestedCount,
                        out committedMutation))
                    {
                        return false;
                    }

                    return afterDelete == null || afterDelete(committedMutation);
                });
            if (!committed || committedMutation == null)
                return false;

            mutation = committedMutation;
            return true;
        }

        internal static bool TryCommitStackableUse(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int expectedItemId,
            out InventoryMutationResult mutation)
        {
            var result = TryCommitStackableUseDetailed(
                lease,
                listType,
                slotIndex,
                expectedItemId);
            mutation = result?.Mutation;
            return result != null && result.Consumed;
        }

        internal static InventoryStackableUseCommitResult TryCommitStackableUseDetailed(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int expectedItemId)
        {
            if (lease?.Inventory == null)
                return null;

            var resolvedItemId = 0;
            lock (lease.SyncRoot)
            {
                if (!InventoryDeleteService.CanUseStackableForClient(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        expectedItemId,
                        out resolvedItemId))
                {
                    return null;
                }
            }

            InventoryMutationResult committedMutation = null;
            InventoryItemLifecycleUsePlan lifecyclePlan = null;
            var consumed = false;
            var expiredDeleted = false;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "use-stackable",
                (connection, transaction) =>
                {
                    lifecyclePlan = InventoryItemLifecycleService.PrepareUse(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        resolvedItemId,
                        InventoryItemLifecycleService.UtcNowUnixSeconds());
                    if (lifecyclePlan.SourceExpiredDeleted)
                    {
                        committedMutation = lifecyclePlan.SourceMutation;
                        expiredDeleted = true;
                        return true;
                    }

                    if (!lifecyclePlan.Success)
                        return false;

                    if (!UsableCountLimitService.TryRecordUseIfLimited(
                            connection,
                            transaction,
                            lease.Inventory.CharacterId,
                            resolvedItemId,
                            1,
                            out var usableCountState))
                    {
                        return false;
                    }

                    if (!InventoryDeleteService.TryUseStackableForClient(
                            lease.Inventory,
                            listType,
                            slotIndex,
                            resolvedItemId,
                            out committedMutation))
                    {
                        return false;
                    }

                    InventoryItemLifecycleService.ApplyUseSuccess(
                        lease.Inventory,
                        lifecyclePlan);

                    if (committedMutation != null)
                        committedMutation.UsableCountState = usableCountState;
                    consumed = true;
                    return true;
                });

            if (!committed || committedMutation == null)
                return null;

            return new InventoryStackableUseCommitResult
            {
                Committed = true,
                Consumed = consumed,
                SourceExpiredDeleted = expiredDeleted,
                Mutation = committedMutation,
                LifecycleStatus = lifecyclePlan != null
                    ? lifecyclePlan.Status
                    : InventoryItemLifecycleStatus.Success,
                Detail = lifecyclePlan?.Detail,
                ItemTemplateId = resolvedItemId,
            };
        }
    }

    internal sealed class InventoryStackableUseCommitResult
    {
        public bool Committed { get; set; }

        public bool Consumed { get; set; }

        public bool SourceExpiredDeleted { get; set; }

        public InventoryMutationResult Mutation { get; set; }

        public InventoryItemLifecycleStatus LifecycleStatus { get; set; }

        public string Detail { get; set; }

        public int ItemTemplateId { get; set; }
    }
}
