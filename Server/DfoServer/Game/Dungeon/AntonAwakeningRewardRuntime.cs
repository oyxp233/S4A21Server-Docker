using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DfoServer.GameWorld;

namespace DfoServer.Game.Dungeon
{
    internal enum AntonPaidCardState
    {
        Pending = 0,
        Committed = 1,
        Skipped = 2,
    }

    internal sealed class AntonNormalCardParticipantState
    {
        internal bool FreeCommitted { get; set; }
        internal AntonPaidCardState PaidState { get; set; }
        internal bool PaidSelectionInFlight { get; set; }
        internal int PaidSelectionGeneration { get; set; }
        internal DateTime DeadlineUtc { get; set; }
        internal bool DeadlineElapsed { get; set; }
    }

    internal readonly struct AntonPaidSelectionTicket
    {
        internal AntonPaidSelectionTicket(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            int generation)
        {
            SourceEventId = sourceEventId;
            Participant = participant;
            Generation = generation;
        }

        internal Guid SourceEventId { get; }
        internal DungeonParticipantRunIdentity Participant { get; }
        internal int Generation { get; }
        internal bool IsValid => SourceEventId != Guid.Empty
            && Participant.IsValid
            && Generation != 0;
    }

    internal sealed class AntonAwakeningRewardPlanEntry
    {
        internal AntonAwakeningRewardPlanEntry(
            DungeonParticipantRosterEntry participant,
            AntonAwakeningRewardDefinition reward)
        {
            Participant = participant
                ?? throw new ArgumentNullException(nameof(participant));
            if (!reward.IsValid)
                throw new ArgumentException("A valid reward is required.", nameof(reward));
            Reward = reward;
        }

        internal DungeonParticipantRosterEntry Participant { get; }
        internal AntonAwakeningRewardDefinition Reward { get; }
    }

    internal sealed class AntonAwakeningRewardPlan
    {
        internal AntonAwakeningRewardPlan(
            Guid sourceEventId,
            IReadOnlyList<AntonAwakeningRewardPlanEntry> entries)
        {
            if (sourceEventId == Guid.Empty)
                throw new ArgumentException("A source event is required.", nameof(sourceEventId));
            SourceEventId = sourceEventId;
            Entries = entries ?? Array.Empty<AntonAwakeningRewardPlanEntry>();
        }

        internal Guid SourceEventId { get; }
        internal IReadOnlyList<AntonAwakeningRewardPlanEntry> Entries { get; }
    }

    internal sealed class AntonAwakeningRewardPlanCreationOutcome
    {
        internal AntonAwakeningRewardPlanCreationOutcome(
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardBatchResolution resolution)
        {
            Plan = plan;
            Resolution = resolution
                ?? new AntonAwakeningRewardBatchResolution(
                    Array.Empty<AntonAwakeningParticipantRewardResolution>());
        }

        internal AntonAwakeningRewardPlan Plan { get; }
        internal AntonAwakeningRewardBatchResolution Resolution { get; }
    }

    // Event-scoped reservation for one potentially expensive planning pass.
    // Evaluation is deliberately separate from publication so the coordinator
    // can run PVF/STK work outside the instance gate and revalidate the run
    // before exposing the frozen outcome to projection/grant consumers.
    internal sealed class AntonAwakeningRewardPlanCreation
    {
        private readonly Lazy<AntonAwakeningRewardPlanCreationOutcome>
            _evaluation;
        private AntonAwakeningRewardPlanCreationOutcome _evaluated;
        private AntonAwakeningRewardPlanCreationOutcome _published;

        internal AntonAwakeningRewardPlanCreation(
            Guid sourceEventId,
            Func<AntonAwakeningRewardPlanCreationOutcome> factory)
        {
            SourceEventId = sourceEventId;
            _evaluation = new Lazy<AntonAwakeningRewardPlanCreationOutcome>(
                factory ?? throw new ArgumentNullException(nameof(factory)),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal Guid SourceEventId { get; }

        internal AntonAwakeningRewardPlanCreationOutcome Evaluate()
        {
            var outcome = _evaluation.Value;
            Interlocked.CompareExchange(ref _evaluated, outcome, null);
            return outcome;
        }

        internal bool TryPublish(
            AntonAwakeningRewardPlanCreationOutcome outcome)
        {
            if (outcome == null
                || !ReferenceEquals(Volatile.Read(ref _evaluated), outcome))
            {
                return false;
            }

            var existing = Interlocked.CompareExchange(
                ref _published,
                outcome,
                null);
            return existing == null || ReferenceEquals(existing, outcome);
        }

        internal bool TryGetPublished(
            out AntonAwakeningRewardPlanCreationOutcome outcome)
        {
            outcome = Volatile.Read(ref _published);
            return outcome != null;
        }
    }

    // Instance-owned, in-process plan/result state. Runtime methods only
    // register, publish or read creation reservations while holding _syncRoot;
    // the caller must evaluate a reservation outside every aggregate gate.
    internal sealed class AntonAwakeningRewardRuntime
    {
        private sealed class NormalCardBarrierState
        {
            internal NormalCardBarrierState(
                IReadOnlyList<DungeonParticipantRosterEntry> roster)
            {
                Roster = roster;
                Participants = roster.ToDictionary(
                    value => value.RunIdentity.ParticipantIdentity,
                    _ => new AntonNormalCardParticipantState());
            }

            internal IReadOnlyList<DungeonParticipantRosterEntry> Roster
            {
                get;
            }

            internal Dictionary<
                DungeonParticipantRunIdentity,
                AntonNormalCardParticipantState> Participants { get; }

            internal DateTime DeadlineUtc { get; set; }
            internal bool NormalCardPhaseClosed { get; set; }
            internal bool SpecialProjectionReady { get; set; }
        }

        private readonly object _syncRoot = new object();
        private readonly Dictionary<Guid, AntonAwakeningRewardPlanCreation>
            _planCreations =
                new Dictionary<Guid, AntonAwakeningRewardPlanCreation>();
        private readonly Dictionary<(
            Guid SourceEventId,
            DungeonParticipantRunIdentity Participant),
            AntonAwakeningRewardGrantResult> _committed =
                new Dictionary<(
                    Guid,
                    DungeonParticipantRunIdentity),
                    AntonAwakeningRewardGrantResult>();
        private readonly Dictionary<(
            Guid SourceEventId,
            DungeonParticipantRunIdentity Participant),
            DateTime> _projectionDeadlinesUtc =
                new Dictionary<(
                    Guid,
                    DungeonParticipantRunIdentity),
                    DateTime>();
        private readonly Dictionary<Guid, NormalCardBarrierState>
            _normalCardBarriers =
                new Dictionary<Guid, NormalCardBarrierState>();

        internal bool TryRegisterNormalCardBarrier(
            Guid sourceEventId,
            IReadOnlyList<DungeonParticipantRosterEntry> roster)
        {
            if (sourceEventId == Guid.Empty
                || roster == null
                || roster.Count == 0
                || roster.Any(value => value == null
                    || !value.RunIdentity.ParticipantIdentity.IsValid))
            {
                return false;
            }

            var frozen = roster.ToList().AsReadOnly();
            if (frozen.Select(value =>
                    value.RunIdentity.ParticipantIdentity)
                .Distinct()
                .Count() != frozen.Count)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (_normalCardBarriers.TryGetValue(
                        sourceEventId,
                        out var existing))
                {
                    return SameRoster(existing.Roster, frozen);
                }

                _normalCardBarriers.Add(
                    sourceEventId,
                    new NormalCardBarrierState(frozen));
                return true;
            }
        }

        internal bool TryGetNormalCardRoster(
            Guid sourceEventId,
            out IReadOnlyList<DungeonParticipantRosterEntry> roster)
        {
            lock (_syncRoot)
            {
                if (_normalCardBarriers.TryGetValue(
                        sourceEventId,
                        out var barrier))
                {
                    roster = barrier.Roster;
                    return true;
                }
            }

            roster = Array.Empty<DungeonParticipantRosterEntry>();
            return false;
        }

        internal bool TryRecordNormalDeadline(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            DateTime deadlineUtc)
        {
            deadlineUtc = NormalizeUtc(deadlineUtc);
            if (sourceEventId == Guid.Empty
                || !participant.IsValid
                || deadlineUtc == DateTime.MinValue)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_normalCardBarriers.TryGetValue(
                        sourceEventId,
                        out var barrier)
                    || barrier.NormalCardPhaseClosed
                    || !barrier.Participants.ContainsKey(participant))
                {
                    return false;
                }

                if (barrier.DeadlineUtc == DateTime.MinValue)
                {
                    barrier.DeadlineUtc = deadlineUtc;
                    foreach (var state in barrier.Participants.Values)
                        state.DeadlineUtc = deadlineUtc;
                }
                return true;
            }
        }

        internal bool TryGetNormalDeadline(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            out DateTime deadlineUtc)
        {
            lock (_syncRoot)
            {
                if (_normalCardBarriers.TryGetValue(
                        sourceEventId,
                        out var barrier)
                    && barrier.Participants.TryGetValue(
                        participant,
                        out var state)
                    && state.DeadlineUtc != DateTime.MinValue)
                {
                    deadlineUtc = state.DeadlineUtc;
                    return true;
                }
            }

            deadlineUtc = DateTime.MinValue;
            return false;
        }

        internal bool IsNormalCardPhaseClosed(Guid sourceEventId)
        {
            lock (_syncRoot)
            {
                return _normalCardBarriers.TryGetValue(
                           sourceEventId,
                           out var barrier)
                    && barrier.NormalCardPhaseClosed;
            }
        }

        internal bool TryBeginPaidSelection(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            out AntonPaidSelectionTicket ticket)
        {
            ticket = default;
            lock (_syncRoot)
            {
                if (!_normalCardBarriers.TryGetValue(
                        sourceEventId,
                        out var barrier)
                    || barrier.NormalCardPhaseClosed
                    || !barrier.Participants.TryGetValue(
                        participant,
                        out var state)
                    || state.PaidState != AntonPaidCardState.Pending
                    || state.PaidSelectionInFlight)
                {
                    return false;
                }

                state.PaidSelectionGeneration = NextGeneration(
                    state.PaidSelectionGeneration);
                state.PaidSelectionInFlight = true;
                ticket = new AntonPaidSelectionTicket(
                    sourceEventId,
                    participant,
                    state.PaidSelectionGeneration);
                return true;
            }
        }

        internal bool TryCancelPaidSelection(
            AntonPaidSelectionTicket ticket)
        {
            if (!ticket.IsValid)
                return false;

            lock (_syncRoot)
            {
                if (!_normalCardBarriers.TryGetValue(
                        ticket.SourceEventId,
                        out var barrier)
                    || !barrier.Participants.TryGetValue(
                        ticket.Participant,
                        out var state)
                    || state.PaidState != AntonPaidCardState.Pending
                    || !state.PaidSelectionInFlight
                    || state.PaidSelectionGeneration != ticket.Generation)
                {
                    return false;
                }

                state.PaidSelectionInFlight = false;
                if (barrier.NormalCardPhaseClosed)
                    state.PaidState = AntonPaidCardState.Skipped;
                return true;
            }
        }

        internal bool TryRecoverPaidSelection(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant)
        {
            lock (_syncRoot)
            {
                if (!_normalCardBarriers.TryGetValue(
                        sourceEventId,
                        out var barrier)
                    || !barrier.Participants.TryGetValue(
                        participant,
                        out var state)
                    || state.PaidState != AntonPaidCardState.Pending)
                {
                    return false;
                }

                if (state.PaidSelectionInFlight)
                {
                    state.PaidSelectionInFlight = false;
                    state.PaidSelectionGeneration = NextGeneration(
                        state.PaidSelectionGeneration);
                }
                if (barrier.NormalCardPhaseClosed)
                    state.PaidState = AntonPaidCardState.Skipped;
                return true;
            }
        }

        internal bool TryMarkCardCommitted(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            CardRewardSide side)
        {
            lock (_syncRoot)
            {
                if (!_normalCardBarriers.TryGetValue(
                        sourceEventId,
                        out var barrier)
                    || !barrier.Participants.TryGetValue(
                        participant,
                        out var state))
                {
                    return false;
                }

                if (side == CardRewardSide.Free)
                    state.FreeCommitted = true;
                else
                {
                    state.PaidState = AntonPaidCardState.Committed;
                    state.PaidSelectionInFlight = false;
                }
                return true;
            }
        }

        internal bool TryMarkDeadlineElapsed(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant)
        {
            lock (_syncRoot)
            {
                if (!_normalCardBarriers.TryGetValue(
                        sourceEventId,
                        out var barrier)
                    || !barrier.Participants.ContainsKey(participant))
                {
                    return false;
                }

                barrier.NormalCardPhaseClosed = true;
                foreach (var state in barrier.Participants.Values)
                {
                    state.DeadlineElapsed = true;
                    if (state.PaidState == AntonPaidCardState.Pending
                        && !state.PaidSelectionInFlight)
                    {
                        state.PaidState = AntonPaidCardState.Skipped;
                    }
                }
                return true;
            }
        }

        internal bool IsPaidSelectionOpen(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant)
        {
            lock (_syncRoot)
            {
                return _normalCardBarriers.TryGetValue(
                           sourceEventId,
                           out var barrier)
                    && !barrier.NormalCardPhaseClosed
                    && barrier.Participants.TryGetValue(
                        participant,
                        out var state)
                    && state.PaidState == AntonPaidCardState.Pending
                    && !state.PaidSelectionInFlight;
            }
        }

        internal bool AreAllCurrentParticipantsReady(
            Guid sourceEventId,
            Func<DungeonParticipantRosterEntry, bool> isCurrent)
        {
            if (isCurrent == null)
                return false;

            IReadOnlyList<DungeonParticipantRosterEntry> roster;
            lock (_syncRoot)
            {
                if (!_normalCardBarriers.TryGetValue(
                        sourceEventId,
                        out var barrier))
                {
                    return false;
                }
                roster = barrier.Roster;
            }

            var currentIdentities = roster
                .Where(isCurrent)
                .Select(value => value.RunIdentity.ParticipantIdentity)
                .ToList();
            if (currentIdentities.Count == 0)
                return false;

            lock (_syncRoot)
            {
                if (!_normalCardBarriers.TryGetValue(
                        sourceEventId,
                        out var barrier)
                    || !barrier.NormalCardPhaseClosed)
                {
                    return false;
                }

                foreach (var identity in currentIdentities)
                {
                    if (!barrier.Participants.TryGetValue(
                            identity,
                            out var state)
                        || !state.DeadlineElapsed
                        || !state.FreeCommitted
                        || (state.PaidState != AntonPaidCardState.Committed
                            && state.PaidState
                                != AntonPaidCardState.Skipped))
                    {
                        return false;
                    }
                }

                barrier.SpecialProjectionReady = true;
                return true;
            }
        }

        internal bool TryGetOrRegisterPlanCreation(
            Guid sourceEventId,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            AntonAwakeningDailyCardService rewards,
            SequentialDungeonDefinition definition,
            int rewardableDungeonId,
            out AntonAwakeningRewardPlanCreation creation)
        {
            creation = null;
            if (sourceEventId == Guid.Empty
                || roster == null
                || rewards == null)
            {
                return false;
            }

            var rosterSnapshot = roster.ToArray();
            lock (_syncRoot)
            {
                if (!_planCreations.TryGetValue(sourceEventId, out creation))
                {
                    creation = new AntonAwakeningRewardPlanCreation(
                        sourceEventId,
                        () => CreatePlan(
                            sourceEventId,
                            rosterSnapshot,
                            rewards,
                            definition,
                            rewardableDungeonId));
                    _planCreations.Add(sourceEventId, creation);
                }
                return true;
            }
        }

        internal bool TryEvaluatePlanCreation(
            AntonAwakeningRewardPlanCreation creation,
            out AntonAwakeningRewardPlanCreationOutcome outcome)
        {
            outcome = null;
            if (creation == null)
                return false;

            lock (_syncRoot)
            {
                if (!_planCreations.TryGetValue(
                        creation.SourceEventId,
                        out var registered)
                    || !ReferenceEquals(registered, creation))
                {
                    return false;
                }
            }

            // This is the only potentially expensive operation in the API.
            // It must be called after releasing CardRewardProjectionGate.
            outcome = creation.Evaluate();
            return outcome != null;
        }

        internal bool TryPublishPlanCreation(
            AntonAwakeningRewardPlanCreation creation,
            AntonAwakeningRewardPlanCreationOutcome outcome)
        {
            if (creation == null || outcome == null)
                return false;

            lock (_syncRoot)
            {
                return _planCreations.TryGetValue(
                           creation.SourceEventId,
                           out var registered)
                    && ReferenceEquals(registered, creation)
                    && creation.TryPublish(outcome);
            }
        }

        internal bool TryGetPlan(
            Guid sourceEventId,
            out AntonAwakeningRewardPlan plan)
        {
            plan = null;
            AntonAwakeningRewardPlanCreation creation;
            lock (_syncRoot)
            {
                if (!_planCreations.TryGetValue(sourceEventId, out creation))
                    return false;
            }

            if (!creation.TryGetPublished(out var outcome))
                return false;
            plan = outcome.Plan;
            return plan != null && plan.Entries.Count > 0;
        }

        internal bool TryGetParticipantResolution(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            out AntonAwakeningParticipantRewardResolution resolution)
        {
            resolution = null;
            AntonAwakeningRewardPlanCreation creation;
            lock (_syncRoot)
            {
                if (!_planCreations.TryGetValue(sourceEventId, out creation))
                    return false;
            }

            if (!creation.TryGetPublished(out var outcome))
                return false;
            resolution = outcome.Resolution.Participants
                .FirstOrDefault(value => value.Participant.RunIdentity
                    .ParticipantIdentity.Equals(participant));
            return resolution != null;
        }

        internal bool TryRecordProjectionDeadline(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            DateTime deadlineUtc)
        {
            deadlineUtc = NormalizeUtc(deadlineUtc);
            if (sourceEventId == Guid.Empty
                || !participant.IsValid
                || deadlineUtc == DateTime.MinValue
                || !ContainsProjectionParticipant(
                    sourceEventId,
                    participant))
            {
                return false;
            }

            lock (_syncRoot)
            {
                var key = (sourceEventId, participant);
                if (_projectionDeadlinesUtc.TryGetValue(key, out var existing))
                    return existing == deadlineUtc;
                _projectionDeadlinesUtc.Add(key, deadlineUtc);
                return true;
            }
        }

        internal bool TryGetProjectionDeadline(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            out DateTime deadlineUtc)
        {
            lock (_syncRoot)
            {
                return _projectionDeadlinesUtc.TryGetValue(
                    (sourceEventId, participant),
                    out deadlineUtc);
            }
        }

        internal bool TryRecordCommitted(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            AntonAwakeningRewardGrantResult result)
        {
            if (sourceEventId == Guid.Empty
                || !participant.IsValid
                || result == null
                || result.Outcome == AntonAwakeningRewardGrantOutcome.Failed
                || !TryGetPlan(sourceEventId, out var plan)
                || !plan.Entries.Any(value =>
                    value.Participant.RunIdentity.ParticipantIdentity
                        .Equals(participant)))
            {
                return false;
            }

            lock (_syncRoot)
            {
                var key = (sourceEventId, participant);
                if (_committed.TryGetValue(key, out var existing))
                {
                    return existing.Outcome == result.Outcome
                        && existing.Reward.GroupKey == result.Reward.GroupKey
                        && existing.Reward.RewardableDungeonId
                            == result.Reward.RewardableDungeonId
                        && existing.Reward.RewardGroupItemId
                            == result.Reward.RewardGroupItemId
                        && existing.Reward.ItemId == result.Reward.ItemId
                        && existing.Reward.Quantity == result.Reward.Quantity
                        && existing.Reward.CardState == result.Reward.CardState;
                }
                _committed.Add(key, result);
                return true;
            }
        }

        internal bool TryGetCommitted(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            out AntonAwakeningRewardGrantResult result)
        {
            lock (_syncRoot)
                return _committed.TryGetValue((sourceEventId, participant), out result);
        }

        private static AntonAwakeningRewardPlanCreationOutcome CreatePlan(
            Guid sourceEventId,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            AntonAwakeningDailyCardService rewards,
            SequentialDungeonDefinition definition,
            int rewardableDungeonId)
        {
            AntonAwakeningRewardBatchResolution resolution;
            try
            {
                resolution = rewards.ResolveParticipantRewards(
                    sourceEventId,
                    roster,
                    definition,
                    rewardableDungeonId);
            }
            catch (Exception ex)
            {
                resolution = rewards.FreezeUnexpectedFailure(
                    sourceEventId,
                    roster,
                    definition,
                    rewardableDungeonId,
                    "unexpected " + ex.GetType().Name);
            }

            var entries = resolution.Participants
                .Where(value => value.Succeeded && value.Reward.IsValid)
                .Select(value => new AntonAwakeningRewardPlanEntry(
                    value.Participant,
                    value.Reward))
                .ToList()
                .AsReadOnly();
            var plan = entries.Count > 0
                ? new AntonAwakeningRewardPlan(sourceEventId, entries)
                : null;
            return new AntonAwakeningRewardPlanCreationOutcome(
                plan,
                resolution);
        }

        private static bool SameRoster(
            IReadOnlyList<DungeonParticipantRosterEntry> left,
            IReadOnlyList<DungeonParticipantRosterEntry> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;
            for (var index = 0; index < left.Count; index++)
            {
                if (!left[index].RunIdentity.ParticipantIdentity.Equals(
                        right[index].RunIdentity.ParticipantIdentity)
                    || left[index].CharacterId != right[index].CharacterId
                    || left[index].ParticipantUserId
                        != right[index].ParticipantUserId)
                {
                    return false;
                }
            }
            return true;
        }

        private bool ContainsProjectionParticipant(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant)
        {
            lock (_syncRoot)
            {
                if (_normalCardBarriers.TryGetValue(
                        sourceEventId,
                        out var barrier)
                    && barrier.Participants.ContainsKey(participant))
                {
                    return true;
                }

                if (!_planCreations.TryGetValue(
                        sourceEventId,
                        out var creation)
                    || !creation.TryGetPublished(out var outcome)
                    || outcome.Plan == null)
                {
                    return false;
                }

                return outcome.Plan.Entries.Any(value =>
                    value.Participant.RunIdentity.ParticipantIdentity
                        .Equals(participant));
            }
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value == DateTime.MinValue || value.Kind == DateTimeKind.Utc)
                return value;
            return value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static int NextGeneration(int previous)
        {
            var next = previous == int.MaxValue ? 1 : previous + 1;
            return next == 0 ? 1 : next;
        }
    }
}
