namespace DfoServer.Game.Inventory
{
    internal static class InventorySpecialConsumableCommitService
    {
        internal static bool TryCommitBoosterItem(
            InventoryLease lease,
            BoosterUseRequest request,
            string characterJobLabel,
            IInventoryOverflowRewardSink overflowSink,
            out BoosterUseResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null)
                return false;

            var applied = false;
            var applyCompleted = false;
            var databaseAccessFailed = false;
            var mailboxDeliveryFailed = false;
            BoosterUseResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "special-consumable-booster",
                (connection, transaction) =>
                {
                    var transactionSink = new TransactionBoundInventoryOverflowRewardSink(
                        connection,
                        transaction,
                        overflowSink);
                    try
                    {
                        applied = InventorySpecialConsumableService.TryUseBoosterItem(
                            connection,
                            transaction,
                            lease.Inventory,
                            request,
                            characterJobLabel,
                            transactionSink,
                            out committedResult,
                            out databaseAccessFailed);
                        applyCompleted = true;
                        return applied;
                    }
                    finally
                    {
                        mailboxDeliveryFailed = transactionSink.MailboxDeliveryFailed;
                    }
                });

            result = committedResult;
            persistenceFailed = !committed
                && (!applyCompleted || applied || databaseAccessFailed || mailboxDeliveryFailed);
            return applied
                && committed
                && (committedResult == null || !committedResult.SourceExpiredDeleted);
        }

        internal static bool TryCommitPackage0207(
            InventoryLease lease,
            short slotIndex,
            System.Collections.Generic.IReadOnlyList<int> selectedItemTemplateIds,
            IInventoryOverflowRewardSink overflowSink,
            out BoosterUseResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null)
                return false;

            var applied = false;
            var applyCompleted = false;
            var mailboxDeliveryFailed = false;
            BoosterUseResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "special-consumable-package-0207",
                (connection, transaction) =>
                {
                    var transactionSink = new TransactionBoundInventoryOverflowRewardSink(
                        connection,
                        transaction,
                        overflowSink);
                    try
                    {
                        applied = InventorySpecialConsumableService.TryOpenPackage0207(
                            lease.Inventory,
                            slotIndex,
                            selectedItemTemplateIds,
                            transactionSink,
                            out committedResult);
                        applyCompleted = true;
                        return applied;
                    }
                    finally
                    {
                        mailboxDeliveryFailed = transactionSink.MailboxDeliveryFailed;
                    }
                });

            result = committedResult;
            persistenceFailed = !committed && (!applyCompleted || applied || mailboxDeliveryFailed);
            return applied && committed;
        }

        internal static bool TryCommitAvatarPackage(
            InventoryLease lease,
            AvatarPackageOpenRequest request,
            IInventoryOverflowRewardSink overflowSink,
            out AvatarPackageOpenResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || request == null)
                return false;

            var applied = false;
            var applyCompleted = false;
            var mailboxDeliveryFailed = false;
            AvatarPackageOpenResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "special-consumable-avatar-package",
                (connection, transaction) =>
                {
                    var transactionSink = new TransactionBoundInventoryOverflowRewardSink(
                        connection,
                        transaction,
                        overflowSink);
                    try
                    {
                        applied = InventorySpecialConsumableService.TryOpenAvatarPackage(
                            lease.Inventory,
                            request,
                            transactionSink,
                            out committedResult);
                        applyCompleted = true;
                        return applied;
                    }
                    finally
                    {
                        mailboxDeliveryFailed = transactionSink.MailboxDeliveryFailed;
                    }
                });

            result = committedResult;
            persistenceFailed = !committed && (!applyCompleted || applied || mailboxDeliveryFailed);
            return applied && committed;
        }

        internal static bool TryCommitSelectablePackage(
            InventoryLease lease,
            SelectablePackageOpenRequest request,
            IInventoryOverflowRewardSink overflowSink,
            out SelectablePackageOpenResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || request == null)
                return false;

            var applied = false;
            var applyCompleted = false;
            var mailboxDeliveryFailed = false;
            SelectablePackageOpenResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "special-consumable-selectable-package",
                (connection, transaction) =>
                {
                    var transactionSink = new TransactionBoundInventoryOverflowRewardSink(
                        connection,
                        transaction,
                        overflowSink);
                    try
                    {
                        applied = InventorySpecialConsumableService.TryOpenSelectablePackage(
                            lease.Inventory,
                            request,
                            transactionSink,
                            out committedResult);
                        applyCompleted = true;
                        return applied;
                    }
                    finally
                    {
                        mailboxDeliveryFailed = transactionSink.MailboxDeliveryFailed;
                    }
                });

            result = committedResult;
            persistenceFailed = !committed && (!applyCompleted || applied || mailboxDeliveryFailed);
            return applied && committed;
        }
    }
}
