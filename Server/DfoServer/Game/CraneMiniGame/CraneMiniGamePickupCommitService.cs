using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;

namespace DfoServer.Game.CraneMiniGame
{
    internal sealed class CraneMiniGamePickupCommitResult
    {
        internal InventoryRewardGrantResult Grant { get; set; }

        internal bool DeliveredByMail { get; set; }
    }

    internal static class CraneMiniGamePickupCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            CraneMiniGamePickupReservation reservation,
            MailboxInventoryOverflowRewardSink overflowRewardSink,
            string overflowMailTitle,
            string overflowMailText,
            out CraneMiniGamePickupCommitResult result)
        {
            result = null;
            if (lease?.Inventory == null
                || reservation?.Item == null
                || !reservation.Won
                || overflowRewardSink == null)
            {
                return false;
            }

            InventoryRewardGrantResult appliedGrant = null;
            var deliveredByMail = false;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "crane-minigame-pickup",
                (connection, transaction) =>
                {
                    if (InventoryRewardGrantService.TryCreateAndInsert(
                            lease.Inventory,
                            reservation.Item.ItemId,
                            ItemCreateReason.Unknown,
                            reservation.Item.Count,
                            out appliedGrant)
                        && appliedGrant != null
                        && appliedGrant.Success)
                    {
                        return true;
                    }

                    if (appliedGrant?.Error
                        != InventoryRewardGrantError.InsertPlanFailed)
                    {
                        return false;
                    }

                    var rewards = new[]
                    {
                        InventoryRewardGrantRequest.Create(
                            reservation.Item.ItemId,
                            reservation.Item.Count,
                            ItemCreateReason.Unknown),
                    };
                    deliveredByMail = overflowRewardSink.TryDeliver(
                        connection,
                        transaction,
                        lease.Inventory,
                        rewards,
                        overflowMailTitle,
                        overflowMailText,
                        out _);
                    return deliveredByMail;
                });
            if (!committed)
                return false;

            result = new CraneMiniGamePickupCommitResult
            {
                Grant = deliveredByMail ? null : appliedGrant,
                DeliveredByMail = deliveredByMail,
            };
            return true;
        }
    }
}
