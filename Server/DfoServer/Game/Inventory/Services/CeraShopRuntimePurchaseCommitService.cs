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

            // 纯读预检: "已达成扩容档次/已达上限"之类的业务拒绝不进库存提交事务,
            // 避免无效购买每次都走 commit failed + 整包回滚重载的日志路径。
            if (!InventoryCeraShopRuntimeService.TryProbeCeraShopPurchaseEffect(
                    lease.Inventory,
                    commodityNo,
                    out var probeFailure))
            {
                failure = probeFailure;
                if (probeFailure == CeraShopPurchaseFailure.NoEffect)
                {
                    FileLogger.Log(
                        $"[CeraShopRuntime] purchase rejected (no effect) "
                        + $"product={commodityNo} cid={lease.CharacterId} aid={accountId}");
                }

                return false;
            }

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
