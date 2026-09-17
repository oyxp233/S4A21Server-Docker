using System;
using System.Linq;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Raid
{
    internal static class RaidRewardCommitService
    {
        internal static bool TryGrantGold(InventoryLease lease, int amount)
        {
            int grantedCount;
            return TryGrantGold(lease, amount, out grantedCount);
        }

        internal static bool TryGrantGold(InventoryLease lease, int amount, out int grantedCount)
        {
            grantedCount = 0;
            if (lease?.Inventory == null || amount <= 0)
            {
                return false;
            }
            int carryLimit = InventoryGoldCarryLimitLoader.Load(lease.Inventory);
            int committedCount = 0;
            bool flag = OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "raid-reward-gold", (SqliteConnection connection, SqliteTransaction transaction) => lease.Inventory.TryGrantGold(amount, carryLimit, out committedCount, out var _) && committedCount > 0);
            grantedCount = (flag ? committedCount : 0);
            return flag;
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
