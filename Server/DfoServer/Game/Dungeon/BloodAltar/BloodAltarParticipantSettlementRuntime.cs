using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon.BloodAltar
{
    internal enum BloodAltarSettlementPhase
    {
        Prepared,
        RankingShown,
        RewardShown,
        Committing,
        Committed,
        ExitReady,
        Ending,
        Ended,
    }

    internal sealed class BloodAltarSettlementPlan
    {
        internal BloodAltarSettlementPlan(
            int completedRounds,
            int maxRounds,
            int clearTimeMilliseconds,
            uint rewardExperience,
            IReadOnlyList<ClearRewardGenerator.CardReward> rewards)
        {
            CompletedRounds = Math.Max(0, completedRounds);
            MaxRounds = Math.Max(0, maxRounds);
            ClearTimeMilliseconds = Math.Max(0, clearTimeMilliseconds);
            RewardExperience = rewardExperience;
            Rewards = new ReadOnlyCollection<ClearRewardGenerator.CardReward>(
                CopyRewards(rewards));

            long total = 0;
            foreach (var reward in Rewards)
            {
                if (reward.IsGold)
                {
                    total = Math.Min(
                        int.MaxValue,
                        total + Math.Max(0, reward.GoldAmount));
                }
            }
            TotalGold = (int)total;
        }

        internal int CompletedRounds { get; }
        internal int MaxRounds { get; }
        internal int ClearTimeMilliseconds { get; }
        internal uint RewardExperience { get; }
        internal IReadOnlyList<ClearRewardGenerator.CardReward> Rewards { get; }
        internal int TotalGold { get; }

        private static List<ClearRewardGenerator.CardReward> CopyRewards(
            IReadOnlyList<ClearRewardGenerator.CardReward> rewards)
        {
            var result = new List<ClearRewardGenerator.CardReward>(
                rewards?.Count ?? 0);
            if (rewards == null)
                return result;
            foreach (var reward in rewards)
            {
                if (reward.IsGold)
                {
                    result.Add(new ClearRewardGenerator.CardReward
                    {
                        IsGold = true,
                        GoldAmount = Math.Max(0, reward.GoldAmount),
                    });
                }
                else if (reward.ItemId > 0 && reward.StackCount > 0)
                {
                    result.Add(new ClearRewardGenerator.CardReward
                    {
                        IsGold = false,
                        ItemId = reward.ItemId,
                        StackCount = reward.StackCount,
                        IsEquipment = reward.IsEquipment,
                        Durability = reward.Durability,
                    });
                }
                else
                {
                    result.Add(new ClearRewardGenerator.CardReward
                    {
                        IsGold = false,
                        ItemId = -1,
                    });
                }
            }
            return result;
        }
    }

    internal sealed class BloodAltarRewardCommitResult
    {
        internal BloodAltarRewardCommitResult(
            int requestedGold,
            int grantedGold,
            int finalGold,
            IReadOnlyList<InventorySlotMutation> changes,
            int mailedRewardCount = 0)
        {
            RequestedGold = Math.Max(0, requestedGold);
            GrantedGold = Math.Max(0, grantedGold);
            FinalGold = Math.Max(0, finalGold);
            MailedRewardCount = Math.Max(0, mailedRewardCount);
            Changes = new ReadOnlyCollection<InventorySlotMutation>(
                new List<InventorySlotMutation>(
                    changes ?? Array.Empty<InventorySlotMutation>()));
        }

        internal int RequestedGold { get; }
        internal int GrantedGold { get; }
        internal int FinalGold { get; }
        internal int MailedRewardCount { get; }
        internal IReadOnlyList<InventorySlotMutation> Changes { get; }
    }

    internal sealed class BloodAltarParticipantSettlementRuntime
    {
        private readonly object _syncRoot = new object();
        private BloodAltarSettlementPhase _phase =
            BloodAltarSettlementPhase.Prepared;
        private BloodAltarRewardCommitResult _commitResult;
        private bool _experienceProjectionSent;
        private bool _levelUpProjectionSent;
        private bool _inventoryProjectionSent;
        private bool _exitReadyProjectionSent;
        private bool _exitStarted;
        private BloodAltarEplpCommand? _pendingExitIntent;

        internal BloodAltarParticipantSettlementRuntime(
            BloodAltarSettlementPlan plan)
        {
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        }

        internal BloodAltarSettlementPlan Plan { get; }

        internal BloodAltarSettlementPhase Phase
        {
            get { lock (_syncRoot) return _phase; }
        }

        internal DateTime RankingDeadlineUtc { get; private set; }
        internal DateTime RewardDeadlineUtc { get; private set; }
        internal DateTime ReturnDeadlineUtc { get; private set; }

        internal BloodAltarRewardCommitResult CommitResult
        {
            get { lock (_syncRoot) return _commitResult; }
        }

        internal bool ExperienceProjectionSent
        {
            get { lock (_syncRoot) return _experienceProjectionSent; }
        }

        internal bool LevelUpProjectionSent
        {
            get { lock (_syncRoot) return _levelUpProjectionSent; }
        }

        internal bool InventoryProjectionSent
        {
            get { lock (_syncRoot) return _inventoryProjectionSent; }
        }

        internal bool ExitReadyProjectionSent
        {
            get { lock (_syncRoot) return _exitReadyProjectionSent; }
        }

        internal bool TryMarkRankingShown(DateTime deadlineUtc)
        {
            lock (_syncRoot)
            {
                if (_phase != BloodAltarSettlementPhase.Prepared)
                    return false;
                RankingDeadlineUtc = NormalizeUtc(deadlineUtc);
                _phase = BloodAltarSettlementPhase.RankingShown;
                return true;
            }
        }

        internal bool TryMarkRewardShown(DateTime deadlineUtc)
        {
            lock (_syncRoot)
            {
                if (_phase != BloodAltarSettlementPhase.RankingShown)
                    return false;
                RewardDeadlineUtc = NormalizeUtc(deadlineUtc);
                _phase = BloodAltarSettlementPhase.RewardShown;
                return true;
            }
        }

        internal bool TryBeginCommit()
        {
            lock (_syncRoot)
            {
                if (_phase != BloodAltarSettlementPhase.RewardShown)
                    return false;
                _phase = BloodAltarSettlementPhase.Committing;
                return true;
            }
        }

        internal bool TryAbortCommit()
        {
            lock (_syncRoot)
            {
                if (_phase != BloodAltarSettlementPhase.Committing)
                    return false;
                _phase = BloodAltarSettlementPhase.RewardShown;
                return true;
            }
        }

        internal bool TryCompleteCommit(
            BloodAltarRewardCommitResult result)
        {
            if (result == null)
                return false;
            lock (_syncRoot)
            {
                if (_phase != BloodAltarSettlementPhase.Committing)
                    return false;
                _commitResult = result;
                _phase = BloodAltarSettlementPhase.Committed;
                return true;
            }
        }

        internal void ProjectCommitted(BloodAltarRewardCommitResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));
            lock (_syncRoot)
            {
                if (_phase == BloodAltarSettlementPhase.Ending
                    || _phase == BloodAltarSettlementPhase.Ended)
                {
                    return;
                }
                _commitResult = result;
                if (_phase != BloodAltarSettlementPhase.ExitReady)
                    _phase = BloodAltarSettlementPhase.Committed;
            }
        }

        internal bool TryMarkExperienceProjectionSent()
            => TrySetOnce(ref _experienceProjectionSent);

        internal bool TryMarkLevelUpProjectionSent()
            => TrySetOnce(ref _levelUpProjectionSent);

        internal bool TryMarkInventoryProjectionSent()
            => TrySetOnce(ref _inventoryProjectionSent);

        internal bool TryQueueExitIntent(BloodAltarEplpCommand command)
        {
            if (!command.RequestsExit)
                return false;
            lock (_syncRoot)
            {
                if (_phase == BloodAltarSettlementPhase.Ending
                    || _phase == BloodAltarSettlementPhase.Ended)
                {
                    return false;
                }
                if (_pendingExitIntent.HasValue)
                    return true;
                _pendingExitIntent = command;
                return true;
            }
        }

        internal bool TryBeginPendingExit(out BloodAltarEplpCommand command)
        {
            lock (_syncRoot)
            {
                command = default;
                if (_phase != BloodAltarSettlementPhase.ExitReady
                    || _exitStarted
                    || !_pendingExitIntent.HasValue)
                {
                    return false;
                }
                command = _pendingExitIntent.Value;
                _pendingExitIntent = null;
                _exitStarted = true;
                _phase = BloodAltarSettlementPhase.Ending;
                return true;
            }
        }

        internal bool TryMarkExitReadyProjectionSent(DateTime returnDeadlineUtc)
        {
            lock (_syncRoot)
            {
                if (_phase != BloodAltarSettlementPhase.Committed
                    || _exitReadyProjectionSent)
                {
                    return false;
                }
                _exitReadyProjectionSent = true;
                ReturnDeadlineUtc = NormalizeUtc(returnDeadlineUtc);
                _phase = BloodAltarSettlementPhase.ExitReady;
                return true;
            }
        }

        internal bool TryBeginExit()
        {
            lock (_syncRoot)
            {
                if (_phase != BloodAltarSettlementPhase.ExitReady
                    || _exitStarted)
                {
                    return false;
                }
                _pendingExitIntent = null;
                _exitStarted = true;
                _phase = BloodAltarSettlementPhase.Ending;
                return true;
            }
        }

        internal bool TryMarkEnded()
        {
            lock (_syncRoot)
            {
                if (_phase == BloodAltarSettlementPhase.Ended)
                    return false;
                if (_phase != BloodAltarSettlementPhase.Ending)
                    return false;
                _phase = BloodAltarSettlementPhase.Ended;
                return true;
            }
        }

        internal bool TryAbortExit()
        {
            lock (_syncRoot)
            {
                if (_phase != BloodAltarSettlementPhase.Ending)
                    return false;
                _exitStarted = false;
                _phase = BloodAltarSettlementPhase.ExitReady;
                return true;
            }
        }

        private bool TrySetOnce(ref bool field)
        {
            lock (_syncRoot)
            {
                if (field)
                    return false;
                field = true;
                return true;
            }
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value == DateTime.MinValue)
                return DateTime.MinValue;
            if (value.Kind == DateTimeKind.Utc)
                return value;
            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }

    internal readonly struct BloodAltarEplpCommand
    {
        internal BloodAltarEplpCommand(byte state, byte option)
        {
            State = state;
            Option = option;
        }

        internal byte State { get; }
        internal byte Option { get; }
        internal bool RequestsExit => State == 1;
    }
}
