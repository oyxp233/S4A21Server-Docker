using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal readonly struct AntonPaidSelectionReservation
    {
        internal AntonPaidSelectionReservation(
            AntonAwakeningRewardRuntime runtime,
            AntonPaidSelectionTicket ticket)
        {
            Runtime = runtime;
            Ticket = ticket;
        }

        internal AntonAwakeningRewardRuntime Runtime { get; }
        internal AntonPaidSelectionTicket Ticket { get; }
        internal bool IsValid => Runtime != null && Ticket.IsValid;
    }

    // Coordinates per-participant inventory commits and a shared, generation-safe
    // projection. Durable daily ownership stays in SQLite; packet retries never
    // re-enter the inventory grant transaction.
    internal sealed class AntonAwakeningRewardCoordinator
    {
        private sealed class ProjectionWorkItem
        {
            internal ProjectionWorkItem(
                DungeonParticipantRosterEntry participant,
                DungeonParticipantEffectReservation reservation)
            {
                Participant = participant;
                Reservation = reservation;
            }

            internal DungeonParticipantRosterEntry Participant { get; }
            internal DungeonParticipantEffectReservation Reservation { get; }
        }

        internal static readonly TimeSpan PostRevealGrantDelay =
            TimeSpan.FromSeconds(11);

        private readonly AntonAwakeningDailyCardService _dailyRewards;
        private readonly AntonAwakeningRewardGrantService _grants;
        private readonly ISessionDirectory _sessions;
        private readonly InventoryRefreshSender _inventoryRefresh;
        private readonly AntonNormalConquestNotificationSender _sender;
        private readonly TimeSpan _postRevealGrantDelay;

        internal AntonAwakeningRewardCoordinator(
            AntonAwakeningDailyCardService dailyRewards,
            AntonAwakeningRewardGrantService grants,
            ISessionDirectory sessions,
            InventoryRefreshSender inventoryRefresh,
            AntonNormalConquestNotificationSender sender,
            TimeSpan? postRevealGrantDelay = null)
        {
            _dailyRewards = dailyRewards
                ?? throw new ArgumentNullException(nameof(dailyRewards));
            _grants = grants ?? throw new ArgumentNullException(nameof(grants));
            _sessions = sessions;
            _inventoryRefresh = inventoryRefresh;
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _postRevealGrantDelay = postRevealGrantDelay
                ?? PostRevealGrantDelay;
            if (_postRevealGrantDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(postRevealGrantDelay));
            }
        }

        internal async Task PrepareClearAsync(
            DungeonRun sourceRun,
            DungeonClearedFact clearFact)
        {
            if (sourceRun?.Instance == null
                || !TryResolveRewardDefinition(
                    sourceRun,
                    out var rewardDefinition)
                || clearFact == null
                || clearFact.PresentationKind
                    != DungeonClearPresentationKind.Standard)
            {
                return;
            }

            var instance = sourceRun.Instance;
            var sourceIdentity = sourceRun.CaptureIdentity();
            IReadOnlyList<DungeonParticipantRosterEntry> roster = null;
            AntonAwakeningRewardRuntime runtime = null;
            AntonAwakeningRewardPlanCreation creation = null;

            await instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                if (!IsPreparationContextCurrent(
                        sourceRun,
                        sourceIdentity,
                        instance,
                        clearFact,
                        rewardDefinition,
                        expectedRoster: null))
                {
                    return;
                }

                var journal = instance.ParticipantEffects;
                roster = journal.GetRoster(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance);
                if (roster.Count == 0)
                    return;

                runtime = GetOrAttachRuntime(sourceRun);
                if (runtime == null
                    || !runtime.TryRegisterNormalCardBarrier(
                        clearFact.SourceEventId,
                        roster))
                {
                    return;
                }
            }
            finally
            {
                instance.CardRewardProjectionGate.Release();
            }

            IReadOnlyList<DungeonParticipantRosterEntry> eligible;
            try
            {
                eligible = roster
                    .Where(value => value != null
                        && !_dailyRewards.HasClaimedRewardToday(
                            value.CharacterId,
                            rewardDefinition.GroupKey,
                            sourceRun.DungeonId))
                    .ToList()
                    .AsReadOnly();
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonAwakening] reward eligibility failed: "
                    + $"instance={instance.PartyDungeonInstanceId} "
                    + $"event={clearFact.SourceEventId:N} "
                    + $"error={ex.Message}");
                return;
            }
            if (eligible.Count == 0)
                return;

            await instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                if (!IsPreparationContextCurrent(
                        sourceRun,
                        sourceIdentity,
                        instance,
                        clearFact,
                        rewardDefinition,
                        roster)
                    || !ReferenceEquals(
                        instance.Mechanisms.AntonAwakeningReward,
                        runtime)
                    || !runtime.TryGetOrRegisterPlanCreation(
                        clearFact.SourceEventId,
                        eligible,
                        _dailyRewards,
                        rewardDefinition,
                        sourceRun.DungeonId,
                        out creation))
                {
                    return;
                }
            }
            finally
            {
                instance.CardRewardProjectionGate.Release();
            }

            AntonAwakeningRewardPlanCreationOutcome outcome;
            try
            {
                if (creation == null
                    || !runtime.TryEvaluatePlanCreation(
                        creation,
                        out outcome))
                {
                    FileLogger.Log(
                        $"[AntonAwakening] reward plan unavailable: "
                        + $"instance={instance.PartyDungeonInstanceId} "
                        + $"event={clearFact.SourceEventId:N}");
                    return;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonAwakening] reward planning failed: "
                    + $"instance={instance.PartyDungeonInstanceId} "
                    + $"event={clearFact.SourceEventId:N} "
                    + $"error={ex.Message}");
                return;
            }

            var contextIsCurrent = false;
            var published = false;
            await instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                contextIsCurrent = IsPreparationContextCurrent(
                    sourceRun,
                    sourceIdentity,
                    instance,
                    clearFact,
                    rewardDefinition,
                    roster);
                published = contextIsCurrent
                    && ReferenceEquals(
                        instance.Mechanisms.AntonAwakeningReward,
                        runtime)
                    && runtime.TryPublishPlanCreation(creation, outcome);
            }
            finally
            {
                instance.CardRewardProjectionGate.Release();
            }

            if (!contextIsCurrent)
            {
                FileLogger.Log(
                    $"[AntonAwakening] stale reward plan discarded: "
                    + $"instance={instance.PartyDungeonInstanceId} "
                    + $"event={clearFact.SourceEventId:N}");
                return;
            }

            var plan = outcome.Plan;
            if (!published || plan == null || plan.Entries.Count == 0)
            {
                FileLogger.Log(
                    $"[AntonAwakening] reward plan unavailable: "
                    + $"instance={instance.PartyDungeonInstanceId} "
                    + $"event={clearFact.SourceEventId:N}");
                return;
            }

            FileLogger.Log(
                $"[AntonAwakening] reward plan prepared: "
                + $"instance={instance.PartyDungeonInstanceId} "
                + $"event={plan.SourceEventId:N} "
                + $"participants={plan.Entries.Count}");

            // The ordinary card flow can be scheduled before the reward plan
            // finishes its database-backed preparation. Recheck the barrier
            // after publication so an already elapsed deadline cannot lose
            // the one transition that makes special projection eligible.
            foreach (var participant in roster)
            {
                if (!TryResolveCurrentSession(participant, out var session))
                    continue;
                await TryProjectReadyPartyAsync(session, participant.Run);
                break;
            }
        }

        internal Task OnFreeCardCommittedAsync(
            EnhancedClientSession session,
            DungeonRun run)
            => OnCardCommittedAsync(session, run, CardRewardSide.Free);

        internal Task OnCardCommittedAsync(
            EnhancedClientSession session,
            DungeonRun run,
            CardRewardSide side)
        {
            if (!TryResolveNormalCardBarrierParticipant(
                    session,
                    run,
                    out var runtime,
                    out var sourceEventId,
                    out var participant)
                || !CardRewardRules.IsCommitted(run, side))
            {
                return Task.CompletedTask;
            }

            runtime.TryMarkCardCommitted(
                sourceEventId,
                participant.RunIdentity.ParticipantIdentity,
                side);
            return Task.CompletedTask;
        }

        internal bool IsPaidSelectionOpen(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (!TryResolveNormalCardBarrierParticipant(
                    session,
                    run,
                    out var runtime,
                    out var sourceEventId,
                    out var participant))
            {
                return true;
            }

            return runtime.IsPaidSelectionOpen(
                sourceEventId,
                participant.RunIdentity.ParticipantIdentity);
        }

        internal bool TryBeginPaidSelection(
            EnhancedClientSession session,
            DungeonRun run,
            out AntonPaidSelectionReservation reservation)
        {
            reservation = default;
            if (!TryResolveNormalCardBarrierParticipant(
                    session,
                    run,
                    out var runtime,
                    out var sourceEventId,
                    out var participant))
            {
                return true;
            }

            if (!runtime.TryBeginPaidSelection(
                    sourceEventId,
                    participant.RunIdentity.ParticipantIdentity,
                    out var ticket))
            {
                return false;
            }

            reservation = new AntonPaidSelectionReservation(runtime, ticket);
            return true;
        }

        internal bool CancelPaidSelection(
            AntonPaidSelectionReservation reservation)
        {
            return reservation.IsValid
                && reservation.Runtime.TryCancelPaidSelection(
                    reservation.Ticket);
        }

        internal void ScheduleNormalPhaseDeadline(
            EnhancedClientSession session,
            DungeonRun run,
            DateTime deadlineUtc)
        {
            if (!TryResolveNormalCardBarrierParticipant(
                    session,
                    run,
                    out var runtime,
                    out var sourceEventId,
                    out var participant)
                || !runtime.TryRecordNormalDeadline(
                    sourceEventId,
                    participant.RunIdentity.ParticipantIdentity,
                    deadlineUtc)
                || !runtime.TryGetNormalDeadline(
                    sourceEventId,
                    participant.RunIdentity.ParticipantIdentity,
                    out deadlineUtc))
            {
                return;
            }

            RunTimerTicket ticket;
            lock (session.Player.DungeonRunLifecycleSyncRoot)
            {
                if (!TryResolveCurrentSession(participant, out var current)
                    || !ReferenceEquals(current, session)
                    || !IsCurrentTimerOwner(session, participant))
                {
                    return;
                }

                ticket = run.Timers.Begin(
                    DungeonRunTimerKeys.AntonAwakeningNormalCardDeadline,
                    deadlineUtc,
                    RunTimerDetachPolicy.SuspendUntilResume);
            }
            ScheduleNormalPhaseDeadlineTimer(
                session,
                run,
                participant,
                runtime,
                sourceEventId,
                deadlineUtc,
                ticket);
        }

        internal bool RecoverNormalPhaseDeadline(
            EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (!TryResolveNormalCardBarrierParticipant(
                    session,
                    run,
                    out var runtime,
                    out var sourceEventId,
                    out var participant))
            {
                return false;
            }
            if (runtime.IsNormalCardPhaseClosed(sourceEventId))
            {
                run.Timers.Cancel(
                    DungeonRunTimerKeys.AntonAwakeningNormalCardDeadline);
                return false;
            }
            if (!runtime.TryGetNormalDeadline(
                    sourceEventId,
                    participant.RunIdentity.ParticipantIdentity,
                    out var recordedDeadline))
            {
                return false;
            }

            RunTimerTicket ticket;
            DateTime deadlineUtc;
            lock (session.Player.DungeonRunLifecycleSyncRoot)
            {
                if (!TryResolveCurrentSession(participant, out var current)
                    || !ReferenceEquals(current, session)
                    || !IsCurrentTimerOwner(session, participant))
                {
                    return false;
                }

                if (run.Timers.TryGetSnapshot(
                        DungeonRunTimerKeys
                            .AntonAwakeningNormalCardDeadline,
                        out var snapshot)
                    && snapshot.HasDeadline)
                {
                    if (snapshot.DeadlineUtc != recordedDeadline
                        || !snapshot.IsSuspended
                        || !run.Timers.TryResume(
                            DungeonRunTimerKeys
                                .AntonAwakeningNormalCardDeadline,
                            out ticket,
                            out deadlineUtc))
                    {
                        return false;
                    }
                }
                else
                {
                    deadlineUtc = recordedDeadline;
                    ticket = run.Timers.Begin(
                        DungeonRunTimerKeys
                            .AntonAwakeningNormalCardDeadline,
                        deadlineUtc,
                        RunTimerDetachPolicy.SuspendUntilResume);
                }
            }

            ScheduleNormalPhaseDeadlineTimer(
                session,
                run,
                participant,
                runtime,
                sourceEventId,
                deadlineUtc,
                ticket);
            return true;
        }

        internal bool MarkNormalPhaseDeadlineElapsed(
            EnhancedClientSession session,
            DungeonRun run)
        {
            return TryResolveNormalCardBarrierParticipant(
                    session,
                    run,
                    out var runtime,
                    out var sourceEventId,
                    out var participant)
                && runtime.TryMarkDeadlineElapsed(
                    sourceEventId,
                    participant.RunIdentity.ParticipantIdentity);
        }

        internal async Task TryProjectReadyPartyAsync(
            EnhancedClientSession session,
            DungeonRun sourceRun)
        {
            if (!TryResolveBarrierParticipant(
                    session,
                    sourceRun,
                    out _,
                    out _,
                    out _))
            {
                return;
            }

            var instance = sourceRun.Instance;
            AntonAwakeningRewardRuntime runtime = null;
            AntonAwakeningRewardPlan plan = null;
            DungeonParticipantEffectJournal journal = null;
            IReadOnlyList<AntonAwakeningRewardEntry> projected = null;
            var sendWork = new List<ProjectionWorkItem>();
            var reusedWork = new List<ProjectionWorkItem>();
            var alreadyCommitted = new List<AntonAwakeningRewardPlanEntry>();

            await instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                if (!TryResolveBarrierParticipant(
                        session,
                        sourceRun,
                        out runtime,
                        out plan,
                        out _))
                {
                    return;
                }
                if (!runtime.TryGetNormalCardRoster(
                        plan.SourceEventId,
                        out var barrierRoster))
                {
                    return;
                }

                var currentRoster = barrierRoster
                    .Where(value => TryResolveCurrentSession(value, out _))
                    .ToList()
                    .AsReadOnly();
                if (!runtime.AreAllCurrentParticipantsReady(
                        plan.SourceEventId,
                        value => currentRoster.Any(current =>
                            ReferenceEquals(current, value))))
                {
                    return;
                }

                journal = instance.ParticipantEffects;
                projected = BuildProjectedEntries(plan);
                if (projected.Count == 0)
                    return;

                foreach (var participant in currentRoster)
                {
                    var identity = participant.RunIdentity
                        .ParticipantIdentity;
                    if (journal.GetState(
                            plan.SourceEventId,
                            DungeonParticipantEffectAudience.Instance,
                            identity,
                            DungeonParticipantEffectKinds
                                .AntonAwakeningRewardProjection)
                        == DungeonParticipantEffectState.Committed)
                    {
                        var committedEntry = FindPlanEntry(plan, participant);
                        if (committedEntry != null)
                            alreadyCommitted.Add(committedEntry);
                        continue;
                    }

                    if (!journal.TryBegin(
                            plan.SourceEventId,
                            DungeonParticipantEffectAudience.Instance,
                            participant,
                            DungeonParticipantEffectKinds
                                .AntonAwakeningRewardProjection,
                            out var reservation,
                            out _))
                    {
                        continue;
                    }

                    var work = new ProjectionWorkItem(
                        participant,
                        reservation);
                    if (runtime.TryGetProjectionDeadline(
                            plan.SourceEventId,
                            identity,
                            out _))
                    {
                        reusedWork.Add(work);
                    }
                    else
                        sendWork.Add(work);
                }
            }
            finally
            {
                instance.CardRewardProjectionGate.Release();
            }

            PartyPacketSendResult sendResult = null;
            Exception sendError = null;
            if (sendWork.Count > 0)
            {
                try
                {
                    sendResult = await _sender
                        .SendAntonAwakeningRewardToPartyAsync(
                            sendWork.Select(value => value.Participant)
                                .ToList()
                                .AsReadOnly(),
                            projected);
                }
                catch (Exception ex)
                {
                    sendError = ex;
                }
            }

            var projectionUtc = DateTime.UtcNow;
            var timerEntries = new List<AntonAwakeningRewardPlanEntry>(
                alreadyCommitted);
            await instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                if (!ReferenceEquals(
                        instance.Mechanisms.AntonAwakeningReward,
                        runtime)
                    || !runtime.TryGetPlan(
                        plan.SourceEventId,
                        out var currentPlan)
                    || !ReferenceEquals(currentPlan, plan))
                {
                    FailProjectionWork(journal, reusedWork);
                    FailProjectionWork(journal, sendWork);
                    return;
                }

                foreach (var work in reusedWork)
                {
                    if (!TryResolveCurrentSession(
                            work.Participant,
                            out _)
                        || !journal.TryCommit(work.Reservation))
                    {
                        journal.TryFail(work.Reservation);
                        continue;
                    }
                    var entry = FindPlanEntry(plan, work.Participant);
                    if (entry != null)
                        timerEntries.Add(entry);
                }

                var succeeded = sendResult?.Succeeded
                    .Select(value => value.RunIdentity.ParticipantIdentity)
                    .ToHashSet()
                    ?? new HashSet<DungeonParticipantRunIdentity>();
                foreach (var work in sendWork)
                {
                    var identity = work.Participant.RunIdentity
                        .ParticipantIdentity;
                    var deadlineUtc = projectionUtc
                        .Add(_postRevealGrantDelay);
                    if (sendError != null
                        || !succeeded.Contains(identity)
                        || !TryResolveCurrentSession(
                            work.Participant,
                            out _)
                        || !runtime.TryRecordProjectionDeadline(
                            plan.SourceEventId,
                            identity,
                            deadlineUtc)
                        || !journal.TryCommit(work.Reservation))
                    {
                        journal.TryFail(work.Reservation);
                        continue;
                    }

                    var entry = FindPlanEntry(plan, work.Participant);
                    if (entry != null)
                        timerEntries.Add(entry);
                    FileLogger.Log(
                        $"[AntonAwakening] reward projected: "
                        + $"cid={work.Participant.CharacterId} "
                        + $"userId={work.Participant.ParticipantUserId} "
                        + $"deadline={deadlineUtc:O} "
                        + $"event={plan.SourceEventId:N}");
                }
            }
            finally
            {
                instance.CardRewardProjectionGate.Release();
            }

            foreach (var entry in timerEntries
                .GroupBy(value => value.Participant.RunIdentity
                    .ParticipantIdentity)
                .Select(value => value.First()))
            {
                EnsureGrantTimerScheduled(runtime, plan, entry);
            }

            if (sendError != null)
            {
                FileLogger.Log(
                    $"[AntonAwakening] party projection failed: "
                    + $"event={plan.SourceEventId:N} "
                    + $"error={sendError.GetType().Name}: "
                    + sendError.Message);
            }
        }

        internal async Task RecoverParticipantAsync(
            EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (!TryResolveRewardDefinition(run, out _))
                return;

            if (TryResolveNormalCardBarrierParticipant(
                    session,
                    run,
                    out var runtime,
                    out var sourceEventId,
                    out var participant))
            {
                runtime.TryRecoverPaidSelection(
                    sourceEventId,
                    participant.RunIdentity.ParticipantIdentity);
            }
            RecoverNormalPhaseDeadline(session);
            if (CardRewardRules.IsCommitted(run, CardRewardSide.Free))
            {
                await OnCardCommittedAsync(
                    session,
                    run,
                    CardRewardSide.Free);
            }
            if (CardRewardRules.IsCommitted(run, CardRewardSide.Paid))
            {
                await OnCardCommittedAsync(
                    session,
                    run,
                    CardRewardSide.Paid);
            }
            await TryProjectReadyPartyAsync(session, run);
        }

        private void ScheduleNormalPhaseDeadlineTimer(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonParticipantRosterEntry participant,
            AntonAwakeningRewardRuntime runtime,
            Guid sourceEventId,
            DateTime deadlineUtc,
            RunTimerTicket ticket)
        {
            if (!IsNormalPhaseDeadlineCurrent(
                    session,
                    run,
                    participant,
                    runtime,
                    sourceEventId,
                    ticket))
            {
                return;
            }

            var handle = ClockService.Instance.ScheduleOneShotAsync(
                BuildNormalCardDeadlineTimerName(
                    sourceEventId,
                    participant,
                    ticket),
                deadlineUtc,
                async _ => await OnNormalPhaseDeadlineElapsedAsync(
                    session,
                    run,
                    participant,
                    runtime,
                    sourceEventId,
                    ticket));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnNormalPhaseDeadlineElapsedAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonParticipantRosterEntry participant,
            AntonAwakeningRewardRuntime runtime,
            Guid sourceEventId,
            RunTimerTicket ticket)
        {
            var elapsed = false;
            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!IsNormalPhaseDeadlineCurrent(
                        session,
                        run,
                        participant,
                        runtime,
                        sourceEventId,
                        ticket))
                {
                    return;
                }

                elapsed = runtime.TryMarkDeadlineElapsed(
                    sourceEventId,
                    participant.RunIdentity.ParticipantIdentity);
            }
            finally
            {
                if (elapsed)
                    run.Timers.TryComplete(ticket);
                run.Settlement.CardProjectionGate.Release();
            }

            if (elapsed)
                await TryProjectReadyPartyAsync(session, run);
        }

        private static bool IsNormalPhaseDeadlineCurrent(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonParticipantRosterEntry participant,
            AntonAwakeningRewardRuntime runtime,
            Guid sourceEventId,
            RunTimerTicket ticket)
            => run?.Instance != null
               && participant != null
               && runtime != null
               && sourceEventId != Guid.Empty
               && run.Timers.IsCurrent(ticket)
               && ReferenceEquals(
                   run.Instance.Mechanisms.AntonAwakeningReward,
                   runtime)
               && runtime.TryGetNormalCardRoster(sourceEventId, out var roster)
               && roster.Any(value => value.RunIdentity.ParticipantIdentity
                   .Equals(participant.RunIdentity.ParticipantIdentity))
               && IsCurrentParticipantSession(session, participant);

        private void EnsureGrantTimerScheduled(
            AntonAwakeningRewardRuntime runtime,
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardPlanEntry entry)
        {
            var run = entry?.Participant?.Run;
            var identity = entry?.Participant?.RunIdentity
                .ParticipantIdentity ?? default;
            if (run == null
                || !TryResolveCurrentSession(
                    entry.Participant,
                    out var session)
                || !runtime.TryGetProjectionDeadline(
                    plan.SourceEventId,
                    identity,
                    out var deadlineUtc))
            {
                return;
            }

            RunTimerTicket ticket;
            lock (session.Player.DungeonRunLifecycleSyncRoot)
            {
                if (!TryResolveCurrentSession(
                        entry.Participant,
                        out var current)
                    || !ReferenceEquals(current, session)
                    || !IsCurrentTimerOwner(session, entry.Participant))
                {
                    return;
                }

                if (run.Timers.TryGetSnapshot(
                        DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                        out var snapshot)
                    && snapshot.HasDeadline
                    && snapshot.DeadlineUtc == deadlineUtc)
                {
                    if (snapshot.IsSuspended)
                    {
                        if (!run.Timers.TryResume(
                                DungeonRunTimerKeys
                                    .AntonAwakeningPostRevealGrant,
                                out ticket,
                                out deadlineUtc))
                        {
                            return;
                        }
                    }
                    else if (!run.Timers.TryGetCurrentTicket(
                                 DungeonRunTimerKeys
                                     .AntonAwakeningPostRevealGrant,
                                 out ticket))
                    {
                        return;
                    }
                }
                else
                {
                    ticket = run.Timers.Begin(
                        DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                        deadlineUtc,
                        RunTimerDetachPolicy.SuspendUntilResume);
                }
            }

            ScheduleGrantTimer(runtime, plan, entry, deadlineUtc, ticket);
        }

        private void ScheduleGrantTimer(
            AntonAwakeningRewardRuntime runtime,
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardPlanEntry entry,
            DateTime deadlineUtc,
            RunTimerTicket ticket)
        {
            var run = entry.Participant.Run;
            if (!run.Timers.IsCurrent(ticket))
                return;

            var handle = ClockService.Instance.ScheduleOneShotAsync(
                BuildGrantTimerName(plan, entry, ticket),
                deadlineUtc,
                async _ => await OnGrantTimerElapsedAsync(
                    runtime,
                    plan,
                    entry,
                    ticket));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnGrantTimerElapsedAsync(
            AntonAwakeningRewardRuntime runtime,
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardPlanEntry entry,
            RunTimerTicket ticket)
        {
            var run = entry?.Participant?.Run;
            if (run?.Instance == null || !run.Timers.IsCurrent(ticket))
                return;

            var journal = run.Instance.ParticipantEffects;
            DungeonParticipantEffectReservation reservation = default;
            var alreadyCommitted = false;
            var reserved = false;
            await run.Instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                var identity = entry.Participant.RunIdentity
                    .ParticipantIdentity;
                if (!run.Timers.IsCurrent(ticket)
                    || !ReferenceEquals(
                        run.Instance.Mechanisms.AntonAwakeningReward,
                        runtime)
                    || !runtime.TryGetPlan(
                        plan.SourceEventId,
                        out var currentPlan)
                    || !ReferenceEquals(plan, currentPlan)
                    || !run.Matches(entry.Participant.RunIdentity)
                    || !CardRewardRules.IsCommitted(
                        run,
                        CardRewardSide.Free)
                    || journal.GetState(
                        plan.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        identity,
                        DungeonParticipantEffectKinds.DungeonClear)
                        != DungeonParticipantEffectState.Committed
                    || journal.GetState(
                        plan.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        identity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        != DungeonParticipantEffectState.Committed)
                {
                    return;
                }

                reserved = journal.TryBegin(
                        plan.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        entry.Participant,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningAutoReward,
                        out reservation,
                        out var existingState);
                if (!reserved)
                {
                    alreadyCommitted = existingState
                        == DungeonParticipantEffectState.Committed;
                }
            }
            finally
            {
                run.Instance.CardRewardProjectionGate.Release();
            }

            if (alreadyCommitted)
            {
                run.Timers.TryComplete(ticket);
                return;
            }
            if (!reserved)
                return;

            if (await TryGrantParticipantAsync(
                    journal,
                    runtime,
                    plan,
                    entry,
                    reservation))
            {
                run.Timers.TryComplete(ticket);
            }
        }

        private async Task<bool> TryGrantParticipantAsync(
            DungeonParticipantEffectJournal journal,
            AntonAwakeningRewardRuntime runtime,
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardPlanEntry entry,
            DungeonParticipantEffectReservation reservation)
        {
            var participant = entry.Participant;
            var identity = participant.RunIdentity.ParticipantIdentity;
            if (journal.GetState(
                    plan.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    identity,
                    DungeonParticipantEffectKinds.DungeonClear)
                != DungeonParticipantEffectState.Committed)
            {
                return false;
            }

            try
            {
                if (!TryResolveCurrentSession(participant, out var session)
                    || !InventoryContext.TryGetOwnedLease(
                        session.SessionId,
                        participant.CharacterId,
                        out var lease))
                {
                    journal.TryFail(reservation);
                    return false;
                }

                var result = _grants.TryGrant(lease, entry.Reward);
                if (result.Outcome == AntonAwakeningRewardGrantOutcome.Failed
                    || !runtime.TryRecordCommitted(
                        plan.SourceEventId,
                        identity,
                        result))
                {
                    journal.TryFail(reservation);
                    return false;
                }

                if (!journal.TryCommit(reservation))
                {
                    throw new InvalidOperationException(
                        "Anton reward effect reservation was lost after commit.");
                }

                if (result.Outcome == AntonAwakeningRewardGrantOutcome.Granted)
                {
                    try
                    {
                        await SendInventoryRefreshAsync(
                            session,
                            participant,
                            lease,
                            result.Changes);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[AntonAwakening] inventory refresh failed: "
                            + $"cid={participant.CharacterId} "
                            + $"event={plan.SourceEventId:N} "
                            + $"error={ex.Message}");
                    }
                }

                var delivery = result.Outcome
                    != AntonAwakeningRewardGrantOutcome.Granted
                    ? "none"
                    : result.DeliveredToMailbox
                        ? "mailbox"
                        : "inventory";
                FileLogger.Log(
                    $"[AntonAwakening] reward committed: "
                    + $"cid={participant.CharacterId} "
                    + $"userId={participant.ParticipantUserId} "
                    + $"group={entry.Reward.GroupKey} "
                    + $"rewardGroup={entry.Reward.RewardGroupItemId} "
                    + $"item={entry.Reward.ItemId} "
                    + $"quantity={entry.Reward.Quantity} "
                    + $"state={entry.Reward.CardState} "
                    + $"outcome={result.Outcome} "
                    + $"delivery={delivery} "
                    + $"event={plan.SourceEventId:N}");
                return true;
            }
            catch (Exception ex)
            {
                journal.TryFail(reservation);
                FileLogger.Log(
                    $"[AntonAwakening] reward failed: "
                    + $"cid={participant.CharacterId} "
                    + $"event={plan.SourceEventId:N} error={ex.Message}");
                return false;
            }
        }

        private bool TryResolveParticipantPlan(
            EnhancedClientSession session,
            DungeonRun run,
            out DungeonParticipantEffectJournal journal,
            out AntonAwakeningRewardRuntime runtime,
            out AntonAwakeningRewardPlan plan,
            out AntonAwakeningRewardPlanEntry entry)
        {
            journal = null;
            runtime = null;
            plan = null;
            entry = null;
            var player = session?.Player;
            var clearFact = run?.ClearedFact ?? run?.Instance?.ClearedFact;
            if (player == null
                || run?.Instance == null
                || !TryResolveRewardDefinition(run, out _)
                || clearFact?.PresentationKind
                    != DungeonClearPresentationKind.Standard
                || !ReferenceEquals(player.CurrentRun, run)
                || !player.IsCurrentDungeonRun(run.CaptureIdentity())
                || _sessions == null
                || !_sessions.TryGet(player.CharacterId, out var current)
                || !ReferenceEquals(current, session))
            {
                return false;
            }

            journal = run.Instance.ParticipantEffects;
            runtime = run.Instance.Mechanisms.AntonAwakeningReward;
            if (runtime == null
                || !runtime.TryGetPlan(clearFact.SourceEventId, out plan))
                return false;

            var participantIdentity = run.CaptureParticipantIdentity();
            entry = plan.Entries.FirstOrDefault(value =>
                value.Participant.CharacterId == player.CharacterId
                && ReferenceEquals(value.Participant.Run, run)
                && value.Participant.RunIdentity.ParticipantIdentity
                    .Equals(participantIdentity));
            return entry != null
                && journal.GetState(
                    plan.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    participantIdentity,
                    DungeonParticipantEffectKinds.DungeonClear)
                    == DungeonParticipantEffectState.Committed;
        }

        private bool TryResolveBarrierParticipant(
            EnhancedClientSession session,
            DungeonRun run,
            out AntonAwakeningRewardRuntime runtime,
            out AntonAwakeningRewardPlan plan,
            out DungeonParticipantRosterEntry participant)
        {
            plan = null;
            if (!TryResolveNormalCardBarrierParticipant(
                    session,
                    run,
                    out runtime,
                    out var sourceEventId,
                    out participant)
                || !runtime.TryGetPlan(sourceEventId, out plan))
            {
                return false;
            }

            return true;
        }

        private bool TryResolveNormalCardBarrierParticipant(
            EnhancedClientSession session,
            DungeonRun run,
            out AntonAwakeningRewardRuntime runtime,
            out Guid sourceEventId,
            out DungeonParticipantRosterEntry participant)
        {
            runtime = null;
            sourceEventId = Guid.Empty;
            participant = null;
            var player = session?.Player;
            var clearFact = run?.ClearedFact ?? run?.Instance?.ClearedFact;
            if (player == null
                || run?.Instance == null
                || !TryResolveRewardDefinition(run, out _)
                || clearFact?.PresentationKind
                    != DungeonClearPresentationKind.Standard
                || !ReferenceEquals(player.CurrentRun, run)
                || !player.IsCurrentDungeonRun(run.CaptureIdentity())
                || _sessions == null
                || !_sessions.TryGet(player.CharacterId, out var current)
                || !ReferenceEquals(current, session))
            {
                return false;
            }

            var roster = run.Instance.ParticipantEffects.GetRoster(
                clearFact.SourceEventId,
                DungeonParticipantEffectAudience.Instance);
            runtime = GetOrAttachRuntime(run);
            if (runtime == null
                || !runtime.TryRegisterNormalCardBarrier(
                    clearFact.SourceEventId,
                    roster))
            {
                return false;
            }

            var identity = run.CaptureParticipantIdentity();
            participant = roster.FirstOrDefault(value =>
                value.CharacterId == player.CharacterId
                && ReferenceEquals(value.Run, run)
                && value.RunIdentity.ParticipantIdentity.Equals(identity));
            if (participant == null
                || run.Instance.ParticipantEffects.GetState(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    identity,
                    DungeonParticipantEffectKinds.DungeonClear)
                    != DungeonParticipantEffectState.Committed)
            {
                return false;
            }

            sourceEventId = clearFact.SourceEventId;
            return sourceEventId != Guid.Empty;
        }

        private static AntonAwakeningRewardPlanEntry FindPlanEntry(
            AntonAwakeningRewardPlan plan,
            DungeonParticipantRosterEntry participant)
        {
            if (plan == null || participant == null)
                return null;
            var identity = participant.RunIdentity.ParticipantIdentity;
            return plan.Entries.FirstOrDefault(value =>
                value.Participant.RunIdentity.ParticipantIdentity.Equals(
                    identity));
        }

        private static void FailProjectionWork(
            DungeonParticipantEffectJournal journal,
            IEnumerable<ProjectionWorkItem> work)
        {
            if (journal == null || work == null)
                return;
            foreach (var item in work)
                journal.TryFail(item.Reservation);
        }

        private async Task SendInventoryRefreshAsync(
            EnhancedClientSession session,
            DungeonParticipantRosterEntry participant,
            InventoryLease lease,
            InventoryMutationSet changes)
        {
            if (_inventoryRefresh == null || changes == null)
                return;

            foreach (var group in changes.Slots.GroupBy(value => value.ListType))
            {
                if (!IsCurrentParticipantSession(session, participant)
                    || !InventoryContext.IsCurrentLease(
                        lease,
                        session.SessionId,
                        participant.CharacterId))
                {
                    return;
                }
                await _inventoryRefresh.SendUpdateItemList(
                    session,
                    group.Key,
                    group.Select(value => value.SlotIndex));
            }
        }

        private bool TryResolveCurrentSession(
            DungeonParticipantRosterEntry participant,
            out EnhancedClientSession session)
        {
            session = null;
            return participant != null
                && _sessions != null
                && _sessions.TryGet(participant.CharacterId, out session)
                && IsCurrentParticipantSession(session, participant);
        }

        private static bool TryResolveRewardDefinition(
            DungeonRun run,
            out SequentialDungeonDefinition definition)
        {
            definition = run?.Instance?.SequentialDefinition;
            if (definition == null
                || !definition.IsAntonDungeonSequence
                || !definition.ShowIndividualProcess
                || !definition.RewardableDungeonIds.Contains(run.DungeonId))
            {
                definition = null;
                return false;
            }

            // Resolve through the shared catalog as a capability check, then
            // continue using the instance-frozen Definition for this run.
            return SequentialDungeonDefinitionCatalog.Current
                    .TryResolveRewardableByDungeonId(
                        run.DungeonId,
                        out var resolved)
                && ReferenceEquals(resolved, definition);
        }

        private static bool IsPreparationContextCurrent(
            DungeonRun sourceRun,
            DungeonRunIdentity sourceIdentity,
            DungeonInstance instance,
            DungeonClearedFact clearFact,
            SequentialDungeonDefinition rewardDefinition,
            IReadOnlyList<DungeonParticipantRosterEntry> expectedRoster)
        {
            if (sourceRun == null
                || instance == null
                || clearFact == null
                || rewardDefinition == null
                || !ReferenceEquals(sourceRun.Instance, instance)
                || !sourceRun.Matches(sourceIdentity)
                || !clearFact.Source.RunIdentity.Equals(sourceIdentity)
                || !ReferenceEquals(sourceRun.ClearedFact, clearFact)
                || !ReferenceEquals(instance.ClearedFact, clearFact)
                || !ReferenceEquals(
                    instance.SequentialDefinition,
                    rewardDefinition)
                || sourceRun.RunState == DungeonRunState.Ending
                || sourceRun.RunState == DungeonRunState.Ended
                || instance.State == DungeonInstanceState.Ending
                || instance.State == DungeonInstanceState.Ended
                || !instance.ParticipantEffects.TryGetSource(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    out var frozenSource)
                || !ReferenceEquals(frozenSource, clearFact.Source))
            {
                return false;
            }

            if (expectedRoster == null)
                return true;

            var currentRoster = instance.ParticipantEffects.GetRoster(
                clearFact.SourceEventId,
                DungeonParticipantEffectAudience.Instance);
            return ReferenceEquals(currentRoster, expectedRoster)
                && expectedRoster.Count > 0
                && expectedRoster.All(value => value != null
                    && ReferenceEquals(value.Run?.Instance, instance)
                    && value.Run.Matches(value.RunIdentity));
        }

        internal static bool IsCurrentParticipantSession(
            EnhancedClientSession session,
            DungeonParticipantRosterEntry participant)
        {
            var player = session?.Player;
            return player != null
                && session.TcpClient != null
                && session.TcpClient.Connected
                && ReferenceEquals(player.CurrentRun, participant.Run)
                && player.IsCurrentDungeonRun(participant.RunIdentity)
                && participant.Run.Matches(participant.RunIdentity)
                && participant.Run.RunState != DungeonRunState.Ending
                && participant.Run.RunState != DungeonRunState.Ended
                && participant.Run.Instance != null
                && participant.Run.Instance.State
                    != DungeonInstanceState.Ending
                && participant.Run.Instance.State
                    != DungeonInstanceState.Ended;
        }

        private static bool IsCurrentTimerOwner(
            EnhancedClientSession session,
            DungeonParticipantRosterEntry participant)
            => IsCurrentParticipantSession(session, participant);

        internal static IReadOnlyList<AntonAwakeningRewardEntry>
            BuildProjectedEntries(AntonAwakeningRewardPlan plan)
        {
            var result = new List<AntonAwakeningRewardEntry>();
            foreach (var entry in plan.Entries)
            {
                result.Add(new AntonAwakeningRewardEntry(
                    entry.Participant.ParticipantUserId,
                    cardType: 0,
                    flags: (uint)entry.Reward.CardState,
                    itemId: (uint)entry.Reward.ItemId,
                    quantity: (uint)entry.Reward.Quantity));
            }
            return result.AsReadOnly();
        }

        private static string BuildGrantTimerName(
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardPlanEntry entry,
            RunTimerTicket ticket)
            => "anton-awakening:"
               + entry.Participant.CharacterId
               + ":"
               + plan.SourceEventId.ToString("N")
               + ":"
               + ticket.Generation;

        private static string BuildNormalCardDeadlineTimerName(
            Guid sourceEventId,
            DungeonParticipantRosterEntry participant,
            RunTimerTicket ticket)
            => "anton-awakening-normal-card:"
               + participant.CharacterId
               + ":"
               + sourceEventId.ToString("N")
               + ":"
               + ticket.Generation;

        private static AntonAwakeningRewardRuntime GetOrAttachRuntime(
            DungeonRun run)
        {
            var mechanisms = run?.Instance?.Mechanisms;
            if (mechanisms == null)
                return null;
            var existing = mechanisms.AntonAwakeningReward;
            if (existing != null)
                return existing;

            var created = new AntonAwakeningRewardRuntime();
            return mechanisms.TryAttachAntonAwakeningReward(created)
                ? created
                : mechanisms.AntonAwakeningReward;
        }
    }
}
