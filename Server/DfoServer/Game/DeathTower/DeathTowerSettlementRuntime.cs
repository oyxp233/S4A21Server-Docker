using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.Game.DeathTower
{
    public enum DeathTowerRewardCandidateKind
    {
        Empty = 0,
        Gold = 1,
        Item = 2,
    }

    public readonly struct DeathTowerRewardCandidate
    {
        private DeathTowerRewardCandidate(
            DeathTowerRewardCandidateKind kind,
            int itemId,
            int addInfo)
        {
            Kind = kind;
            ItemId = itemId;
            AddInfo = addInfo;
        }

        public DeathTowerRewardCandidateKind Kind { get; }
        public int ItemId { get; }
        public int AddInfo { get; }

        public static DeathTowerRewardCandidate Empty() =>
            new DeathTowerRewardCandidate(
                DeathTowerRewardCandidateKind.Empty,
                -1,
                0);

        public static DeathTowerRewardCandidate Gold(int amount) =>
            new DeathTowerRewardCandidate(
                DeathTowerRewardCandidateKind.Gold,
                0,
                Math.Max(0, amount));

        public static DeathTowerRewardCandidate Item(int itemId, int count)
        {
            if (itemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(itemId));
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            return new DeathTowerRewardCandidate(
                DeathTowerRewardCandidateKind.Item,
                itemId,
                count);
        }
    }

    internal readonly struct DeathTowerSettlementContext
    {
        internal DeathTowerSettlementContext(
            Guid sourceEventId,
            int characterId,
            int accountId,
            byte level,
            uint exp,
            byte difficulty)
        {
            if (sourceEventId == Guid.Empty)
                throw new ArgumentException(
                    "A settlement source event ID is required.",
                    nameof(sourceEventId));
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(accountId));

            SourceEventId = sourceEventId;
            CharacterId = characterId;
            AccountId = accountId;
            Level = level;
            Exp = exp;
            Difficulty = difficulty;
        }

        internal Guid SourceEventId { get; }
        internal int CharacterId { get; }
        internal int AccountId { get; }
        internal byte Level { get; }
        internal uint Exp { get; }
        internal byte Difficulty { get; }
    }

    internal sealed class DeathTowerSettlementPlan
    {
        internal DeathTowerSettlementPlan(
            DeathTowerSettlementContext context,
            int dungeonId,
            int clearedFloorCount,
            int clearTimeMilliseconds,
            uint rewardExp,
            IReadOnlyList<DeathTowerRewardCandidate> candidates)
        {
            if (dungeonId <= 0)
                throw new ArgumentOutOfRangeException(nameof(dungeonId));
            if (clearedFloorCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(clearedFloorCount));
            if (clearTimeMilliseconds < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(clearTimeMilliseconds));

            Context = context;
            DungeonId = dungeonId;
            ClearedFloorCount = clearedFloorCount;
            ClearTimeMilliseconds = clearTimeMilliseconds;
            RewardExp = rewardExp;
            Candidates = Freeze(candidates);
        }

        internal DeathTowerSettlementContext Context { get; }
        internal Guid SourceEventId => Context.SourceEventId;
        internal int DungeonId { get; }
        internal int ClearedFloorCount { get; }
        internal int ClearTimeMilliseconds { get; }
        internal uint RewardExp { get; }
        internal IReadOnlyList<DeathTowerRewardCandidate> Candidates { get; }

        private static IReadOnlyList<DeathTowerRewardCandidate> Freeze(
            IReadOnlyList<DeathTowerRewardCandidate> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<DeathTowerRewardCandidate>();

            var copy = new DeathTowerRewardCandidate[source.Count];
            for (var index = 0; index < source.Count; index++)
                copy[index] = source[index];
            return new ReadOnlyCollection<DeathTowerRewardCandidate>(copy);
        }
    }

    internal enum DeathTowerSettlementPhase
    {
        Prepared = 0,
        RankingShown = 1,
        RewardShown = 2,
        EplpShown = 3,
        Committing = 4,
        Committed = 5,
        Ending = 6,
    }

    internal sealed class DeathTowerSettlementRuntime
    {
        private readonly object _syncRoot = new object();
        private DeathTowerSettlementPhase _phase;
        private bool? _allMembersHaveEplpItem;
        private DeathTowerSettlementResult _commitResult;
        private bool _experienceProjectionSent;
        private bool _levelUpFollowupsSent;
        private bool _inventoryProjectionSent;

        internal DeathTowerSettlementRuntime(DeathTowerSettlementPlan plan)
        {
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            _phase = DeathTowerSettlementPhase.Prepared;
        }

        internal DeathTowerSettlementPlan Plan { get; }

        internal DeathTowerSettlementPhase Phase
        {
            get { lock (_syncRoot) return _phase; }
        }

        internal DateTime RewardDeadlineUtc { get; private set; }
        internal DateTime EplpDeadlineUtc { get; private set; }
        internal DateTime ReturnDeadlineUtc { get; private set; }

        internal bool? AllMembersHaveEplpItem
        {
            get { lock (_syncRoot) return _allMembersHaveEplpItem; }
        }

        internal DeathTowerSettlementResult CommitResult
        {
            get { lock (_syncRoot) return _commitResult; }
        }

        internal bool ExperienceProjectionSent
        {
            get { lock (_syncRoot) return _experienceProjectionSent; }
        }

        internal bool InventoryProjectionSent
        {
            get { lock (_syncRoot) return _inventoryProjectionSent; }
        }

        internal bool LevelUpFollowupsSent
        {
            get { lock (_syncRoot) return _levelUpFollowupsSent; }
        }

        internal bool IsCommitted
        {
            get
            {
                lock (_syncRoot)
                {
                    return _phase == DeathTowerSettlementPhase.Committed
                        || _phase == DeathTowerSettlementPhase.Ending;
                }
            }
        }

        internal bool TryMarkRankingShown(DateTime rewardDeadlineUtc)
        {
            lock (_syncRoot)
            {
                if (_phase != DeathTowerSettlementPhase.Prepared)
                    return false;
                RewardDeadlineUtc = NormalizeUtc(rewardDeadlineUtc);
                _phase = DeathTowerSettlementPhase.RankingShown;
                return true;
            }
        }

        internal bool TryMarkRewardShown(DateTime eplpDeadlineUtc)
        {
            lock (_syncRoot)
            {
                if (_phase != DeathTowerSettlementPhase.RankingShown)
                    return false;
                EplpDeadlineUtc = NormalizeUtc(eplpDeadlineUtc);
                _phase = DeathTowerSettlementPhase.RewardShown;
                return true;
            }
        }

        internal bool TryFreezeEplpState(bool allMembersHaveRequiredItem)
        {
            lock (_syncRoot)
            {
                if (_phase != DeathTowerSettlementPhase.RewardShown)
                    return false;
                if (_allMembersHaveEplpItem.HasValue)
                    return _allMembersHaveEplpItem.Value
                        == allMembersHaveRequiredItem;
                _allMembersHaveEplpItem = allMembersHaveRequiredItem;
                return true;
            }
        }

        internal bool TryMarkEplpShown()
        {
            lock (_syncRoot)
            {
                if (_phase != DeathTowerSettlementPhase.RewardShown
                    || !_allMembersHaveEplpItem.HasValue)
                {
                    return false;
                }
                _phase = DeathTowerSettlementPhase.EplpShown;
                return true;
            }
        }

        internal bool TryBeginCommit()
        {
            lock (_syncRoot)
            {
                if (_phase != DeathTowerSettlementPhase.EplpShown)
                    return false;
                _phase = DeathTowerSettlementPhase.Committing;
                return true;
            }
        }

        internal bool TryAbortCommit()
        {
            lock (_syncRoot)
            {
                if (_phase != DeathTowerSettlementPhase.Committing)
                    return false;
                _phase = DeathTowerSettlementPhase.EplpShown;
                return true;
            }
        }

        internal bool TryCompleteCommit(DeathTowerSettlementResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            lock (_syncRoot)
            {
                if (_phase != DeathTowerSettlementPhase.Committing)
                    return false;
                _commitResult = result;
                _phase = DeathTowerSettlementPhase.Committed;
                return true;
            }
        }

        internal bool TryScheduleReturn(DateTime returnDeadlineUtc)
        {
            lock (_syncRoot)
            {
                if (_phase != DeathTowerSettlementPhase.EplpShown
                    && _phase != DeathTowerSettlementPhase.Committing
                    && _phase != DeathTowerSettlementPhase.Committed
                    && _phase != DeathTowerSettlementPhase.Ending)
                {
                    return false;
                }
                ReturnDeadlineUtc = NormalizeUtc(returnDeadlineUtc);
                if (_phase == DeathTowerSettlementPhase.Committed)
                    _phase = DeathTowerSettlementPhase.Ending;
                return true;
            }
        }

        internal bool TryMarkExperienceProjectionSent()
        {
            lock (_syncRoot)
            {
                if (!IsCommittedCore() || _experienceProjectionSent)
                    return false;
                _experienceProjectionSent = true;
                return true;
            }
        }

        internal bool TryMarkInventoryProjectionSent()
        {
            lock (_syncRoot)
            {
                if (!IsCommittedCore() || _inventoryProjectionSent)
                    return false;
                _inventoryProjectionSent = true;
                return true;
            }
        }

        internal bool TryMarkLevelUpFollowupsSent()
        {
            lock (_syncRoot)
            {
                if (!IsCommittedCore() || _levelUpFollowupsSent)
                    return false;
                _levelUpFollowupsSent = true;
                return true;
            }
        }

        private bool IsCommittedCore() =>
            _phase == DeathTowerSettlementPhase.Committed
            || _phase == DeathTowerSettlementPhase.Ending;

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value == DateTime.MinValue)
                return value;
            return value.Kind == DateTimeKind.Utc
                ? value
                : value.ToUniversalTime();
        }
    }

    internal readonly struct DeathTowerEplpCommand
    {
        internal DeathTowerEplpCommand(byte state, byte option)
        {
            State = state;
            Option = option;
        }

        internal byte State { get; }
        internal byte Option { get; }
    }

    internal static class DeathTowerEplpCommandRules
    {
        internal static bool TryResolveReturnDelay(
            DeathTowerEplpCommand command,
            out TimeSpan delay,
            out bool keepSelection)
        {
            delay = TimeSpan.Zero;
            keepSelection = false;
            if (command.State != 1)
                return false;

            switch (command.Option)
            {
                case 0:
                case 1:
                    delay = TimeSpan.FromSeconds(3);
                    return true;
                case 2:
                    delay = TimeSpan.FromSeconds(1);
                    return true;
                case 3:
                    keepSelection = true;
                    return true;
                default:
                    return false;
            }
        }
    }
}
