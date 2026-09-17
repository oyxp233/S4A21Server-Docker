using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon
{
    internal enum AntonAwakeningRewardGrantOutcome
    {
        Failed = 0,
        Granted = 1,
        AlreadyClaimed = 2,
    }

    internal sealed class AntonAwakeningRewardGrantResult
    {
        internal AntonAwakeningRewardGrantResult(
            AntonAwakeningRewardGrantOutcome outcome,
            AntonAwakeningRewardDefinition reward,
            InventoryMutationSet changes = null,
            bool deliveredToMailbox = false)
        {
            Outcome = outcome;
            Reward = reward;
            Changes = changes ?? new InventoryMutationSet();
            DeliveredToMailbox = deliveredToMailbox;
        }

        internal AntonAwakeningRewardGrantOutcome Outcome { get; }
        internal AntonAwakeningRewardDefinition Reward { get; }
        internal InventoryMutationSet Changes { get; }
        internal bool DeliveredToMailbox { get; }
    }

    internal sealed class AntonAwakeningRewardGrantService
    {
        private readonly AntonAwakeningDailyCardService _dailyRewards;
        private readonly IInventoryOverflowRewardSink _overflowRewardSink;

        internal AntonAwakeningRewardGrantService(
            AntonAwakeningDailyCardService dailyRewards,
            IInventoryOverflowRewardSink overflowRewardSink = null)
        {
            _dailyRewards = dailyRewards
                ?? throw new ArgumentNullException(nameof(dailyRewards));
            _overflowRewardSink = overflowRewardSink
                ?? RejectingInventoryOverflowRewardSink.Instance;
        }

        internal AntonAwakeningRewardGrantResult TryGrant(
            InventoryLease lease,
            AntonAwakeningRewardDefinition reward)
        {
            if (lease == null || !reward.IsValid)
            {
                return new AntonAwakeningRewardGrantResult(
                    AntonAwakeningRewardGrantOutcome.Failed,
                    reward);
            }

            var alreadyClaimed = false;
            var deliveredToMailbox = false;
            InventoryRewardGrantResult inventoryResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "anton-awakening-auto-reward",
                (connection, transaction) =>
                {
                    if (!_dailyRewards.TryClaimReward(
                            connection,
                            transaction,
                            lease.CharacterId,
                            reward.GroupKey,
                            reward.RewardableDungeonId))
                    {
                        alreadyClaimed = true;
                        return true;
                    }

                    if (InventoryRewardGrantService.TryCreateAndInsert(
                            lease,
                            reward.ItemId,
                            ItemCreateReason.DungeonDrop,
                            reward.Quantity,
                            out inventoryResult))
                    {
                        return true;
                    }

                    if (inventoryResult?.Error
                        != InventoryRewardGrantError.InsertPlanFailed)
                    {
                        return false;
                    }

                    var transactionSink =
                        new TransactionBoundInventoryOverflowRewardSink(
                            connection,
                            transaction,
                            _overflowRewardSink,
                            "安徒恩特殊翻牌奖励",
                            "背包空间不足，安徒恩特殊翻牌奖励已通过邮件发放。");
                    var overflowRewards =
                        new List<InventoryRewardGrantRequest>
                        {
                            InventoryRewardGrantRequest.Create(
                                reward.ItemId,
                                reward.Quantity,
                                ItemCreateReason.DungeonDrop),
                        };
                    if (!transactionSink.TryDeliver(
                            lease.Inventory,
                            overflowRewards,
                            out _))
                    {
                        return false;
                    }

                    deliveredToMailbox = true;
                    return true;
                });
            if (!committed)
            {
                return new AntonAwakeningRewardGrantResult(
                    AntonAwakeningRewardGrantOutcome.Failed,
                    reward);
            }
            if (alreadyClaimed)
            {
                return new AntonAwakeningRewardGrantResult(
                    AntonAwakeningRewardGrantOutcome.AlreadyClaimed,
                    reward);
            }

            return new AntonAwakeningRewardGrantResult(
                AntonAwakeningRewardGrantOutcome.Granted,
                reward,
                inventoryResult?.Changes,
                deliveredToMailbox);
        }
    }
}
