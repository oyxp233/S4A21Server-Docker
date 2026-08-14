using DfoServer.Game.Mailbox;

namespace DfoServer.Game.Inventory
{
    internal static class CeraShopRuntimePurchaseCommitService
    {
        internal static bool TryPurchase(
            InventoryLease lease,
            int accountId,
            int commodityNo,
            byte paymentMode,
            byte attributeValue,
            int couponItemId,
            short couponSlot,
            CeraShopPurchaseOptions itemOptions,
            MailboxInventoryOverflowRewardSink overflowRewardSink,
            out InventoryMutationResult result,
            out CeraShopPurchaseFailure failure)
        {
            result = null;
            failure = CeraShopPurchaseFailure.Unknown;
            if (lease?.Inventory?.Database == null
                || accountId <= 0
                || commodityNo <= 0)
            {
                return false;
            }

            InventoryMutationResult appliedResult = null;
            var appliedFailure = CeraShopPurchaseFailure.Unknown;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "cerashop-runtime-purchase",
                (connection, transaction) =>
                {
                    var context =
                        new InventoryCeraShopRuntimeService.TransactionContext(
                            connection,
                            transaction);
                    return InventoryCeraShopRuntimeService.TryBuyCeraShopItem(
                            lease.Inventory,
                            accountId,
                            commodityNo,
                            1,
                            paymentMode,
                            attributeValue,
                            couponItemId,
                            couponSlot,
                            itemOptions,
                            overflowRewardSink,
                            out appliedResult,
                            out appliedFailure,
                            out _,
                            context)
                        && appliedResult != null;
                });

            result = appliedResult;
            failure = appliedFailure;
            return committed;
        }
    }
}
