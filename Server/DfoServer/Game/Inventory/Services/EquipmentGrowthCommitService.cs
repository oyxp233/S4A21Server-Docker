using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    // 龙跃武器成长值的在线提交入口。
    //
    // 成长值是装备实例的持久状态，落库必须与背包 dirty 落库同属一个事务：
    // 这里复用 OnlineInventoryMutationCommitCoordinator，在 lease 锁内
    // 累加成长值并让 SaveDirtyInTransaction 一起提交。
    internal static class EquipmentGrowthCommitService
    {
        internal static bool TryAccumulate(
            InventoryLease lease,
            int growthGain)
        {
            return TryAccumulate(lease, growthGain, out _);
        }

        // stageUpgraded 为 true 表示本次累积触发了星级提升（模板变了），
        // 调用方必须用整栏刷新让客户端重建条目。
        internal static bool TryAccumulate(
            InventoryLease lease,
            int growthGain,
            out bool stageUpgraded)
        {
            stageUpgraded = false;
            if (lease == null
                || lease.Inventory == null
                || growthGain <= 0)
            {
                return false;
            }

            var affected = 0;
            var upgraded = false;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "equipment-growth-accumulate",
                (connection, transaction) =>
                {
                    affected = EquipmentGrowthService.AccumulateForEquippedWeapons(
                        lease.Inventory,
                        connection,
                        transaction,
                        growthGain,
                        out upgraded);
                    return true;
                });
            stageUpgraded = upgraded;

            if (!committed)
                return false;

            if (affected > 0)
            {
                FileLogger.Log(
                    $"[EquipmentGrowth] accumulated growth={growthGain} " +
                    $"cid={lease.CharacterId} weapons={affected}");
            }

            return affected > 0;
        }
    }
}
