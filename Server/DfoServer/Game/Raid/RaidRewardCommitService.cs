using System;
using System.Linq;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Raid
{
    internal static class RaidRewardCommitService
    {
        internal static bool TryGrantGold(
            InventoryLease lease,
            int amount)
        {
            if (lease?.Inventory == null || amount <= 0)
                return false;

            var carryLimit = InventoryGoldCarryLimitLoader.Load(
                lease.Inventory);
            return OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "raid-reward-gold",
                (connection, transaction) =>
                {
                    return lease.Inventory.TryGrantGold(
                            amount,
                            carryLimit,
                            out var granted,
                            out _)
                        && granted > 0;
                });
        }

        internal static bool TryGrantItem(
            InventoryLease lease,
            int itemTemplateId,
            int count,
            out InventorySlotMutation[] changes)
        {
            changes = Array.Empty<InventorySlotMutation>();
            if (lease?.Inventory == null
                || itemTemplateId <= 0
                || count <= 0)
            {
                return false;
            }

            InventorySlotMutation[] committedChanges =
                Array.Empty<InventorySlotMutation>();
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "raid-reward-item",
                (connection, transaction) =>
                {
                    if (!InventoryRewardGrantService.TryCreateAndInsert(
                            lease.Inventory,
                            itemTemplateId,
                            ItemCreateReason.DungeonDrop,
                            count,
                            out var grant)
                        || grant == null
                        || !grant.Success)
                    {
                        return false;
                    }

                    committedChanges = grant.Changes.Slots.ToArray();
                    return true;
                });
            if (!committed)
                return false;

            changes = committedChanges;
            return true;
        }
    }
}
