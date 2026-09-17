using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct AntonAwakeningRewardDefinition
    {
        internal AntonAwakeningRewardDefinition(
            int groupKey,
            int rewardableDungeonId,
            int rewardGroupItemId,
            int itemId,
            int quantity,
            int cardState)
        {
            GroupKey = groupKey;
            RewardableDungeonId = rewardableDungeonId;
            RewardGroupItemId = rewardGroupItemId;
            ItemId = itemId;
            Quantity = quantity;
            CardState = cardState;
        }

        internal int GroupKey { get; }
        internal int RewardableDungeonId { get; }
        internal int RewardGroupItemId { get; }
        internal int ItemId { get; }
        internal int Quantity { get; }
        internal int CardState { get; }
        internal int State => CardState;

        internal bool IsValid => GroupKey > 0
            && RewardableDungeonId > 0
            && RewardGroupItemId > 0
            && ItemId > 0
            && Quantity > 0
            && CardState >= 0;
    }

    internal readonly struct AntonAwakeningRewardResolutionFailure
    {
        internal AntonAwakeningRewardResolutionFailure(
            int groupKey,
            int rewardableDungeonId,
            int rewardGroupItemId,
            int finalItemId,
            int cardState,
            string reason)
        {
            GroupKey = groupKey;
            RewardableDungeonId = rewardableDungeonId;
            RewardGroupItemId = rewardGroupItemId;
            FinalItemId = finalItemId;
            CardState = cardState;
            Reason = reason ?? "unknown";
        }

        internal int GroupKey { get; }
        internal int RewardableDungeonId { get; }
        internal int RewardGroupItemId { get; }
        internal int FinalItemId { get; }
        internal int CardState { get; }
        internal string Reason { get; }
    }

    internal sealed class AntonAwakeningParticipantRewardResolution
    {
        private AntonAwakeningParticipantRewardResolution(
            DungeonParticipantRosterEntry participant,
            AntonAwakeningRewardDefinition reward,
            AntonAwakeningRewardResolutionFailure failure,
            bool succeeded)
        {
            Participant = participant
                ?? throw new ArgumentNullException(nameof(participant));
            Reward = reward;
            Failure = failure;
            Succeeded = succeeded;
        }

        internal DungeonParticipantRosterEntry Participant { get; }
        internal AntonAwakeningRewardDefinition Reward { get; }
        internal AntonAwakeningRewardResolutionFailure Failure { get; }
        internal bool Succeeded { get; }

        internal static AntonAwakeningParticipantRewardResolution Success(
            DungeonParticipantRosterEntry participant,
            AntonAwakeningRewardDefinition reward)
            => new AntonAwakeningParticipantRewardResolution(
                participant,
                reward,
                default,
                succeeded: true);

        internal static AntonAwakeningParticipantRewardResolution Failed(
            DungeonParticipantRosterEntry participant,
            AntonAwakeningRewardResolutionFailure failure)
            => new AntonAwakeningParticipantRewardResolution(
                participant,
                default,
                failure,
                succeeded: false);
    }

    internal sealed class AntonAwakeningRewardBatchResolution
    {
        internal AntonAwakeningRewardBatchResolution(
            IReadOnlyList<AntonAwakeningParticipantRewardResolution>
                participants)
        {
            Participants = Array.AsReadOnly((participants
                ?? Array.Empty<AntonAwakeningParticipantRewardResolution>())
                .ToArray());
        }

        internal IReadOnlyList<AntonAwakeningParticipantRewardResolution>
            Participants { get; }
    }

    internal readonly struct AntonAwakeningPreparedRewardEntry
    {
        internal AntonAwakeningPreparedRewardEntry(
            int itemId,
            int weight,
            int quantity)
        {
            ItemId = itemId;
            Weight = weight;
            Quantity = quantity;
        }

        internal int ItemId { get; }
        internal int Weight { get; }
        internal int Quantity { get; }
    }

    internal sealed class AntonAwakeningPreparedRewardGroup
    {
        internal AntonAwakeningPreparedRewardGroup(
            int weight,
            int rewardGroupItemId,
            int cardState,
            int totalInnerWeight,
            IReadOnlyList<AntonAwakeningPreparedRewardEntry> entries)
        {
            Weight = weight;
            RewardGroupItemId = rewardGroupItemId;
            CardState = cardState;
            TotalInnerWeight = totalInnerWeight;
            Entries = Array.AsReadOnly((entries
                ?? Array.Empty<AntonAwakeningPreparedRewardEntry>()).ToArray());
        }

        internal int Weight { get; }
        internal int RewardGroupItemId { get; }
        internal int CardState { get; }
        internal int TotalInnerWeight { get; }
        internal IReadOnlyList<AntonAwakeningPreparedRewardEntry> Entries
        {
            get;
        }
    }

    internal sealed class AntonAwakeningPreparedRewardPools
    {
        internal AntonAwakeningPreparedRewardPools(
            int groupKey,
            int rewardableDungeonId,
            int totalOuterWeight,
            IReadOnlyList<AntonAwakeningPreparedRewardGroup> groups)
        {
            GroupKey = groupKey;
            RewardableDungeonId = rewardableDungeonId;
            TotalOuterWeight = totalOuterWeight;
            Groups = Array.AsReadOnly((groups
                ?? Array.Empty<AntonAwakeningPreparedRewardGroup>()).ToArray());
        }

        internal int GroupKey { get; }
        internal int RewardableDungeonId { get; }
        internal int TotalOuterWeight { get; }
        internal IReadOnlyList<AntonAwakeningPreparedRewardGroup> Groups
        {
            get;
        }
    }

    /// <summary>
    /// Prepares all configured STK pools before random selection and owns the
    /// dynamic daily claim key. Durable state remains in DailyResetService.
    /// </summary>
    internal sealed class AntonAwakeningDailyCardService
    {
        private const string RewardPath = "etc/sequential_dungeon_info.etc";

        private readonly DailyResetService _dailyReset;
        private readonly Func<int, StackableItemFile> _stackableLoader;
        private readonly Func<int, int> _nextRoll;

        internal AntonAwakeningDailyCardService(DailyResetService dailyReset)
            : this(dailyReset, StackableItemProvider.Load, null)
        {
        }

        internal AntonAwakeningDailyCardService(
            DailyResetService dailyReset,
            Func<int, StackableItemFile> stackableLoader,
            Func<int, int> nextRoll = null)
        {
            _dailyReset = dailyReset;
            _stackableLoader = stackableLoader
                ?? throw new ArgumentNullException(nameof(stackableLoader));
            _nextRoll = nextRoll ?? ServerRandom.Next;
        }

        // Validates and copies every outer group's STK pool before any random
        // call. The value-only graph cannot be changed through PvfLib caches.
        internal bool TryPrepareRewardPools(
            SequentialDungeonDefinition definition,
            int rewardableDungeonId,
            out AntonAwakeningPreparedRewardPools prepared,
            out AntonAwakeningRewardResolutionFailure failure)
        {
            prepared = null;
            failure = default;
            var groupKey = definition?.GroupKey ?? 0;
            if (definition == null
                || rewardableDungeonId <= 0
                || !definition.RewardableDungeonIds.Contains(
                    rewardableDungeonId))
            {
                failure = NewFailure(
                    groupKey,
                    rewardableDungeonId,
                    reason: "definition missing or dungeon is not rewardable");
                return false;
            }

            var outer = definition.ClearRewardGroups;
            if (outer == null || outer.Count == 0)
            {
                failure = NewFailure(
                    groupKey,
                    rewardableDungeonId,
                    reason: "clear reward group is empty");
                return false;
            }

            var preparedGroups = new List<AntonAwakeningPreparedRewardGroup>(
                outer.Count);
            var totalOuterWeight = 0L;
            foreach (var group in outer)
            {
                if (group == null
                    || group.Weight <= 0
                    || group.RewardGroupItemId <= 0
                    || group.CardState < 0)
                {
                    failure = NewFailure(
                        groupKey,
                        rewardableDungeonId,
                        group?.RewardGroupItemId ?? 0,
                        group?.CardState ?? -1,
                        reason: "clear reward group contains invalid data");
                    return false;
                }

                totalOuterWeight += group.Weight;
                if (totalOuterWeight > int.MaxValue)
                {
                    failure = NewFailure(
                        groupKey,
                        rewardableDungeonId,
                        group.RewardGroupItemId,
                        group.CardState,
                        reason: "clear reward weight exceeds Int32 capacity");
                    return false;
                }

                StackableItemFile stackable;
                try
                {
                    stackable = _stackableLoader(group.RewardGroupItemId);
                }
                catch (Exception ex)
                {
                    failure = NewFailure(
                        groupKey,
                        rewardableDungeonId,
                        group.RewardGroupItemId,
                        group.CardState,
                        reason: "stackable loader threw "
                            + ex.GetType().Name);
                    return false;
                }

                if (stackable == null)
                {
                    failure = NewFailure(
                        groupKey,
                        rewardableDungeonId,
                        group.RewardGroupItemId,
                        group.CardState,
                        reason: "stackable item is missing");
                    return false;
                }

                var stackableType = StackableItemProvider.NormalizeType(
                    stackable.StackableType);
                if (!string.Equals(
                        stackableType,
                        StackableItemProvider.UpgradableLegacyType,
                        StringComparison.OrdinalIgnoreCase))
                {
                    failure = NewFailure(
                        groupKey,
                        rewardableDungeonId,
                        group.RewardGroupItemId,
                        group.CardState,
                        reason: "stackable type is not upgradable legacy");
                    return false;
                }

                var inner = stackable.UpgradableLegacyRewards;
                if (inner == null || inner.Count == 0)
                {
                    failure = NewFailure(
                        groupKey,
                        rewardableDungeonId,
                        group.RewardGroupItemId,
                        group.CardState,
                        reason: "upgradable legacy rewards are empty");
                    return false;
                }

                var preparedEntries =
                    new List<AntonAwakeningPreparedRewardEntry>(inner.Count);
                var totalInnerWeight = 0L;
                foreach (var candidate in inner)
                {
                    if (candidate == null
                        || candidate.ItemId <= 0
                        || candidate.Weight <= 0
                        || candidate.Count <= 0)
                    {
                        failure = NewFailure(
                            groupKey,
                            rewardableDungeonId,
                            group.RewardGroupItemId,
                            group.CardState,
                            candidate?.ItemId ?? 0,
                            reason: "upgradable legacy entry is invalid");
                        return false;
                    }

                    totalInnerWeight += candidate.Weight;
                    if (totalInnerWeight > int.MaxValue)
                    {
                        failure = NewFailure(
                            groupKey,
                            rewardableDungeonId,
                            group.RewardGroupItemId,
                            group.CardState,
                            candidate.ItemId,
                            reason: "upgradable legacy weight exceeds Int32 capacity");
                        return false;
                    }

                    preparedEntries.Add(
                        new AntonAwakeningPreparedRewardEntry(
                            candidate.ItemId,
                            candidate.Weight,
                            candidate.Count));
                }

                preparedGroups.Add(
                    new AntonAwakeningPreparedRewardGroup(
                        group.Weight,
                        group.RewardGroupItemId,
                        group.CardState,
                        (int)totalInnerWeight,
                        preparedEntries.AsReadOnly()));
            }

            prepared = new AntonAwakeningPreparedRewardPools(
                groupKey,
                rewardableDungeonId,
                (int)totalOuterWeight,
                preparedGroups.AsReadOnly());
            return true;
        }

        // One event planning attempt. Every participant gets a terminal result;
        // each success consumes exactly one outer and one inner roll.
        internal AntonAwakeningRewardBatchResolution ResolveParticipantRewards(
            Guid sourceEventId,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            SequentialDungeonDefinition definition,
            int rewardableDungeonId)
        {
            var participants = (roster
                    ?? Array.Empty<DungeonParticipantRosterEntry>())
                .Where(value => value != null)
                .OrderBy(value => value.PartySlot)
                .ThenBy(value => value.ParticipantUserId)
                .ThenBy(value => value.CharacterId)
                .ToList();
            if (participants.Count == 0)
            {
                return new AntonAwakeningRewardBatchResolution(
                    Array.Empty<AntonAwakeningParticipantRewardResolution>());
            }

            var identities = new HashSet<DungeonParticipantRunIdentity>();
            var userIds = new HashSet<ushort>();
            var rosterIsValid = sourceEventId != Guid.Empty;
            foreach (var participant in participants)
            {
                rosterIsValid = rosterIsValid
                    && participant.RunIdentity.ParticipantIdentity.IsValid
                    && participant.CharacterId > 0
                    && participant.ParticipantUserId > 0
                    && userIds.Add(participant.ParticipantUserId)
                    && identities.Add(
                        participant.RunIdentity.ParticipantIdentity);
            }

            if (!rosterIsValid)
            {
                return FailAll(
                    sourceEventId,
                    participants,
                    NewFailure(
                        definition?.GroupKey ?? 0,
                        rewardableDungeonId,
                        reason: "source event or participant roster is invalid"));
            }

            if (!TryPrepareRewardPools(
                    definition,
                    rewardableDungeonId,
                    out var prepared,
                    out var preparationFailure))
            {
                return FailAll(
                    sourceEventId,
                    participants,
                    preparationFailure);
            }

            var results =
                new List<AntonAwakeningParticipantRewardResolution>(
                    participants.Count);
            foreach (var participant in participants)
            {
                if (TryDrawPreparedReward(
                        prepared,
                        out var reward,
                        out var drawFailure))
                {
                    results.Add(
                        AntonAwakeningParticipantRewardResolution.Success(
                            participant,
                            reward));
                    continue;
                }

                LogFailure(
                    participant.CharacterId,
                    sourceEventId,
                    drawFailure);
                results.Add(
                    AntonAwakeningParticipantRewardResolution.Failed(
                        participant,
                        drawFailure));
            }

            return new AntonAwakeningRewardBatchResolution(
                results.AsReadOnly());
        }

        internal AntonAwakeningRewardBatchResolution FreezeUnexpectedFailure(
            Guid sourceEventId,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            SequentialDungeonDefinition definition,
            int rewardableDungeonId,
            string reason)
        {
            var participants = (roster
                    ?? Array.Empty<DungeonParticipantRosterEntry>())
                .Where(value => value != null)
                .OrderBy(value => value.PartySlot)
                .ThenBy(value => value.ParticipantUserId)
                .ThenBy(value => value.CharacterId)
                .ToList()
                .AsReadOnly();
            return FailAll(
                sourceEventId,
                participants,
                NewFailure(
                    definition?.GroupKey ?? 0,
                    rewardableDungeonId,
                    reason: reason));
        }

        internal bool TryDrawPreparedReward(
            AntonAwakeningPreparedRewardPools prepared,
            out AntonAwakeningRewardDefinition reward,
            out AntonAwakeningRewardResolutionFailure failure)
        {
            reward = default;
            failure = default;
            if (prepared == null
                || prepared.Groups.Count == 0
                || prepared.TotalOuterWeight <= 0)
            {
                failure = NewFailure(
                    prepared?.GroupKey ?? 0,
                    prepared?.RewardableDungeonId ?? 0,
                    reason: "prepared reward pools are invalid");
                return false;
            }

            if (!TryRoll(prepared.TotalOuterWeight, out var outerRoll))
            {
                failure = NewFailure(
                    prepared.GroupKey,
                    prepared.RewardableDungeonId,
                    reason: "outer reward roll is invalid");
                return false;
            }

            AntonAwakeningPreparedRewardGroup selectedGroup = null;
            var remainingOuter = outerRoll;
            foreach (var group in prepared.Groups)
            {
                if (remainingOuter < group.Weight)
                {
                    selectedGroup = group;
                    break;
                }
                remainingOuter -= group.Weight;
            }

            if (selectedGroup == null)
            {
                failure = NewFailure(
                    prepared.GroupKey,
                    prepared.RewardableDungeonId,
                    reason: "outer reward roll selected no group");
                return false;
            }

            if (!TryRoll(selectedGroup.TotalInnerWeight, out var innerRoll))
            {
                failure = NewFailure(
                    prepared.GroupKey,
                    prepared.RewardableDungeonId,
                    selectedGroup.RewardGroupItemId,
                    selectedGroup.CardState,
                    reason: "inner reward roll is invalid");
                return false;
            }

            var selectedEntry = default(AntonAwakeningPreparedRewardEntry);
            var selected = false;
            var remainingInner = innerRoll;
            foreach (var entry in selectedGroup.Entries)
            {
                if (remainingInner < entry.Weight)
                {
                    selectedEntry = entry;
                    selected = true;
                    break;
                }
                remainingInner -= entry.Weight;
            }

            if (!selected)
            {
                failure = NewFailure(
                    prepared.GroupKey,
                    prepared.RewardableDungeonId,
                    selectedGroup.RewardGroupItemId,
                    selectedGroup.CardState,
                    reason: "inner reward roll selected no item");
                return false;
            }

            reward = new AntonAwakeningRewardDefinition(
                prepared.GroupKey,
                prepared.RewardableDungeonId,
                selectedGroup.RewardGroupItemId,
                selectedEntry.ItemId,
                selectedEntry.Quantity,
                selectedGroup.CardState);
            if (reward.IsValid)
                return true;

            failure = NewFailure(
                prepared.GroupKey,
                prepared.RewardableDungeonId,
                selectedGroup.RewardGroupItemId,
                selectedGroup.CardState,
                selectedEntry.ItemId,
                reason: "resolved reward is invalid");
            return false;
        }

        internal bool HasClaimedRewardToday(
            int characterId,
            int groupKey,
            int rewardableDungeonId)
        {
            var key = BuildRewardCounterKey(groupKey, rewardableDungeonId);
            return _dailyReset != null
                && characterId > 0
                && key.Length > 0
                && _dailyReset.IsClaimed(characterId, key);
        }

        internal bool TryClaimReward(
            int characterId,
            int groupKey,
            int rewardableDungeonId)
        {
            var key = BuildRewardCounterKey(groupKey, rewardableDungeonId);
            return _dailyReset != null
                && characterId > 0
                && key.Length > 0
                && _dailyReset.TryIncrementCounter(
                    characterId,
                    key,
                    cap: 1,
                    period: DailyResetService.PeriodDay);
        }

        internal bool TryClaimReward(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int groupKey,
            int rewardableDungeonId)
        {
            var key = BuildRewardCounterKey(groupKey, rewardableDungeonId);
            return _dailyReset != null
                && connection != null
                && transaction != null
                && characterId > 0
                && key.Length > 0
                && _dailyReset.TryIncrementCounter(
                    connection,
                    transaction,
                    characterId,
                    key,
                    cap: 1,
                    period: DailyResetService.PeriodDay);
        }

        internal static string BuildRewardCounterKey(
            int groupKey,
            int rewardableDungeonId)
            => groupKey > 0 && rewardableDungeonId > 0
                ? "sequential_reward_v1:"
                    + groupKey
                    + ":"
                    + rewardableDungeonId
                : string.Empty;

        private bool TryRoll(int maximum, out int roll)
        {
            roll = 0;
            if (maximum <= 0)
                return false;
            try
            {
                roll = _nextRoll(maximum);
                return roll >= 0 && roll < maximum;
            }
            catch
            {
                return false;
            }
        }

        private static AntonAwakeningRewardBatchResolution FailAll(
            Guid sourceEventId,
            IReadOnlyList<DungeonParticipantRosterEntry> participants,
            AntonAwakeningRewardResolutionFailure failure)
        {
            var results =
                new List<AntonAwakeningParticipantRewardResolution>(
                    participants.Count);
            foreach (var participant in participants)
            {
                LogFailure(
                    participant.CharacterId,
                    sourceEventId,
                    failure);
                results.Add(
                    AntonAwakeningParticipantRewardResolution.Failed(
                        participant,
                        failure));
            }
            return new AntonAwakeningRewardBatchResolution(
                results.AsReadOnly());
        }

        private static AntonAwakeningRewardResolutionFailure NewFailure(
            int groupKey,
            int rewardableDungeonId,
            int rewardGroupItemId = 0,
            int cardState = -1,
            int finalItemId = 0,
            string reason = null)
            => new AntonAwakeningRewardResolutionFailure(
                groupKey,
                rewardableDungeonId,
                rewardGroupItemId,
                finalItemId,
                cardState,
                reason);

        private static void LogFailure(
            int characterId,
            Guid sourceEventId,
            AntonAwakeningRewardResolutionFailure failure)
        {
            FileLogger.Log(
                "[AntonAwakeningDailyCardService] reward resolution failed: "
                + $"path={RewardPath} "
                + $"characterId={characterId} "
                + $"sourceEventId={sourceEventId:N} "
                + $"group={failure.GroupKey} "
                + $"rewardableDungeon={failure.RewardableDungeonId} "
                + $"card={failure.CardState} "
                + $"rewardGroup={failure.RewardGroupItemId} "
                + $"final={failure.FinalItemId} "
                + $"reason={failure.Reason}");
        }
    }
}
