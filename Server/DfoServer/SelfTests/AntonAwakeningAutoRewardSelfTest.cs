using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    public static class AntonAwakeningAutoRewardSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== ANTON_AWAKENING_AUTO_REWARD selftest ===");
            var failures = 0;
            VerifyStableInstancePlanAndJournal(ref failures);
            VerifyGenerationSafePartyPacketSender(ref failures);
            VerifyPartyPacketBatchIsolation(ref failures);
            VerifyInFlightWriteTimeoutIsolation(ref failures);
            VerifyPreparationPlanningRunsOutsideProjectionGate(ref failures);
            VerifyStalePreparationIsNotPublished(ref failures);
            VerifyFourParticipantIndependentPlanning(ref failures);
            VerifyParticipantFailureIsolation(ref failures);
            VerifyNormalCardBarrierRuntime(ref failures);
            VerifyBarrierSurvivesProductionPreparationOrder(ref failures);
            VerifyTwoParticipantNormalCardBarrierProjection(ref failures);
            VerifyFourParticipantDeadlineBarrierProjection(ref failures);
            VerifyInvalidParticipantDoesNotBlockBarrier(ref failures);
            VerifyPaidSelectionDetachDeadlineResume(ref failures);
            VerifyPartialProjectionRetriesOnlyFailedParticipant(ref failures);
            VerifyNormalDeadlineTimerLifecycle(ref failures);
            VerifyMissingNormalDeadlineTimerRecovery(ref failures);
            VerifyLatePaidCardDoesNotCharge(ref failures);
            VerifyRejectedManualFreeKeepsAutoFlipTimer(ref failures);
            VerifyCardIoRunsOutsideStateGates(ref failures);
            VerifyConcurrentEplpRevealKeepsCommand(ref failures);
            VerifyDelayedProjectionState(ref failures);
            VerifyProjectionJournalRecovery(ref failures);
            VerifyTimerGrantAfterProjection(ref failures);
            VerifyEndingRunDoesNotRearmGrantTimer(ref failures);
            VerifyNormalCardMailboxOverflow(ref failures);
            VerifyTransactionalGrant(ref failures);
            VerifyNonRewardableDungeonDoesNotPrepare(ref failures);
            Console.WriteLine(
                failures == 0
                    ? "ANTON_AWAKENING_AUTO_REWARD selftest passed."
                    : $"ANTON_AWAKENING_AUTO_REWARD selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyNormalCardBarrierRuntime(ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var leftRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var rightRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var left = new DungeonParticipantRosterEntry(
                61901,
                401,
                leftRun,
                leftRun.CaptureIdentity(),
                room,
                1,
                partySlot: 0);
            var right = new DungeonParticipantRosterEntry(
                61902,
                402,
                rightRun,
                rightRun.CaptureIdentity(),
                room,
                1,
                partySlot: 1);
            var roster = new[] { left, right };
            var runtime = new AntonAwakeningRewardRuntime();
            var eventId = Guid.NewGuid();
            var firstDeadline = DateTime.UtcNow.AddSeconds(6);
            var slightlyLaterDeadline = firstDeadline.AddMilliseconds(25);

            var registered = runtime.TryRegisterNormalCardBarrier(
                eventId,
                roster);
            var leftDeadline = runtime.TryRecordNormalDeadline(
                eventId,
                left.RunIdentity.ParticipantIdentity,
                firstDeadline);
            var rightDeadline = runtime.TryRecordNormalDeadline(
                eventId,
                right.RunIdentity.ParticipantIdentity,
                slightlyLaterDeadline);
            var freeRecorded = runtime.TryMarkCardCommitted(
                eventId,
                left.RunIdentity.ParticipantIdentity,
                CardRewardSide.Free);
            var manualFreeDidNotOpenBarrier =
                !runtime.AreAllCurrentParticipantsReady(
                    eventId,
                    _ => true);
            var paidReserved = runtime.TryBeginPaidSelection(
                eventId,
                left.RunIdentity.ParticipantIdentity,
                out _);
            var paidRecorded = runtime.TryMarkCardCommitted(
                eventId,
                left.RunIdentity.ParticipantIdentity,
                CardRewardSide.Paid);
            var rightFreeRecorded = runtime.TryMarkCardCommitted(
                eventId,
                right.RunIdentity.ParticipantIdentity,
                CardRewardSide.Free);
            var closed = runtime.TryMarkDeadlineElapsed(
                eventId,
                left.RunIdentity.ParticipantIdentity);

            Check(
                "normal-card barrier freezes one common absolute deadline",
                registered
                && leftDeadline
                && rightDeadline
                && runtime.TryGetNormalDeadline(
                    eventId,
                    left.RunIdentity.ParticipantIdentity,
                    out var storedLeftDeadline)
                && runtime.TryGetNormalDeadline(
                    eventId,
                    right.RunIdentity.ParticipantIdentity,
                    out var storedRightDeadline)
                && storedLeftDeadline == firstDeadline
                && storedRightDeadline == firstDeadline,
                ref failures);
            Check(
                "manual free and paid commits wait for the common deadline",
                freeRecorded
                && manualFreeDidNotOpenBarrier
                && paidReserved
                && paidRecorded
                && rightFreeRecorded,
                ref failures);
            Check(
                "deadline skips pending paid cards and opens the ready barrier",
                closed
                && !runtime.IsPaidSelectionOpen(
                    eventId,
                    left.RunIdentity.ParticipantIdentity)
                && !runtime.IsPaidSelectionOpen(
                    eventId,
                    right.RunIdentity.ParticipantIdentity)
                && runtime.AreAllCurrentParticipantsReady(
                    eventId,
                    _ => true),
                ref failures);
            Check(
                "normal-card deadline timer key is independent from auto flip",
                !DungeonRunTimerKeys.AntonAwakeningNormalCardDeadline.Equals(
                    DungeonRunTimerKeys.SettlementCardAutoFlow),
                ref failures);
        }

        private static void VerifyTwoParticipantNormalCardBarrierProjection(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var leftRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var rightRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var source = DungeonEventEnvelope.Create(
                leftRun,
                61911,
                "anton-normal-card-party-barrier",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            leftRun.TryBeginClearCommit(clearFact);
            leftRun.TryCompleteClearCommit(clearFact);
            rightRun.TryBeginClearCommit(clearFact);
            rightRun.TryCompleteClearCommit(clearFact);
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var leftParticipant = new DungeonParticipantRosterEntry(
                61911,
                411,
                leftRun,
                leftRun.CaptureIdentity(),
                room,
                1,
                partySlot: 0);
            var rightParticipant = new DungeonParticipantRosterEntry(
                61912,
                412,
                rightRun,
                rightRun.CaptureIdentity(),
                room,
                1,
                partySlot: 1);
            var roster = new[] { leftParticipant, rightParticipant };
            var journal = instance.ParticipantEffects;
            journal.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                roster,
                out _);
            foreach (var participant in roster)
            {
                journal.TryBegin(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    participant,
                    DungeonParticipantEffectKinds.DungeonClear,
                    out var clearReservation,
                    out _);
                journal.TryCommit(clearReservation);
            }

            var rewards = CreateRewardService(
                dailyReset: null,
                finalItemId: 3309,
                quantity: 2);
            var sessions = new SessionDirectory();
            using (var left = new ConnectedSession())
            using (var right = new ConnectedSession())
            {
                left.Session.Player.CharacterId = leftParticipant.CharacterId;
                left.Session.Player.UserId =
                    leftParticipant.ParticipantUserId;
                left.Session.Player.CurrentRun = leftRun;
                right.Session.Player.CharacterId =
                    rightParticipant.CharacterId;
                right.Session.Player.UserId =
                    rightParticipant.ParticipantUserId;
                right.Session.Player.CurrentRun = rightRun;
                sessions.Register(leftParticipant.CharacterId, left.Session);
                sessions.Register(rightParticipant.CharacterId, right.Session);
                var coordinator = new AntonAwakeningRewardCoordinator(
                    rewards,
                    new AntonAwakeningRewardGrantService(rewards),
                    sessions,
                    null,
                        new AntonNormalConquestNotificationSender(
                            new PartyPacketSender(sessions)));
                coordinator.PrepareClearAsync(leftRun, clearFact)
                    .GetAwaiter()
                    .GetResult();
                var cardCoordinator = new CardRewardCoordinator(
                    sessions: sessions,
                    antonRewards: coordinator);
                cardCoordinator.ScheduleAutoFlow(
                    left.Session,
                    layoutDelayMs: 60000,
                    autoFlipDelayMs: 4000);
                var hasAutoFlowTimer = leftRun.Timers.TryGetCurrentTicket(
                        DungeonRunTimerKeys.SettlementCardAutoFlow,
                        out var autoFlowTicket);
                var hasNormalDeadlineTimer =
                    leftRun.Timers.TryGetCurrentTicket(
                        DungeonRunTimerKeys.AntonAwakeningNormalCardDeadline,
                        out var normalDeadlineTicket);
                DungeonRunLifecycle.CancelAutoFlip(left.Session);
                Check(
                    "manual free cancellation leaves normal deadline armed",
                    hasAutoFlowTimer
                    && hasNormalDeadlineTimer
                    && !leftRun.Timers.IsCurrent(autoFlowTicket)
                    && leftRun.Timers.IsCurrent(normalDeadlineTicket),
                    ref failures);

                leftRun.Effects.TryReserve(
                    CardRewardRules.GetEffectId(
                        leftRun,
                        CardRewardSide.Free),
                    out var leftFree);
                leftRun.Effects.TryCommit(leftFree);
                coordinator.OnCardCommittedAsync(
                        left.Session,
                        leftRun,
                        CardRewardSide.Free)
                    .GetAwaiter()
                    .GetResult();
                coordinator.TryProjectReadyPartyAsync(
                        left.Session,
                        leftRun)
                    .GetAwaiter()
                    .GetResult();

                Check(
                    "one manual free card cannot project the special party reward",
                    left.AvailableByteCount == 0
                    && right.AvailableByteCount == 0,
                    ref failures);

                rightRun.Effects.TryReserve(
                    CardRewardRules.GetEffectId(
                        rightRun,
                        CardRewardSide.Free),
                    out var rightFree);
                rightRun.Effects.TryCommit(rightFree);
                leftRun.Effects.TryReserve(
                    CardRewardRules.GetEffectId(
                        leftRun,
                        CardRewardSide.Paid),
                    out var leftPaid);
                leftRun.Effects.TryCommit(leftPaid);
                coordinator.OnCardCommittedAsync(
                        right.Session,
                        rightRun,
                        CardRewardSide.Free)
                    .GetAwaiter()
                    .GetResult();
                coordinator.OnCardCommittedAsync(
                        left.Session,
                        leftRun,
                        CardRewardSide.Paid)
                    .GetAwaiter()
                    .GetResult();
                var deadline = DateTime.UtcNow.AddSeconds(6);
                coordinator.ScheduleNormalPhaseDeadline(
                    left.Session,
                    leftRun,
                    deadline);
                coordinator.ScheduleNormalPhaseDeadline(
                    right.Session,
                    rightRun,
                    deadline.AddMilliseconds(10));
                var deadlineElapsed =
                    coordinator.MarkNormalPhaseDeadlineElapsed(
                        left.Session,
                        leftRun);
                coordinator.TryProjectReadyPartyAsync(
                        left.Session,
                        leftRun)
                    .GetAwaiter()
                    .GetResult();

                var leftPackets = left.ReadPackets(2);
                var rightPackets = right.ReadPackets(2);
                Check(
                    "common deadline projects one identical full party payload",
                    deadlineElapsed
                    && leftPackets.Count == 2
                    && rightPackets.Count == 2
                    && leftPackets[0].SequenceEqual(rightPackets[0])
                    && leftPackets[1].SequenceEqual(rightPackets[1])
                    && BitConverter.ToUInt32(leftPackets[0], 15) == 2
                    && BitConverter.ToUInt16(leftPackets[0], 1)
                        == (ushort)NotiPacketTypeA21
                            .ANTON_AWAKENING_MODE_REWARD
                    && BitConverter.ToUInt16(leftPackets[1], 1)
                        == (ushort)NotiPacketTypeA21.EXERCISE_MODE_CLEAR,
                    ref failures);

                leftRun.Timers.CancelAll();
                rightRun.Timers.CancelAll();
                sessions.UnregisterAsync(
                        leftParticipant.CharacterId,
                        left.Session)
                    .GetAwaiter()
                    .GetResult();
                sessions.UnregisterAsync(
                        rightParticipant.CharacterId,
                        right.Session)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private static void VerifyBarrierSurvivesProductionPreparationOrder(
            ref int failures)
        {
            using (var fixture = new BarrierPartyFixture(
                leftCharacterId: 61921,
                rightCharacterId: 61922,
                prepareImmediately: false))
            {
                var deadline = DateTime.UtcNow.AddMinutes(1);
                fixture.Coordinator.ScheduleNormalPhaseDeadline(
                    fixture.Left.Session,
                    fixture.LeftRun,
                    deadline);
                fixture.Coordinator.ScheduleNormalPhaseDeadline(
                    fixture.Right.Session,
                    fixture.RightRun,
                    deadline.AddMilliseconds(10));
                fixture.CommitCard(
                    fixture.LeftParticipant,
                    fixture.Left.Session,
                    CardRewardSide.Free);
                fixture.CommitCard(
                    fixture.RightParticipant,
                    fixture.Right.Session,
                    CardRewardSide.Free);
                fixture.CommitCard(
                    fixture.LeftParticipant,
                    fixture.Left.Session,
                    CardRewardSide.Paid);
                var elapsed = fixture.Coordinator
                    .MarkNormalPhaseDeadlineElapsed(
                        fixture.Left.Session,
                        fixture.LeftRun);

                fixture.Coordinator.TryProjectReadyPartyAsync(
                        fixture.Left.Session,
                        fixture.LeftRun)
                    .GetAwaiter()
                    .GetResult();
                var silentBeforePreparation =
                    fixture.Left.AvailableByteCount == 0
                    && fixture.Right.AvailableByteCount == 0;

                fixture.Prepare();
                var leftPackets = fixture.Left.ReadPackets(2);
                var rightPackets = fixture.Right.ReadPackets(2);

                Check(
                    "production order preserves card barrier before plan publication",
                    elapsed
                    && silentBeforePreparation
                    && leftPackets.Count == 2
                    && rightPackets.Count == 2
                    && leftPackets[0].SequenceEqual(rightPackets[0])
                    && leftPackets[1].SequenceEqual(rightPackets[1]),
                    ref failures);
            }
        }

        private static void VerifyFourParticipantDeadlineBarrierProjection(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var roster = BuildRoster(instance, 4, characterIdBase: 61970);
            var source = DungeonEventEnvelope.Create(
                roster[0].Run,
                roster[0].CharacterId,
                "anton-four-member-deadline-barrier",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            foreach (var participant in roster)
            {
                participant.Run.TryBeginClearCommit(clearFact);
                participant.Run.TryCompleteClearCommit(clearFact);
            }

            var journal = instance.ParticipantEffects;
            journal.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                roster,
                out _);
            foreach (var participant in roster)
            {
                journal.TryBegin(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    participant,
                    DungeonParticipantEffectKinds.DungeonClear,
                    out var clearReservation,
                    out _);
                journal.TryCommit(clearReservation);
            }

            var sessions = new SessionDirectory();
            var captures = new List<ConnectedSession>();
            try
            {
                foreach (var participant in roster)
                {
                    var capture = new ConnectedSession();
                    captures.Add(capture);
                    capture.Session.Player.CharacterId =
                        participant.CharacterId;
                    capture.Session.Player.UserId =
                        participant.ParticipantUserId;
                    capture.Session.Player.CurrentRun = participant.Run;
                    sessions.Register(
                        participant.CharacterId,
                        capture.Session);
                }

                var rewards = CreateRewardService(
                    dailyReset: null,
                    finalItemId: 3309,
                    quantity: 2);
                var coordinator = new AntonAwakeningRewardCoordinator(
                    rewards,
                    new AntonAwakeningRewardGrantService(rewards),
                    sessions,
                    null,
                    new AntonNormalConquestNotificationSender(
                        new PartyPacketSender(sessions)));
                coordinator.PrepareClearAsync(roster[0].Run, clearFact)
                    .GetAwaiter()
                    .GetResult();

                var deadline = DateTime.UtcNow.AddMinutes(1);
                for (var index = 0; index < roster.Count; index++)
                {
                    coordinator.ScheduleNormalPhaseDeadline(
                        captures[index].Session,
                        roster[index].Run,
                        deadline.AddMilliseconds(index));
                }
                for (var index = 0; index < roster.Count - 1; index++)
                {
                    CommitCard(
                        coordinator,
                        roster[index],
                        captures[index].Session,
                        CardRewardSide.Free);
                }
                CommitCard(
                    coordinator,
                    roster[0],
                    captures[0].Session,
                    CardRewardSide.Paid);
                var elapsed = coordinator.MarkNormalPhaseDeadlineElapsed(
                    captures[0].Session,
                    roster[0].Run);
                coordinator.TryProjectReadyPartyAsync(
                        captures[0].Session,
                        roster[0].Run)
                    .GetAwaiter()
                    .GetResult();
                var blockedByFourth = captures.All(value =>
                    value.AvailableByteCount == 0);

                CommitCard(
                    coordinator,
                    roster[3],
                    captures[3].Session,
                    CardRewardSide.Free);
                coordinator.TryProjectReadyPartyAsync(
                        captures[3].Session,
                        roster[3].Run)
                    .GetAwaiter()
                    .GetResult();
                var packets = captures
                    .Select(value => value.ReadPackets(2))
                    .ToList();

                Check(
                    "four-member deadline barrier waits for every free card",
                    elapsed
                    && blockedByFourth
                    && packets.All(value => value.Count == 2)
                    && packets.Skip(1).All(value =>
                        value[0].SequenceEqual(packets[0][0])
                        && value[1].SequenceEqual(packets[0][1]))
                    && BitConverter.ToUInt32(packets[0][0], 15) == 4,
                    ref failures);
            }
            finally
            {
                foreach (var participant in roster)
                    participant.Run.Timers.CancelAll();
                for (var index = 0; index < captures.Count; index++)
                {
                    sessions.UnregisterAsync(
                            roster[index].CharacterId,
                            captures[index].Session)
                        .GetAwaiter()
                        .GetResult();
                    captures[index].Dispose();
                }
            }
        }

        private static void CommitCard(
            AntonAwakeningRewardCoordinator coordinator,
            DungeonParticipantRosterEntry participant,
            EnhancedClientSession session,
            CardRewardSide side)
        {
            participant.Run.Effects.TryReserve(
                CardRewardRules.GetEffectId(participant.Run, side),
                out var reservation);
            participant.Run.Effects.TryCommit(reservation);
            coordinator.OnCardCommittedAsync(
                    session,
                    participant.Run,
                    side)
                .GetAwaiter()
                .GetResult();
        }

        private static void VerifyInvalidParticipantDoesNotBlockBarrier(
            ref int failures)
        {
            using (var fixture = new BarrierPartyFixture(
                leftCharacterId: 61931,
                rightCharacterId: 61932))
            {
                fixture.CommitCard(
                    fixture.LeftParticipant,
                    fixture.Left.Session,
                    CardRewardSide.Free);
                fixture.Right.Session.Player.CurrentRun = null;

                var elapsed = fixture.Coordinator
                    .MarkNormalPhaseDeadlineElapsed(
                        fixture.Left.Session,
                        fixture.LeftRun);
                fixture.Coordinator.TryProjectReadyPartyAsync(
                        fixture.Left.Session,
                        fixture.LeftRun)
                    .GetAwaiter()
                    .GetResult();

                var packets = fixture.Left.ReadPackets(2);
                Check(
                    "invalid participant does not block or receive party projection",
                    elapsed
                    && packets.Count == 2
                    && fixture.Right.AvailableByteCount == 0
                    && fixture.Instance.ParticipantEffects.GetState(
                        fixture.ClearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        fixture.LeftParticipant.RunIdentity
                            .ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        == DungeonParticipantEffectState.Committed
                    && fixture.Instance.ParticipantEffects.GetState(
                        fixture.ClearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        fixture.RightParticipant.RunIdentity
                            .ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        == DungeonParticipantEffectState.Pending,
                    ref failures);
            }
        }

        private static void VerifyPaidSelectionDetachDeadlineResume(
            ref int failures)
        {
            using (var fixture = new BarrierPartyFixture(
                leftCharacterId: 61933,
                rightCharacterId: 61934))
            {
                fixture.CommitCard(
                    fixture.LeftParticipant,
                    fixture.Left.Session,
                    CardRewardSide.Free);
                fixture.CommitCard(
                    fixture.RightParticipant,
                    fixture.Right.Session,
                    CardRewardSide.Free);
                var reserved = fixture.Coordinator.TryBeginPaidSelection(
                    fixture.Left.Session,
                    fixture.LeftRun,
                    out var reservation);

                fixture.Left.Session.Player.CurrentRun = null;
                var elapsed = fixture.Coordinator
                    .MarkNormalPhaseDeadlineElapsed(
                        fixture.Right.Session,
                        fixture.RightRun);
                fixture.Coordinator.TryProjectReadyPartyAsync(
                        fixture.Right.Session,
                        fixture.RightRun)
                    .GetAwaiter()
                    .GetResult();
                var rightPackets = fixture.Right.ReadPackets(2);

                fixture.Left.Session.Player.CurrentRun = fixture.LeftRun;
                fixture.Coordinator.RecoverParticipantAsync(
                        fixture.Left.Session)
                    .GetAwaiter()
                    .GetResult();
                var leftPackets = fixture.Left.ReadPackets(2);
                var staleReservationCancelled = fixture.Coordinator
                    .CancelPaidSelection(reservation);

                Check(
                    "paid selection detach does not block the current party",
                    reserved
                    && reservation.IsValid
                    && elapsed
                    && rightPackets.Count == 2,
                    ref failures);
                Check(
                    "resume invalidates stale paid selection and projects once",
                    leftPackets.Count == 2
                    && !staleReservationCancelled
                    && fixture.Instance.ParticipantEffects.GetState(
                        fixture.ClearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        fixture.LeftParticipant.RunIdentity
                            .ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        == DungeonParticipantEffectState.Committed
                    && fixture.Left.AvailableByteCount == 0,
                    ref failures);
            }
        }

        private static void VerifyPartialProjectionRetriesOnlyFailedParticipant(
            ref int failures)
        {
            using (var fixture = new BarrierPartyFixture(
                leftCharacterId: 61941,
                rightCharacterId: 61942,
                sendLockTimeout: TimeSpan.FromMilliseconds(150)))
            using (var sendLockHeld = new ManualResetEventSlim())
            using (var releaseSendLock = new ManualResetEventSlim())
            {
                fixture.CommitCard(
                    fixture.LeftParticipant,
                    fixture.Left.Session,
                    CardRewardSide.Free);
                fixture.CommitCard(
                    fixture.RightParticipant,
                    fixture.Right.Session,
                    CardRewardSide.Free);
                fixture.Coordinator.MarkNormalPhaseDeadlineElapsed(
                    fixture.Left.Session,
                    fixture.LeftRun);

                var blocker = HoldSendLock(
                    fixture.Left.Session,
                    sendLockHeld,
                    releaseSendLock);
                var lockWasHeld = sendLockHeld.Wait(
                    TimeSpan.FromSeconds(5));
                fixture.Coordinator.TryProjectReadyPartyAsync(
                        fixture.Right.Session,
                        fixture.RightRun)
                    .GetAwaiter()
                    .GetResult();
                var firstRightPackets = fixture.Right.ReadPackets(2);
                var leftFailed = fixture.Instance.ParticipantEffects.GetState(
                        fixture.ClearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        fixture.LeftParticipant.RunIdentity
                            .ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                    == DungeonParticipantEffectState.Failed;
                var rightCommitted = fixture.Instance.ParticipantEffects
                    .GetState(
                        fixture.ClearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        fixture.RightParticipant.RunIdentity
                            .ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                    == DungeonParticipantEffectState.Committed;

                releaseSendLock.Set();
                blocker.Wait(TimeSpan.FromSeconds(5));
                fixture.Coordinator.TryProjectReadyPartyAsync(
                        fixture.Left.Session,
                        fixture.LeftRun)
                    .GetAwaiter()
                    .GetResult();
                var retryLeftPackets = fixture.Left.ReadPackets(2);

                Check(
                    "partial party projection commits the healthy participant only",
                    lockWasHeld
                    && leftFailed
                    && rightCommitted
                    && firstRightPackets.Count == 2,
                    ref failures);
                Check(
                    "projection retry sends only the previously failed participant",
                    retryLeftPackets.Count == 2
                    && fixture.Right.AvailableByteCount == 0
                    && fixture.Instance.ParticipantEffects.GetState(
                        fixture.ClearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        fixture.LeftParticipant.RunIdentity
                            .ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        == DungeonParticipantEffectState.Committed,
                    ref failures);
            }
        }

        private static void VerifyNormalDeadlineTimerLifecycle(
            ref int failures)
        {
            using (var fixture = new BarrierPartyFixture(
                leftCharacterId: 61951,
                rightCharacterId: 61952))
            {
                fixture.CommitCard(
                    fixture.LeftParticipant,
                    fixture.Left.Session,
                    CardRewardSide.Free);
                fixture.Right.Session.Player.CurrentRun = null;
                var originalDeadline = DateTime.UtcNow
                    .AddMilliseconds(250);
                fixture.Coordinator.ScheduleNormalPhaseDeadline(
                    fixture.Left.Session,
                    fixture.LeftRun,
                    originalDeadline);
                fixture.LeftRun.Timers.TryGetCurrentTicket(
                    DungeonRunTimerKeys.AntonAwakeningNormalCardDeadline,
                    out var originalTicket);

                var suspended = fixture.LeftRun.Timers
                    .SuspendForNetworkDetach();
                Thread.Sleep(100);
                var recovered = fixture.Coordinator
                    .RecoverNormalPhaseDeadline(fixture.Left.Session);
                var hasRecoveredSnapshot = fixture.LeftRun.Timers
                    .TryGetSnapshot(
                        DungeonRunTimerKeys
                            .AntonAwakeningNormalCardDeadline,
                        out var recoveredSnapshot);
                fixture.LeftRun.Timers.TryGetCurrentTicket(
                    DungeonRunTimerKeys.AntonAwakeningNormalCardDeadline,
                    out var recoveredTicket);
                Thread.Sleep(170);
                ClockService.Instance.CheckOnce(DateTime.UtcNow);
                var projectedAtOriginalDeadline = SpinWait.SpinUntil(
                    () => fixture.Left.AvailableByteCount > 0,
                    TimeSpan.FromSeconds(1));
                var packets = projectedAtOriginalDeadline
                    ? fixture.Left.ReadPackets(2)
                    : new List<byte[]>();
                Thread.Sleep(100);

                Check(
                    "normal deadline detach and resume preserve the absolute deadline",
                    suspended == 1
                    && recovered
                    && hasRecoveredSnapshot
                    && recoveredSnapshot.DeadlineUtc == originalDeadline
                    && recoveredTicket.Generation
                        != originalTicket.Generation,
                    ref failures);
                Check(
                    "resumed normal deadline projects at the preserved deadline",
                    projectedAtOriginalDeadline
                    && packets.Count == 2,
                    ref failures);
                Check(
                    "old normal deadline ticket cannot duplicate projection",
                    packets.Count == 2
                    && !fixture.LeftRun.Timers.IsCurrent(originalTicket)
                    && fixture.Left.AvailableByteCount == 0,
                    ref failures);
                var closedRecovery = fixture.Coordinator
                    .RecoverNormalPhaseDeadline(fixture.Left.Session);
                var hasClosedSnapshot = fixture.LeftRun.Timers.TryGetSnapshot(
                    DungeonRunTimerKeys.AntonAwakeningNormalCardDeadline,
                    out var closedSnapshot);
                Check(
                    "closed normal phase cannot rearm its deadline timer",
                    !closedRecovery
                    && hasClosedSnapshot
                    && !closedSnapshot.HasDeadline,
                    ref failures);
            }
        }

        private static void VerifyMissingNormalDeadlineTimerRecovery(
            ref int failures)
        {
            using (var fixture = new BarrierPartyFixture(
                leftCharacterId: 61953,
                rightCharacterId: 61954))
            {
                var runtime = fixture.Instance.Mechanisms
                    .AntonAwakeningReward;
                var deadline = DateTime.UtcNow.AddMinutes(1);
                var recorded = runtime != null
                    && runtime.TryRecordNormalDeadline(
                        fixture.ClearFact.SourceEventId,
                        fixture.LeftParticipant.RunIdentity
                            .ParticipantIdentity,
                        deadline);
                var recovered = fixture.Coordinator
                    .RecoverNormalPhaseDeadline(fixture.Left.Session);
                var hasSnapshot = fixture.LeftRun.Timers.TryGetSnapshot(
                    DungeonRunTimerKeys.AntonAwakeningNormalCardDeadline,
                    out var snapshot);

                Check(
                    "normal deadline recovery recreates a missing timer",
                    recorded
                    && recovered
                    && hasSnapshot
                    && snapshot.DeadlineUtc == deadline
                    && snapshot.DetachPolicy
                        == RunTimerDetachPolicy.SuspendUntilResume,
                    ref failures);
            }
        }

        private static void VerifyLatePaidCardDoesNotCharge(ref int failures)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_late_paid_{Guid.NewGuid():N}.db");
            var sessionId = Guid.Empty;
            const int accountId = 61920;
            const int characterId = 61921;
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                Seed(database, accountId, characterId);
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                }

                var instance = new DungeonInstance(247, 0);
                var run = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    1,
                    DungeonRunState.Active);
                var source = DungeonEventEnvelope.Create(
                    run,
                    characterId,
                    "anton-late-paid",
                    sourceEventId: Guid.NewGuid());
                var clearFact = instance.GetOrCreateClearedFact(
                    new DungeonClearIntent(source, "selftest", 0),
                    out _);
                run.TryBeginClearCommit(clearFact);
                run.TryCompleteClearCommit(clearFact);
                run.Phase = DungeonRunPhase.CardsRevealed;
                run.CardRewards = new List<ClearRewardGenerator.CardReward>
                {
                    default,
                    default,
                    default,
                    default,
                    default,
                    new ClearRewardGenerator.CardReward
                    {
                        ItemId = 3309,
                        StackCount = 1,
                    },
                    default,
                    default,
                };
                run.FreeCardSlots = new byte[]
                {
                    0xFF, 0xFF, 0xFF, 0xFF,
                };
                run.PaidCardSlots = new byte[]
                {
                    0xFF, 0xFF, 0xFF, 0xFF,
                };
                run.PaidCardCost = 500;
                var participant = new DungeonParticipantRosterEntry(
                    characterId,
                    421,
                    run,
                    run.CaptureIdentity(),
                    new DungeonRoomIdentity(instance.Identity, 1),
                    1,
                    partySlot: 0);
                var journal = instance.ParticipantEffects;
                journal.TryFreeze(
                    clearFact.Source,
                    DungeonParticipantEffectAudience.Instance,
                    new[] { participant },
                    out _);
                journal.TryBegin(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    participant,
                    DungeonParticipantEffectKinds.DungeonClear,
                    out var clearReservation,
                    out _);
                journal.TryCommit(clearReservation);

                var sessions = new SessionDirectory();
                using (var capture = new ConnectedSession())
                {
                    capture.Session.Player.CharacterId = characterId;
                    capture.Session.Player.UserId = 421;
                    capture.Session.Player.CurrentRun = run;
                    sessionId = capture.Session.SessionId;
                    var lease = InventoryContext.Register(
                        sessionId,
                        characterId,
                        inventory);
                    lock (lease.SyncRoot)
                        lease.Inventory.SetMainVirtualCount(0, 5000);
                    sessions.Register(characterId, capture.Session);
                    var rewards = CreateRewardService(
                        new DailyResetService(database),
                        finalItemId: 3309,
                        quantity: 1);
                    var anton = new AntonAwakeningRewardCoordinator(
                        rewards,
                        new AntonAwakeningRewardGrantService(rewards),
                        sessions,
                        null,
                        new AntonNormalConquestNotificationSender(
                            new PartyPacketSender(sessions)));
                    anton.PrepareClearAsync(run, clearFact)
                        .GetAwaiter()
                        .GetResult();
                    anton.ScheduleNormalPhaseDeadline(
                        capture.Session,
                        run,
                        DateTime.UtcNow.AddMinutes(1));
                    anton.MarkNormalPhaseDeadlineElapsed(
                        capture.Session,
                        run);
                    var beforeGold = CountMainItem(lease, 0);
                    var cards = new CardRewardCoordinator(
                        new CardRewardService(),
                        sessions: sessions,
                        database: database,
                        antonRewards: anton);
                    cards.HandleSelectCard(
                            capture.Session,
                            new byte[] { 1, 0 })
                        .GetAwaiter()
                        .GetResult();
                    var afterGold = CountMainItem(lease, 0);

                    Check(
                        "paid card after normal deadline is rejected before charge",
                        beforeGold == 5000
                        && afterGold == beforeGold
                        && !CardRewardRules.IsCommitted(
                            run,
                            CardRewardSide.Paid),
                        ref failures);

                    run.Timers.CancelAll();
                    sessions.UnregisterAsync(characterId, capture.Session)
                        .GetAwaiter()
                        .GetResult();
                }
            }
            finally
            {
                if (sessionId != Guid.Empty)
                    InventoryContext.Unregister(sessionId, characterId);
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static void VerifyRejectedManualFreeKeepsAutoFlipTimer(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            run.Phase = DungeonRunPhase.CardsRevealed;
            run.CardRewards = Enumerable.Repeat(
                    default(ClearRewardGenerator.CardReward),
                    8)
                .ToList();
            run.FreeCardSlots = new byte[] { 0, 0xFF, 0xFF, 0xFF };
            run.PaidCardSlots = new byte[]
            {
                0xFF, 0xFF, 0xFF, 0xFF,
            };

            using (var capture = new ConnectedSession())
            {
                capture.Session.Player.CharacterId = 61955;
                capture.Session.Player.UserId = 455;
                capture.Session.Player.CurrentRun = run;
                var deadline = DateTime.UtcNow.AddMinutes(1);
                var ticket = run.Timers.Begin(
                    DungeonRunTimerKeys.SettlementCardAutoFlow,
                    deadline,
                    RunTimerDetachPolicy.SuspendUntilResume);
                var cards = new CardRewardCoordinator();

                cards.HandleSelectCard(
                        capture.Session,
                        new byte[] { 0, 0 })
                    .GetAwaiter()
                    .GetResult();

                Check(
                    "occupied manual free-card slot keeps auto-flip timer",
                    run.Timers.IsCurrent(ticket)
                    && run.Timers.TryGetSnapshot(
                        DungeonRunTimerKeys.SettlementCardAutoFlow,
                        out var snapshot)
                    && snapshot.DeadlineUtc == deadline
                    && !CardRewardRules.IsCommitted(
                        run,
                        CardRewardSide.Free),
                    ref failures);
                run.Timers.CancelAll();
            }
        }

        private static void VerifyCardIoRunsOutsideStateGates(
            ref int failures)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo_card_gate_io_{Guid.NewGuid():N}.db");
            var sessionId = Guid.Empty;
            const int accountId = 61960;
            const int characterId = 61961;
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                Seed(database, accountId, characterId);
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                }

                var instance = new DungeonInstance(247, 0);
                var run = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    1,
                    DungeonRunState.Active);
                var source = DungeonEventEnvelope.Create(
                    run,
                    characterId,
                    "card-io-lock-boundary",
                    sourceEventId: Guid.NewGuid());
                var clearFact = instance.GetOrCreateClearedFact(
                    new DungeonClearIntent(source, "selftest", 0),
                    out _);
                run.TryBeginClearCommit(clearFact);
                run.TryCompleteClearCommit(clearFact);
                run.Phase = DungeonRunPhase.CardsRevealed;
                run.CardRewards = new List<ClearRewardGenerator.CardReward>
                {
                    default,
                    new ClearRewardGenerator.CardReward
                    {
                        ItemId = 3309,
                        StackCount = 1,
                    },
                    default,
                    default,
                    default,
                    default,
                    default,
                    default,
                };
                run.FreeCardSlots = new byte[]
                {
                    0xFF, 0xFF, 0xFF, 0xFF,
                };
                run.PaidCardSlots = new byte[]
                {
                    0xFF, 0xFF, 0xFF, 0xFF,
                };

                using (var capture = new ConnectedSession())
                {
                    capture.Session.Player.CharacterId = characterId;
                    capture.Session.Player.UserId = 431;
                    capture.Session.Player.CurrentRun = run;
                    sessionId = capture.Session.SessionId;
                    var lease = InventoryContext.Register(
                        sessionId,
                        characterId,
                        inventory);
                    var allStateGatesAvailable = true;
                    Action observe = () =>
                    {
                        allStateGatesAvailable &= AreCardStateGatesAvailable(
                            instance,
                            run);
                    };
                    var sender = new GateObservingCardSender(observe);
                    var cards = new CardRewardCoordinator(
                        new CardRewardService(afterDurableCommit: observe),
                        sender);

                    cards.HandleSelectCard(
                            capture.Session,
                            new byte[] { 0, 0 })
                        .GetAwaiter()
                        .GetResult();

                    Check(
                        "card network, database, and inventory refresh run outside state gates",
                        allStateGatesAvailable
                        && sender.CardInfoCalls == 1
                        && sender.ItemUpdateCalls == 1
                        && CardRewardRules.IsCommitted(
                            run,
                            CardRewardSide.Free)
                        && CountMainItem(lease, 3309) == 1,
                        ref failures);
                }
            }
            finally
            {
                if (sessionId != Guid.Empty)
                    InventoryContext.Unregister(sessionId, characterId);
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static bool AreCardStateGatesAvailable(
            DungeonInstance instance,
            DungeonRun run)
        {
            var instanceGateAvailable = instance.CardRewardProjectionGate
                .Wait(0);
            if (instanceGateAvailable)
                instance.CardRewardProjectionGate.Release();
            var runGateAvailable = run.Settlement.CardProjectionGate.Wait(0);
            if (runGateAvailable)
                run.Settlement.CardProjectionGate.Release();
            var runSyncAvailable = Monitor.TryEnter(run.SyncRoot);
            if (runSyncAvailable)
                Monitor.Exit(run.SyncRoot);
            return instanceGateAvailable
                && runGateAvailable
                && runSyncAvailable;
        }

        private static void VerifyConcurrentEplpRevealKeepsCommand(
            ref int failures)
        {
            var instance = new DungeonInstance(1, 0);
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active)
            {
                Phase = DungeonRunPhase.ResultShown,
                CardRewards = Enumerable.Repeat(
                        default(ClearRewardGenerator.CardReward),
                        8)
                    .ToList(),
                FreeCardSlots = new byte[]
                {
                    0xFF, 0xFF, 0xFF, 0xFF,
                },
                PaidCardSlots = new byte[]
                {
                    0xFF, 0xFF, 0xFF, 0xFF,
                },
            };
            using (var capture = new ConnectedSession())
            using (var sender = new BlockingLayoutCardSender())
            {
                capture.Session.Player.CharacterId = 61969;
                capture.Session.Player.UserId = 469;
                capture.Session.Player.CurrentRun = run;
                var cards = new CardRewardCoordinator(sender: sender);
                var firstReveal = System.Threading.Tasks.Task.Run(
                    async () => await cards.HandleCardStartRequest(
                        capture.Session));
                var firstEntered = sender.Entered.Wait(
                    TimeSpan.FromSeconds(5));
                var eplp = cards.PrepareEplpCommand(
                    capture.Session,
                    new byte[] { 1, 2 });
                sender.Release.Set();
                System.Threading.Tasks.Task.WaitAll(firstReveal, eplp);
                var decision = eplp.Result;

                Check(
                    "concurrent card reveal does not swallow the pending EPLP command",
                    firstEntered
                    && run.SettlementState
                        == DungeonSettlementState.CardsRevealed
                    && decision.Ready
                    && decision.State == 1
                    && decision.Option == 2,
                    ref failures);
                run.Timers.CancelAll();
            }
        }

        private static void VerifyPartyPacketBatchIsolation(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var leftRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var rightRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var leftParticipant = new DungeonParticipantRosterEntry(
                61801,
                301,
                leftRun,
                leftRun.CaptureIdentity(),
                room,
                1,
                partySlot: 0);
            var rightParticipant = new DungeonParticipantRosterEntry(
                61802,
                302,
                rightRun,
                rightRun.CaptureIdentity(),
                room,
                1,
                partySlot: 1);
            IReadOnlyList<DungeonParticipantRosterEntry> roster =
                new[] { leftParticipant, rightParticipant };
            var sessions = new SessionDirectory();

            using (var left = new ConnectedSession())
            using (var right = new ConnectedSession())
            {
                left.Session.Player.CharacterId = leftParticipant.CharacterId;
                left.Session.Player.UserId =
                    leftParticipant.ParticipantUserId;
                left.Session.Player.CurrentRun = leftRun;
                right.Session.Player.CharacterId = rightParticipant.CharacterId;
                right.Session.Player.UserId =
                    rightParticipant.ParticipantUserId;
                right.Session.Player.CurrentRun = rightRun;
                sessions.Register(leftParticipant.CharacterId, left.Session);
                sessions.Register(rightParticipant.CharacterId, right.Session);
                var sender = new PartyPacketSender(sessions);

                var mutablePackets = new List<byte[]>
                {
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketTypeA21
                            .ANTON_AWAKENING_MODE_REWARD,
                        new byte[] { 0x11, 0x12, 0x13 }),
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketTypeA21.EXERCISE_MODE_CLEAR,
                        new byte[] { 0x21, 0x22, 0x23, 0x24 }),
                };
                var expectedPackets = mutablePackets
                    .Select(packet => packet.ToArray())
                    .ToArray();
                using (var sendLockHeld = new ManualResetEventSlim())
                using (var releaseSendLock = new ManualResetEventSlim())
                {
                    var blocker = HoldSendLock(
                        right.Session,
                        sendLockHeld,
                        releaseSendLock);
                    var lockWasHeld = sendLockHeld.Wait(
                        TimeSpan.FromSeconds(5));
                    var sending = sender.SendToPartyAsync(
                        roster,
                        mutablePackets);
                    var leftPackets = left.ReadPackets(2);
                    var queuedBehindRightLock = !sending.IsCompleted;
                    mutablePackets[0][15] ^= 0x7F;
                    mutablePackets[1] = GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketTypeA21.DUNGEON_PERMISSION,
                        new byte[] { 0x31, 0x32, 0x33, 0x34 });
                    releaseSendLock.Set();
                    System.Threading.Tasks.Task.WaitAll(
                        new System.Threading.Tasks.Task[]
                        {
                            blocker,
                            sending,
                        },
                        TimeSpan.FromSeconds(10));
                    var result = sending.GetAwaiter().GetResult();
                    List<byte[]> rightPackets = null;
                    try
                    {
                        rightPackets = right.ReadPackets(2);
                    }
                    catch (TimeoutException)
                    {
                        // The old per-envelope implementation can write the
                        // first frame, observe the mutated list, and abort the
                        // second frame. Keep that outcome as an assertion
                        // failure instead of terminating the whole selftest.
                    }
                    Check(
                        "party sender freezes packet list and bytes before awaits",
                        lockWasHeld
                        && queuedBehindRightLock
                        && result.Succeeded.Count == 2
                        && rightPackets != null
                        && leftPackets[0].SequenceEqual(expectedPackets[0])
                        && leftPackets[1].SequenceEqual(expectedPackets[1])
                        && rightPackets[0].SequenceEqual(expectedPackets[0])
                        && rightPackets[1].SequenceEqual(expectedPackets[1]),
                        ref failures);
                }

                var packets = expectedPackets
                    .Select(packet => packet.ToArray())
                    .ToArray();
                using (var sendLockHeld = new ManualResetEventSlim())
                using (var releaseSendLock = new ManualResetEventSlim())
                {
                    var blocker = HoldSendLock(
                        left.Session,
                        sendLockHeld,
                        releaseSendLock);
                    var lockWasHeld = sendLockHeld.Wait(
                        TimeSpan.FromSeconds(5));
                    var sending = new PartyPacketSender(
                            sessions,
                            TimeSpan.FromMilliseconds(150))
                        .SendToPartyAsync(roster, packets);
                    var rightReceivedBeforeLeftReleased = SpinWait.SpinUntil(
                        () => right.AvailableByteCount > 0,
                        TimeSpan.FromSeconds(2));
                    var completedWhileLeftHeld = sending.Wait(
                        TimeSpan.FromSeconds(3));
                    releaseSendLock.Set();
                    System.Threading.Tasks.Task.WaitAll(
                        new System.Threading.Tasks.Task[]
                        {
                            blocker,
                            sending,
                        },
                        TimeSpan.FromSeconds(10));
                    var result = sending.GetAwaiter().GetResult();
                    var rightPackets = right.ReadPackets(2);
                    Check(
                        "blocked participant times out without blocking peer batch",
                        lockWasHeld
                        && rightReceivedBeforeLeftReleased
                        && completedWhileLeftHeld
                        && result.Succeeded.Count == 1
                        && ReferenceEquals(
                            result.Succeeded[0],
                            rightParticipant)
                        && result.Failed.Count == 1
                        && ReferenceEquals(
                            result.Failed[0],
                            leftParticipant)
                        && left.AvailableByteCount == 0,
                        ref failures);
                    Check(
                        "single wire batch remains two ordered A21 envelopes",
                        BitConverter.ToUInt16(rightPackets[0], 1)
                            == (ushort)NotiPacketTypeA21
                                .ANTON_AWAKENING_MODE_REWARD
                        && BitConverter.ToUInt16(rightPackets[1], 1)
                            == (ushort)NotiPacketTypeA21.EXERCISE_MODE_CLEAR,
                        ref failures);
                }

                var emptyElementResult = sender.SendToPartyAsync(
                        roster,
                        new[]
                        {
                            Array.Empty<byte>(),
                            expectedPackets[0],
                        })
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "zero-length packet fails the whole batch without writing",
                    emptyElementResult.Succeeded.Count == 0
                    && emptyElementResult.Failed.Count == 2
                    && left.AvailableByteCount == 0
                    && right.AvailableByteCount == 0,
                    ref failures);

                sessions.UnregisterAsync(
                        leftParticipant.CharacterId,
                        left.Session)
                    .GetAwaiter()
                    .GetResult();
                sessions.UnregisterAsync(
                        rightParticipant.CharacterId,
                        right.Session)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private static System.Threading.Tasks.Task<bool> HoldSendLock(
            EnhancedClientSession session,
            ManualResetEventSlim sendLockHeld,
            ManualResetEventSlim releaseSendLock)
            => System.Threading.Tasks.Task.Run(() =>
                session.TrySendPacketAsync(
                        Array.Empty<byte>(),
                        CancellationToken.None,
                        () =>
                        {
                            sendLockHeld.Set();
                            releaseSendLock.Wait(TimeSpan.FromSeconds(10));
                            return false;
                        })
                    .GetAwaiter()
                    .GetResult());

        private static void VerifyInFlightWriteTimeoutIsolation(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var blockedRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var healthyRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var blockedParticipant = new DungeonParticipantRosterEntry(
                61901,
                401,
                blockedRun,
                blockedRun.CaptureIdentity(),
                room,
                1,
                partySlot: 0);
            var healthyParticipant = new DungeonParticipantRosterEntry(
                61902,
                402,
                healthyRun,
                healthyRun.CaptureIdentity(),
                room,
                1,
                partySlot: 1);
            IReadOnlyList<DungeonParticipantRosterEntry> roster =
                new[] { blockedParticipant, healthyParticipant };
            var sessions = new SessionDirectory();

            using (var blocked = new ConnectedSession())
            using (var healthy = new ConnectedSession())
            {
                blocked.Session.Player.CharacterId =
                    blockedParticipant.CharacterId;
                blocked.Session.Player.UserId =
                    blockedParticipant.ParticipantUserId;
                blocked.Session.Player.CurrentRun = blockedRun;
                healthy.Session.Player.CharacterId =
                    healthyParticipant.CharacterId;
                healthy.Session.Player.UserId =
                    healthyParticipant.ParticipantUserId;
                healthy.Session.Player.CurrentRun = healthyRun;
                sessions.Register(
                    blockedParticipant.CharacterId,
                    blocked.Session);
                sessions.Register(
                    healthyParticipant.CharacterId,
                    healthy.Session);

                blocked.ConstrainSocketBuffers(1024);
                var blockedBeforeBatch =
                    blocked.FillWriterUntilWouldBlock();
                var body = new byte[16 * 1024];
                body[0] = 0x41;
                body[body.Length - 1] = 0x42;
                var packet = GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21
                        .ANTON_AWAKENING_MODE_REWARD,
                    body);
                var healthyRead = System.Threading.Tasks.Task.Run(
                    () => healthy.ReadBytes(packet.Length));
                var sending = new PartyPacketSender(
                        sessions,
                        TimeSpan.FromMilliseconds(500))
                    .SendToPartyAsync(roster, new[] { packet });
                var sendCompleted = sending.Wait(TimeSpan.FromSeconds(5));
                if (!sendCompleted)
                {
                    blocked.Session.TcpClient.Close();
                    sending.Wait(TimeSpan.FromSeconds(5));
                }
                var readCompleted = SpinWait.SpinUntil(
                    () => healthyRead.IsCompleted,
                    TimeSpan.FromSeconds(5));
                if (!readCompleted)
                    healthy.Session.TcpClient.Close();

                var result = sendCompleted
                    ? sending.GetAwaiter().GetResult()
                    : null;
                var received = healthyRead.IsCompletedSuccessfully
                    ? healthyRead.Result
                    : null;
                Check(
                    "in-flight write timeout retires only blocked transport",
                    blockedBeforeBatch
                    && sendCompleted
                    && result != null
                    && result.Succeeded.Count == 1
                    && ReferenceEquals(
                        result.Succeeded[0],
                        healthyParticipant)
                    && result.Failed.Count == 1
                    && ReferenceEquals(
                        result.Failed[0],
                        blockedParticipant)
                    && received != null
                    && received.SequenceEqual(packet)
                    && !blocked.Session.TcpClient.Connected,
                    ref failures);

                sessions.UnregisterAsync(
                        blockedParticipant.CharacterId,
                        blocked.Session)
                    .GetAwaiter()
                    .GetResult();
                sessions.UnregisterAsync(
                        healthyParticipant.CharacterId,
                        healthy.Session)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private static void VerifyGenerationSafePartyPacketSender(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var leftRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var rightRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var leftParticipant = new DungeonParticipantRosterEntry(
                61901,
                101,
                leftRun,
                leftRun.CaptureIdentity(),
                room,
                1,
                partySlot: 0);
            var rightParticipant = new DungeonParticipantRosterEntry(
                61902,
                202,
                rightRun,
                rightRun.CaptureIdentity(),
                room,
                1,
                partySlot: 1);
            IReadOnlyList<DungeonParticipantRosterEntry> reversedRoster =
                new[] { rightParticipant, leftParticipant };
            var entries = new[]
            {
                new Network.Builders.AntonAwakeningRewardEntry(
                    101, 0, 0, 10157831, 1),
                new Network.Builders.AntonAwakeningRewardEntry(
                    202, 0, 2, 10157833, 1),
            };
            var sessions = new SessionDirectory();

            using (var left = new ConnectedSession())
            using (var right = new ConnectedSession())
            {
                left.Session.Player.CharacterId = leftParticipant.CharacterId;
                left.Session.Player.UserId =
                    leftParticipant.ParticipantUserId;
                left.Session.Player.CurrentRun = leftRun;
                right.Session.Player.CharacterId = rightParticipant.CharacterId;
                right.Session.Player.UserId =
                    rightParticipant.ParticipantUserId;
                right.Session.Player.CurrentRun = rightRun;
                sessions.Register(leftParticipant.CharacterId, left.Session);
                sessions.Register(rightParticipant.CharacterId, right.Session);

                var sender = new AntonNormalConquestNotificationSender(
                    new PartyPacketSender(sessions));
                var result = sender.SendAntonAwakeningRewardToPartyAsync(
                        reversedRoster,
                        entries)
                    .GetAwaiter()
                    .GetResult();
                var leftPackets = left.ReadPackets(2);
                var rightPackets = right.ReadPackets(2);
                Check(
                    "both participants receive projection in stable roster order",
                    result.Succeeded.Count == 2
                    && result.Failed.Count == 0
                    && ReferenceEquals(
                        result.Succeeded[0],
                        leftParticipant)
                    && ReferenceEquals(
                        result.Succeeded[1],
                        rightParticipant),
                    ref failures);
                Check(
                    "party receives byte-identical packet batch",
                    leftPackets.Count == 2
                    && rightPackets.Count == 2
                    && leftPackets[0].SequenceEqual(rightPackets[0])
                    && leftPackets[1].SequenceEqual(rightPackets[1]),
                    ref failures);

                var emptyBatchResult = new PartyPacketSender(sessions)
                    .SendToPartyAsync(
                        reversedRoster,
                        Array.Empty<byte[]>())
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "empty packet batch fails every participant without writing",
                    emptyBatchResult.Succeeded.Count == 0
                    && emptyBatchResult.Failed.Count == 2
                    && ReferenceEquals(
                        emptyBatchResult.Failed[0],
                        leftParticipant)
                    && ReferenceEquals(
                        emptyBatchResult.Failed[1],
                        rightParticipant)
                    && left.AvailableByteCount == 0
                    && right.AvailableByteCount == 0,
                    ref failures);

                var mutableSucceeded = new List<
                    DungeonParticipantRosterEntry> { leftParticipant };
                var mutableFailed = new List<
                    DungeonParticipantRosterEntry> { rightParticipant };
                var frozenResult = new PartyPacketSendResult(
                    mutableSucceeded,
                    mutableFailed);
                mutableSucceeded.Clear();
                mutableFailed.Clear();
                Check(
                    "packet result owns frozen roster snapshots",
                    frozenResult.Succeeded.Count == 1
                    && ReferenceEquals(
                        frozenResult.Succeeded[0],
                        leftParticipant)
                    && frozenResult.Failed.Count == 1
                    && ReferenceEquals(
                        frozenResult.Failed[0],
                        rightParticipant),
                    ref failures);

                right.Session.Player.CurrentRun = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    rightRun.RunGeneration + 1,
                    DungeonRunState.Active);
                var staleResult = sender
                    .SendAntonAwakeningRewardToPartyAsync(
                        reversedRoster,
                        entries)
                    .GetAwaiter()
                    .GetResult();
                var validRetryPackets = left.ReadPackets(2);
                Check(
                    "stale participant fails without interrupting valid peer",
                    staleResult.Succeeded.Count == 1
                    && ReferenceEquals(
                        staleResult.Succeeded[0],
                        leftParticipant)
                    && staleResult.Failed.Count == 1
                    && ReferenceEquals(
                        staleResult.Failed[0],
                        rightParticipant)
                    && validRetryPackets.Count == 2
                    && right.AvailableByteCount == 0,
                    ref failures);

                right.Session.Player.CurrentRun = rightRun;
                using (var sendLockHeld = new ManualResetEventSlim())
                using (var releaseSendLock = new ManualResetEventSlim())
                {
                    var blocker = System.Threading.Tasks.Task.Run(() =>
                        right.Session.TrySendPacketAsync(
                                Array.Empty<byte>(),
                                CancellationToken.None,
                                () =>
                                {
                                    sendLockHeld.Set();
                                    releaseSendLock.Wait(
                                        TimeSpan.FromSeconds(10));
                                    return false;
                                })
                            .GetAwaiter()
                            .GetResult());
                    var lockWasHeld = sendLockHeld.Wait(
                        TimeSpan.FromSeconds(5));
                    var queued = new PartyPacketSender(sessions)
                        .SendToPartyAsync(
                            new[] { rightParticipant },
                            new[] { leftPackets[0], leftPackets[1] });
                    var queuedBehindSendLock = !queued.IsCompleted;
                    right.Session.Player.CurrentRun = new DungeonRun(
                        instance,
                        DungeonIdentityGenerator.NextRunId(),
                        rightRun.RunGeneration + 1,
                        DungeonRunState.Active);
                    releaseSendLock.Set();
                    System.Threading.Tasks.Task.WaitAll(
                        new System.Threading.Tasks.Task[] { blocker, queued },
                        TimeSpan.FromSeconds(10));
                    var queuedResult = queued.GetAwaiter().GetResult();
                    Check(
                        "send-lock queued stale generation writes no old packet",
                        lockWasHeld
                        && queuedBehindSendLock
                        && queuedResult.Succeeded.Count == 0
                        && queuedResult.Failed.Count == 1
                        && ReferenceEquals(
                            queuedResult.Failed[0],
                            rightParticipant)
                        && right.AvailableByteCount == 0,
                        ref failures);
                }

                sessions.UnregisterAsync(
                        leftParticipant.CharacterId,
                        left.Session)
                    .GetAwaiter()
                    .GetResult();
                sessions.UnregisterAsync(
                        rightParticipant.CharacterId,
                        right.Session)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private static void VerifyNonRewardableDungeonDoesNotPrepare(
            ref int failures)
        {
            var instance = new DungeonInstance(4108, 0);
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var source = DungeonEventEnvelope.Create(
                run,
                63501,
                "non-rewardable-sequential",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            run.TryBeginClearCommit(clearFact);
            run.TryCompleteClearCommit(clearFact);
            var participant = new DungeonParticipantRosterEntry(
                63501,
                901,
                run,
                run.CaptureIdentity(),
                new DungeonRoomIdentity(instance.Identity, 1),
                1,
                partySlot: 0);
            instance.ParticipantEffects.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                new[] { participant },
                out _);

            var service = new AntonAwakeningDailyCardService(
                null,
                _ => null,
                _ => 0);
            var coordinator = new AntonAwakeningRewardCoordinator(
                service,
                new AntonAwakeningRewardGrantService(service),
                null,
                null,
                new AntonNormalConquestNotificationSender());
            coordinator.PrepareClearAsync(run, clearFact)
                .GetAwaiter()
                .GetResult();
            Check(
                "non-rewardable sequential dungeon does not prepare Anton rewards",
                instance.Mechanisms.AntonAwakeningReward == null,
                ref failures);
        }

        private static void VerifyPreparationPlanningRunsOutsideProjectionGate(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var roster = BuildRoster(instance, 4, characterIdBase: 63600);
            var sourceRun = roster[0].Run;
            var source = DungeonEventEnvelope.Create(
                sourceRun,
                roster[0].CharacterId,
                "anton-plan-lock-boundary",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            sourceRun.TryBeginClearCommit(clearFact);
            sourceRun.TryCompleteClearCommit(clearFact);
            instance.ParticipantEffects.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                roster,
                out _);

            using (var loaderEntered = new ManualResetEventSlim())
            using (var releaseLoader = new ManualResetEventSlim())
            {
                var rollCalls = 0;
                var rewards = new AntonAwakeningDailyCardService(
                    null,
                    _ =>
                    {
                        loaderEntered.Set();
                        releaseLoader.Wait(TimeSpan.FromSeconds(10));
                        return BuildUpgradableLegacy((90001, 1, 1));
                    },
                    _ =>
                    {
                        Interlocked.Increment(ref rollCalls);
                        return 0;
                    });
                var coordinator = new AntonAwakeningRewardCoordinator(
                    rewards,
                    new AntonAwakeningRewardGrantService(rewards),
                    null,
                    null,
                    new AntonNormalConquestNotificationSender());
                System.Threading.Tasks.Task first = null;
                System.Threading.Tasks.Task second = null;
                var planningStarted = false;
                var gateAvailable = false;
                try
                {
                    first = System.Threading.Tasks.Task.Run(() =>
                        coordinator.PrepareClearAsync(sourceRun, clearFact)
                            .GetAwaiter()
                            .GetResult());
                    planningStarted = loaderEntered.Wait(
                        TimeSpan.FromSeconds(5));
                    second = System.Threading.Tasks.Task.Run(() =>
                        coordinator.PrepareClearAsync(sourceRun, clearFact)
                            .GetAwaiter()
                            .GetResult());
                    gateAvailable = instance.CardRewardProjectionGate.Wait(
                        TimeSpan.FromSeconds(1));
                    if (gateAvailable)
                        instance.CardRewardProjectionGate.Release();
                }
                finally
                {
                    releaseLoader.Set();
                    if (first != null && second != null)
                    {
                        System.Threading.Tasks.Task.WaitAll(
                            new[] { first, second },
                            TimeSpan.FromSeconds(10));
                    }
                }

                var runtime = instance.Mechanisms.AntonAwakeningReward;
                Check(
                    "STK planning runs outside the instance card projection gate",
                    planningStarted && gateAvailable,
                    ref failures);
                Check(
                    "concurrent clear preparation evaluates one four-member plan",
                    first?.IsCompletedSuccessfully == true
                    && second?.IsCompletedSuccessfully == true
                    && rollCalls == 8
                    && runtime != null
                    && runtime.TryGetPlan(
                        clearFact.SourceEventId,
                        out var plan)
                    && plan.Entries.Count == 4,
                    ref failures);
            }
        }

        private static void VerifyStalePreparationIsNotPublished(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var roster = BuildRoster(instance, 1, characterIdBase: 63700);
            var sourceRun = roster[0].Run;
            var source = DungeonEventEnvelope.Create(
                sourceRun,
                roster[0].CharacterId,
                "anton-stale-plan",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            sourceRun.TryBeginClearCommit(clearFact);
            sourceRun.TryCompleteClearCommit(clearFact);
            instance.ParticipantEffects.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                roster,
                out _);

            using (var rollEntered = new ManualResetEventSlim())
            using (var releaseRoll = new ManualResetEventSlim())
            {
                var rollCalls = 0;
                var rewards = new AntonAwakeningDailyCardService(
                    null,
                    _ => BuildUpgradableLegacy((90001, 1, 1)),
                    _ =>
                    {
                        var call = Interlocked.Increment(ref rollCalls);
                        if (call == 1)
                        {
                            rollEntered.Set();
                            releaseRoll.Wait(TimeSpan.FromSeconds(10));
                        }
                        return 0;
                    });
                var coordinator = new AntonAwakeningRewardCoordinator(
                    rewards,
                    new AntonAwakeningRewardGrantService(rewards),
                    null,
                    null,
                    new AntonNormalConquestNotificationSender());
                var prepare = System.Threading.Tasks.Task.Run(() =>
                    coordinator.PrepareClearAsync(sourceRun, clearFact)
                        .GetAwaiter()
                        .GetResult());
                var planningStarted = rollEntered.Wait(
                    TimeSpan.FromSeconds(5));
                var gateAvailable = instance.CardRewardProjectionGate.Wait(
                    TimeSpan.FromSeconds(1));
                try
                {
                    sourceRun.TryBeginEnding();
                }
                finally
                {
                    if (gateAvailable)
                        instance.CardRewardProjectionGate.Release();
                    releaseRoll.Set();
                }
                prepare.Wait(TimeSpan.FromSeconds(10));

                var replacementRun = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    sourceRun.RunGeneration + 1,
                    DungeonRunState.Active);
                coordinator.PrepareClearAsync(replacementRun, clearFact)
                    .GetAwaiter()
                    .GetResult();
                var runtime = instance.Mechanisms.AntonAwakeningReward;
                Check(
                    "planning completion for an ending run is not published",
                    planningStarted
                    && gateAvailable
                    && prepare.IsCompletedSuccessfully
                    && rollCalls == 2
                    && instance.State == DungeonInstanceState.Cleared
                    && runtime != null
                    && !runtime.TryGetPlan(clearFact.SourceEventId, out _),
                    ref failures);
            }
        }

        private static void VerifyProjectionJournalRecovery(ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var source = DungeonEventEnvelope.Create(
                run,
                63201,
                "anton-projection-journal-recovery",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            run.TryBeginClearCommit(clearFact);
            run.TryCompleteClearCommit(clearFact);
            var participant = new DungeonParticipantRosterEntry(
                63201,
                405,
                run,
                run.CaptureIdentity(),
                new DungeonRoomIdentity(instance.Identity, 1),
                1,
                partySlot: 0);
            var journal = instance.ParticipantEffects;
            journal.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                new[] { participant },
                out _);
            journal.TryBegin(
                clearFact.SourceEventId,
                DungeonParticipantEffectAudience.Instance,
                participant,
                DungeonParticipantEffectKinds.DungeonClear,
                out var clearReservation,
                out _);
            journal.TryCommit(clearReservation);
            run.Effects.TryReserve(
                CardRewardRules.GetEffectId(run, CardRewardSide.Free),
                out var freeReservation);
            run.Effects.TryCommit(freeReservation);

            var rewards = CreateRewardService(
                dailyReset: null,
                finalItemId: 3309,
                quantity: 2);
            var sessions = new SessionDirectory();
            using (var capture = new ConnectedSession())
            {
                capture.Session.Player.CharacterId = participant.CharacterId;
                capture.Session.Player.UserId = participant.ParticipantUserId;
                capture.Session.Player.CurrentRun = run;
                sessions.Register(participant.CharacterId, capture.Session);
                var coordinator = new AntonAwakeningRewardCoordinator(
                    rewards,
                    new AntonAwakeningRewardGrantService(rewards),
                    sessions,
                    null,
                    new AntonNormalConquestNotificationSender(
                        new PartyPacketSender(sessions)));
                coordinator.PrepareClearAsync(run, clearFact)
                    .GetAwaiter()
                    .GetResult();

                var runtime = instance.Mechanisms.AntonAwakeningReward;
                var frozenDeadline = DateTime.UtcNow.AddMinutes(2);
                var deadlineRecorded = runtime != null
                    && runtime.TryRecordProjectionDeadline(
                        clearFact.SourceEventId,
                        participant.RunIdentity.ParticipantIdentity,
                        frozenDeadline);
                coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();
                coordinator.MarkNormalPhaseDeadlineElapsed(
                    capture.Session,
                    run);
                coordinator.TryProjectReadyPartyAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();

                Check(
                    "sent projection with lost journal reservation reuses its original deadline",
                    deadlineRecorded
                    && capture.AvailableByteCount == 0
                    && journal.GetState(
                        clearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        participant.RunIdentity.ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        == DungeonParticipantEffectState.Committed
                    && run.Timers.TryGetSnapshot(
                        DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                        out var timerSnapshot)
                    && timerSnapshot.DeadlineUtc == frozenDeadline,
                    ref failures);

                run.Timers.Cancel(
                    DungeonRunTimerKeys.AntonAwakeningPostRevealGrant);
                sessions.UnregisterAsync(
                        participant.CharacterId,
                        capture.Session)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private static void VerifyTimerGrantAfterProjection(ref int failures)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_delayed_grant_{Guid.NewGuid():N}.db");
            var sessionId = Guid.Empty;
            const int accountId = 63100;
            const int characterId = 63101;
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                Seed(database, accountId, characterId);
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                }

                var instance = new DungeonInstance(247, 0);
                var run = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    1,
                    DungeonRunState.Active);
                var source = DungeonEventEnvelope.Create(
                    run,
                    characterId,
                    "anton-delayed-grant",
                    sourceEventId: Guid.NewGuid());
                var clearFact = instance.GetOrCreateClearedFact(
                    new DungeonClearIntent(source, "selftest", 0),
                    out _);
                run.TryBeginClearCommit(clearFact);
                run.TryCompleteClearCommit(clearFact);
                var participant = new DungeonParticipantRosterEntry(
                    characterId,
                    404,
                    run,
                    run.CaptureIdentity(),
                    new DungeonRoomIdentity(instance.Identity, 1),
                    1,
                    partySlot: 0);
                instance.ParticipantEffects.TryFreeze(
                    clearFact.Source,
                    DungeonParticipantEffectAudience.Instance,
                    new[] { participant },
                    out _);
                instance.ParticipantEffects.TryBegin(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    participant,
                    DungeonParticipantEffectKinds.DungeonClear,
                    out var clearReservation,
                    out _);
                instance.ParticipantEffects.TryCommit(clearReservation);
                run.Effects.TryReserve(
                    CardRewardRules.GetEffectId(run, CardRewardSide.Free),
                    out var freeReservation);
                run.Effects.TryCommit(freeReservation);

                var daily = CreateRewardService(
                    new DailyResetService(database),
                    finalItemId: 3309,
                    quantity: 3);
                var sessions = new SessionDirectory();
                using (var capture = new ConnectedSession())
                {
                    capture.Session.Player.CharacterId = characterId;
                    capture.Session.Player.UserId = 404;
                    capture.Session.Player.CurrentRun = run;
                    sessionId = capture.Session.SessionId;
                    var lease = InventoryContext.Register(
                        sessionId,
                        characterId,
                        inventory);
                    sessions.Register(characterId, capture.Session);
                    var coordinator = new AntonAwakeningRewardCoordinator(
                        daily,
                        new AntonAwakeningRewardGrantService(daily),
                        sessions,
                        null,
                        new AntonNormalConquestNotificationSender(
                            new PartyPacketSender(sessions)),
                        postRevealGrantDelay: TimeSpan.FromMilliseconds(300));

                    coordinator.PrepareClearAsync(run, clearFact)
                        .GetAwaiter()
                        .GetResult();
                    coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                        .GetAwaiter()
                        .GetResult();
                    coordinator.MarkNormalPhaseDeadlineElapsed(
                        capture.Session,
                        run);
                    coordinator.TryProjectReadyPartyAsync(capture.Session, run)
                        .GetAwaiter()
                        .GetResult();
                    var projectionPackets = capture.ReadPackets(2);
                    Check(
                        "Anton inventory and daily claim remain absent before deadline",
                        projectionPackets.Count == 2
                        && BitConverter.ToUInt32(projectionPackets[0], 26)
                            == 3309
                        && BitConverter.ToUInt32(projectionPackets[0], 30)
                            == 3
                        && CountMainItem(lease, 3309) == 0
                        && !daily.HasClaimedRewardToday(characterId, 41, 247),
                        ref failures);

                    ClockService.Instance.CheckOnce(
                        DateTime.UtcNow.AddSeconds(1));
                    var grantDeadline = DateTime.UtcNow.AddSeconds(3);
                    while (!daily.HasClaimedRewardToday(characterId, 41, 247)
                           && DateTime.UtcNow < grantDeadline)
                    {
                        Thread.Sleep(25);
                    }
                    var grantedCount = CountMainItem(lease, 3309);
                    Check(
                        "Anton timer commits the frozen reward and daily claim once",
                        daily.HasClaimedRewardToday(characterId, 41, 247)
                        && grantedCount == 3
                        && instance.ParticipantEffects.GetState(
                            clearFact.SourceEventId,
                            DungeonParticipantEffectAudience.Instance,
                            participant.RunIdentity.ParticipantIdentity,
                            DungeonParticipantEffectKinds
                                .AntonAwakeningAutoReward)
                            == DungeonParticipantEffectState.Committed,
                        ref failures);

                    coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                        .GetAwaiter()
                        .GetResult();
                    coordinator.TryProjectReadyPartyAsync(capture.Session, run)
                        .GetAwaiter()
                        .GetResult();
                    Thread.Sleep(100);
                    Check(
                        "committed Anton reward ignores duplicate free-card callbacks",
                        CountMainItem(lease, 3309) == grantedCount,
                        ref failures);
                    run.Timers.Cancel(
                        DungeonRunTimerKeys.AntonAwakeningPostRevealGrant);
                    sessions.UnregisterAsync(characterId, capture.Session)
                        .GetAwaiter()
                        .GetResult();
                }
            }
            finally
            {
                if (sessionId != Guid.Empty)
                    InventoryContext.Unregister(sessionId, characterId);
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static void VerifyDelayedProjectionState(ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var source = DungeonEventEnvelope.Create(
                run,
                63001,
                "anton-delayed-projection",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            run.TryBeginClearCommit(clearFact);
            run.TryCompleteClearCommit(clearFact);
            var participant = new DungeonParticipantRosterEntry(
                63001,
                303,
                run,
                run.CaptureIdentity(),
                room,
                1,
                partySlot: 0);
            var roster = new[] { participant };
            var journal = instance.ParticipantEffects;
            journal.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                roster,
                out _);
            journal.TryBegin(
                clearFact.SourceEventId,
                DungeonParticipantEffectAudience.Instance,
                participant,
                DungeonParticipantEffectKinds.DungeonClear,
                out var clearReservation,
                out _);
            journal.TryCommit(clearReservation);

            var rewards = CreateRewardService(
                dailyReset: null,
                finalItemId: 3309,
                quantity: 2);
            var sessions = new SessionDirectory();
            using (var capture = new ConnectedSession())
            {
                capture.Session.Player.CharacterId = participant.CharacterId;
                capture.Session.Player.UserId = participant.ParticipantUserId;
                capture.Session.Player.CurrentRun = run;
                sessions.Register(participant.CharacterId, capture.Session);
                var coordinator = new AntonAwakeningRewardCoordinator(
                    rewards,
                    new AntonAwakeningRewardGrantService(rewards),
                    sessions,
                    null,
                    new AntonNormalConquestNotificationSender(
                        new PartyPacketSender(sessions)));

                coordinator.PrepareClearAsync(run, clearFact)
                    .GetAwaiter()
                    .GetResult();
                var runtime = instance.Mechanisms.AntonAwakeningReward;
                coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();
                coordinator.TryProjectReadyPartyAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "Anton projection waits for committed free-card reward",
                    runtime != null
                    && !runtime.TryGetProjectionDeadline(
                        clearFact.SourceEventId,
                        participant.RunIdentity.ParticipantIdentity,
                        out _)
                    && journal.GetState(
                        clearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        participant.RunIdentity.ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        == DungeonParticipantEffectState.Pending
                    && capture.AvailableByteCount == 0,
                    ref failures);

                run.Effects.TryReserve(
                    CardRewardRules.GetEffectId(run, CardRewardSide.Free),
                    out var freeReservation);
                run.Effects.TryCommit(freeReservation);
                var projectedAt = DateTime.UtcNow;
                coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "manual free-card commit cannot project before normal deadline",
                    capture.AvailableByteCount == 0,
                    ref failures);
                coordinator.MarkNormalPhaseDeadlineElapsed(
                    capture.Session,
                    run);
                coordinator.TryProjectReadyPartyAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();
                var packets = capture.ReadPackets(2);
                var projectedUntil = DateTime.UtcNow.AddSeconds(11);
                var hasDeadline = runtime.TryGetProjectionDeadline(
                    clearFact.SourceEventId,
                    participant.RunIdentity.ParticipantIdentity,
                    out var deadlineUtc);
                Check(
                    "committed free card projects 0x0319 then 0x00FF and arms 11-second timer",
                    AntonAwakeningRewardCoordinator.PostRevealGrantDelay
                        == TimeSpan.FromSeconds(11)
                    && packets.Count == 2
                    && BitConverter.ToUInt16(packets[0], 1)
                        == (ushort)NotiPacketTypeA21
                            .ANTON_AWAKENING_MODE_REWARD
                    && BitConverter.ToUInt16(packets[1], 1)
                        == (ushort)NotiPacketTypeA21.EXERCISE_MODE_CLEAR
                    && hasDeadline
                    && deadlineUtc >= projectedAt.AddSeconds(11)
                    && deadlineUtc <= projectedUntil
                    && journal.GetState(
                        clearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        participant.RunIdentity.ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        == DungeonParticipantEffectState.Committed
                    && run.Timers.TryGetSnapshot(
                        DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                        out var timerSnapshot)
                    && timerSnapshot.DeadlineUtc == deadlineUtc
                    && timerSnapshot.DetachPolicy
                        == RunTimerDetachPolicy.SuspendUntilResume,
                    ref failures);

                coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();
                coordinator.TryProjectReadyPartyAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "duplicate free-card callback does not reproject or replace deadline",
                    capture.AvailableByteCount == 0
                    && runtime.TryGetProjectionDeadline(
                        clearFact.SourceEventId,
                        participant.RunIdentity.ParticipantIdentity,
                        out var replayDeadline)
                    && replayDeadline == deadlineUtc,
                    ref failures);

                var suspended = run.Timers.SuspendForNetworkDetach();
                var resumed = run.Timers.TryResume(
                    DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                    out _,
                    out var resumedDeadline);
                Check(
                    "network detach and resume preserve the original Anton deadline",
                    suspended == 1
                    && resumed
                    && resumedDeadline == deadlineUtc,
                    ref failures);
                run.Timers.Cancel(
                    DungeonRunTimerKeys.AntonAwakeningPostRevealGrant);
                sessions.UnregisterAsync(
                        participant.CharacterId,
                        capture.Session)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private static void VerifyStableInstancePlanAndJournal(ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var runA = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var runB = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var roster = new List<DungeonParticipantRosterEntry>
            {
                new DungeonParticipantRosterEntry(
                    62001,
                    202,
                    runB,
                    runB.CaptureIdentity(),
                    room,
                    1,
                    partySlot: 1),
                new DungeonParticipantRosterEntry(
                    62000,
                    101,
                    runA,
                    runA.CaptureIdentity(),
                    room,
                    1,
                    partySlot: 0),
            };
            var definition = BuildRewardDefinition(
                "1 7001 1 1 7002 2");
            var rolls = new Queue<int>(new[] { 0, 0, 1, 0 });
            var drawCalls = 0;
            var rewards = new AntonAwakeningDailyCardService(
                null,
                groupItemId => groupItemId == 7001
                    ? BuildUpgradableLegacy((10157834, 1, 2))
                    : BuildUpgradableLegacy((10157833, 1, 4)),
                maximum =>
                {
                    drawCalls++;
                    var value = rolls.Dequeue();
                    return value < maximum ? value : maximum - 1;
                });
            var runtime = new AntonAwakeningRewardRuntime();
            var eventId = Guid.NewGuid();

            var first = TryCreateAndPublishPlan(
                runtime,
                eventId,
                roster,
                rewards,
                definition,
                247,
                out var firstPlan);
            var second = TryCreateAndPublishPlan(
                runtime,
                eventId,
                new[] { roster[0] },
                rewards,
                definition,
                247,
                out var secondPlan);
            Check(
                "same clear event reuses one immutable participant plan",
                first
                && second
                && ReferenceEquals(firstPlan, secondPlan)
                && firstPlan.Entries.Count == 2
                && drawCalls == 4,
                ref failures);
            Check(
                "reward plan is ordered by frozen party slot",
                firstPlan?.Entries[0].Participant.ParticipantUserId == 101
                && firstPlan?.Entries[1].Participant.ParticipantUserId == 202,
                ref failures);
            Check(
                "reward plan preserves item and PVF state",
                firstPlan?.Entries[0].Reward.ItemId == 10157834
                && firstPlan.Entries[0].Reward.State == 1
                && firstPlan.Entries[0].Reward.Quantity == 2
                && firstPlan.Entries[1].Reward.ItemId == 10157833
                && firstPlan.Entries[1].Reward.State == 2
                && firstPlan.Entries[1].Reward.Quantity == 4,
                ref failures);
            Check(
                "reward plan lookup returns the frozen event plan",
                runtime.TryGetPlan(eventId, out var lookedUpPlan)
                && ReferenceEquals(firstPlan, lookedUpPlan),
                ref failures);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            var replacedDeadline = deadline.AddSeconds(30);
            var recordedDeadline = runtime.TryRecordProjectionDeadline(
                eventId,
                roster[1].RunIdentity.ParticipantIdentity,
                deadline);
            var replayedDeadline = runtime.TryRecordProjectionDeadline(
                eventId,
                roster[1].RunIdentity.ParticipantIdentity,
                deadline);
            var replacementRejected = !runtime.TryRecordProjectionDeadline(
                eventId,
                roster[1].RunIdentity.ParticipantIdentity,
                replacedDeadline);
            Check(
                "projection deadline is absolute and immutable per participant",
                recordedDeadline
                && replayedDeadline
                && replacementRejected
                && runtime.TryGetProjectionDeadline(
                    eventId,
                    roster[1].RunIdentity.ParticipantIdentity,
                    out var storedDeadline)
                && storedDeadline == deadline,
                ref failures);

            var source = new DungeonEventEnvelope(
                eventId,
                runA.CaptureIdentity(),
                room.RoomInstanceId,
                62000,
                62000,
                null,
                null,
                "anton-selftest",
                1);
            var journal = instance.ParticipantEffects;
            journal.TryFreeze(
                source,
                DungeonParticipantEffectAudience.Instance,
                roster,
                out _);
            var began = journal.TryBegin(
                eventId,
                DungeonParticipantEffectAudience.Instance,
                roster[1],
                DungeonParticipantEffectKinds.AntonAwakeningAutoReward,
                out var failedReservation,
                out _);
            var failed = journal.TryFail(failedReservation);
            var retried = journal.TryBegin(
                eventId,
                DungeonParticipantEffectAudience.Instance,
                roster[1],
                DungeonParticipantEffectKinds.AntonAwakeningAutoReward,
                out var committedReservation,
                out _);
            var committed = journal.TryCommit(committedReservation);
            var duplicate = journal.TryBegin(
                eventId,
                DungeonParticipantEffectAudience.Instance,
                roster[1],
                DungeonParticipantEffectKinds.AntonAwakeningAutoReward,
                out _,
                out var duplicateState);
            Check(
                "failed reward effect is retryable and committed effect is idempotent",
                began
                && failed
                && retried
                && committed
                && !duplicate
                && duplicateState == DungeonParticipantEffectState.Committed,
                ref failures);

            using (var capture = new ConnectedSession())
            {
                capture.Session.Player.CharacterId = roster[1].CharacterId;
                capture.Session.Player.UserId = roster[1].ParticipantUserId;
                capture.Session.Player.CurrentRun = runA;
                Check(
                    "matching frozen run generation remains projection eligible",
                    Network.Handlers.Dungeon.AntonAwakeningRewardCoordinator
                        .IsCurrentParticipantSession(
                            capture.Session,
                            roster[1]),
                    ref failures);

                var projected = new[]
                {
                    new Network.Builders.AntonAwakeningRewardEntry(
                        101, 0, 0, 10157831, 1),
                    new Network.Builders.AntonAwakeningRewardEntry(
                        202, 0, 2, 10157833, 1),
                };
                var sent = new Network.Handlers.Dungeon
                    .AntonNormalConquestNotificationSender()
                    .SendAntonAwakeningRewardAsync(
                        capture.Session,
                        projected,
                        runA.CaptureIdentity())
                    .GetAwaiter()
                    .GetResult();
                var packets = capture.ReadPackets(2);
                var expectedBody = Network.Builders
                    .AntonAwakeningRewardPacketBuilder.Build(projected);
                Check(
                    "sender projects one full two-member 0x0319 body then 0x00FF",
                    sent
                    && packets.Count == 2
                    && packets[0].Length == 15 + expectedBody.Length
                    && BitConverter.ToUInt16(packets[0], 1)
                        == (ushort)NotiPacketTypeA21
                            .ANTON_AWAKENING_MODE_REWARD
                    && packets[0].Skip(15).SequenceEqual(expectedBody)
                    && BitConverter.ToUInt16(packets[1], 1)
                        == (ushort)NotiPacketTypeA21.EXERCISE_MODE_CLEAR
                    && packets[1].Length == 15 + sizeof(uint),
                    ref failures);

                capture.Session.Player.CurrentRun = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    runA.RunGeneration + 1,
                    DungeonRunState.Active);
                Check(
                    "stale run generation cannot receive old projection",
                    !Network.Handlers.Dungeon.AntonAwakeningRewardCoordinator
                        .IsCurrentParticipantSession(
                            capture.Session,
                            roster[1]),
                    ref failures);
            }
        }

        private static void VerifyEndingRunDoesNotRearmGrantTimer(
            ref int failures)
        {
            using (var fixture = new BarrierPartyFixture(
                leftCharacterId: 62011,
                rightCharacterId: 62012))
            {
                fixture.CommitCard(
                    fixture.LeftParticipant,
                    fixture.Left.Session,
                    CardRewardSide.Free);
                fixture.CommitCard(
                    fixture.RightParticipant,
                    fixture.Right.Session,
                    CardRewardSide.Free);
                fixture.Coordinator.MarkNormalPhaseDeadlineElapsed(
                    fixture.Left.Session,
                    fixture.LeftRun);
                fixture.Coordinator.TryProjectReadyPartyAsync(
                        fixture.Left.Session,
                        fixture.LeftRun)
                    .GetAwaiter()
                    .GetResult();
                fixture.Left.ReadPackets(2);
                fixture.Right.ReadPackets(2);
                var hadGrantTimer = fixture.LeftRun.Timers
                    .TryGetSnapshot(
                        DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                        out var armedSnapshot)
                    && armedSnapshot.HasDeadline;

                fixture.LeftRun.Timers.CancelAll();
                var ending = fixture.LeftRun.TryBeginEnding();
                fixture.Coordinator.TryProjectReadyPartyAsync(
                        fixture.Left.Session,
                        fixture.LeftRun)
                    .GetAwaiter()
                    .GetResult();

                Check(
                    "ending run cannot rearm a cancelled Anton grant timer",
                    hadGrantTimer
                    && ending
                    && fixture.LeftRun.Timers.TryGetSnapshot(
                        DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                        out var cancelledSnapshot)
                    && !cancelledSnapshot.HasDeadline,
                    ref failures);
            }
        }

        private static void VerifyTransactionalGrant(ref int failures)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_auto_reward_{Guid.NewGuid():N}.db");
            var sessionId = Guid.NewGuid();
            const int accountId = 62100;
            const int characterId = 62101;
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                Seed(database, accountId, characterId);
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                }
                var lease = InventoryContext.Register(
                    sessionId,
                    characterId,
                    inventory);
                var daily = new AntonAwakeningDailyCardService(
                    new DailyResetService(database),
                    _ => null,
                    _ => 0);
                var mailbox = new MailboxService(
                    new MailboxRepository(database));
                var grants = new AntonAwakeningRewardGrantService(
                    daily,
                    new MailboxInventoryOverflowRewardSink(mailbox));

                var failed = grants.TryGrant(
                    lease,
                    new AntonAwakeningRewardDefinition(
                        99,
                        247,
                        7001,
                        int.MaxValue,
                        2,
                        2));
                Check(
                    "failed inventory insertion rolls back daily claim",
                    failed.Outcome == AntonAwakeningRewardGrantOutcome.Failed
                    && !daily.HasClaimedRewardToday(characterId, 99, 247),
                    ref failures);

                var granted = grants.TryGrant(
                    lease,
                    new AntonAwakeningRewardDefinition(
                        99,
                        247,
                        7001,
                        3309,
                        3,
                        0));
                var countAfterGrant = CountMainItem(lease, 3309);
                var duplicate = grants.TryGrant(
                    lease,
                    new AntonAwakeningRewardDefinition(
                        99,
                        247,
                        7001,
                        3309,
                        3,
                        0));
                var countAfterDuplicate = CountMainItem(lease, 3309);
                var differentScope = grants.TryGrant(
                    lease,
                    new AntonAwakeningRewardDefinition(
                        99,
                        248,
                        7002,
                        3309,
                        2,
                        1));
                Check(
                    "reward and daily claim commit in one transaction",
                    granted.Outcome == AntonAwakeningRewardGrantOutcome.Granted
                    && daily.HasClaimedRewardToday(characterId, 99, 247)
                    && countAfterGrant == 3
                    && CountMainItem(lease, 7001) == 0,
                    ref failures);
                Check(
                    "duplicate clear becomes committed no-reward",
                    duplicate.Outcome
                        == AntonAwakeningRewardGrantOutcome.AlreadyClaimed
                    && countAfterDuplicate == countAfterGrant,
                    ref failures);
                Check(
                    "a different group/dungeon claim key remains independent",
                    differentScope.Outcome
                        == AntonAwakeningRewardGrantOutcome.Granted
                    && daily.HasClaimedRewardToday(characterId, 99, 248)
                    && CountMainItem(lease, 3309) == countAfterGrant + 2,
                    ref failures);

                lock (lease.SyncRoot)
                    FillMainInventory(lease.Inventory);
                var overflowReward = new AntonAwakeningRewardDefinition(
                    100,
                    247,
                    7003,
                    100320752,
                    1,
                    2);
                var overflowGranted = grants.TryGrant(
                    lease,
                    overflowReward);
                var overflowInbox = mailbox.LoadInbox(characterId, 20);
                var overflowDuplicate = grants.TryGrant(
                    lease,
                    overflowReward);
                var inboxAfterDuplicate = mailbox.LoadInbox(characterId, 20);
                Check(
                    "full inventory Anton reward commits daily claim and mail",
                    overflowGranted.Outcome
                        == AntonAwakeningRewardGrantOutcome.Granted
                    && overflowGranted.DeliveredToMailbox
                    && !overflowGranted.Changes.HasChanges
                    && daily.HasClaimedRewardToday(characterId, 100, 247)
                    && CountMainItem(lease, 100320752) == 0
                    && overflowInbox.Count == 1
                    && overflowInbox[0].Attachments.Count == 1
                    && overflowInbox[0].Attachments[0].ItemTemplateId
                        == 100320752
                    && overflowInbox[0].Attachments[0].ItemCount == 1
                    && overflowInbox[0].Attachments[0].ItemCoreData.Length
                        == ItemCore.Size,
                    ref failures);
                Check(
                    "duplicate Anton reward does not send duplicate mail",
                    overflowDuplicate.Outcome
                        == AntonAwakeningRewardGrantOutcome.AlreadyClaimed
                    && inboxAfterDuplicate.Count == 1,
                    ref failures);

                var rejectingGrants =
                    new AntonAwakeningRewardGrantService(daily);
                var rejectedReward = new AntonAwakeningRewardDefinition(
                    101,
                    247,
                    7004,
                    100320752,
                    1,
                    2);
                var rejected = rejectingGrants.TryGrant(
                    lease,
                    rejectedReward);
                Check(
                    "Anton mail failure rolls back its daily claim",
                    rejected.Outcome
                        == AntonAwakeningRewardGrantOutcome.Failed
                    && !daily.HasClaimedRewardToday(characterId, 101, 247)
                    && mailbox.LoadInbox(characterId, 20).Count == 1,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, characterId);
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static void VerifyNormalCardMailboxOverflow(ref int failures)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo_card_mail_overflow_{Guid.NewGuid():N}.db");
            var sessionId = Guid.NewGuid();
            const int accountId = 62110;
            const int characterId = 62111;
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                Seed(database, accountId, characterId);
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                }
                var lease = InventoryContext.Register(
                    sessionId,
                    characterId,
                    inventory);
                lock (lease.SyncRoot)
                {
                    lease.Inventory.SetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart,
                        1000);
                    FillMainInventory(lease.Inventory);
                }

                var mailbox = new MailboxService(
                    new MailboxRepository(database));
                var overflow = new MailboxInventoryOverflowRewardSink(mailbox);
                var effects = new DungeonPersistentEffectApplicationService(
                    database.ConnectionString,
                    database: database,
                    overflowRewardSink: overflow);
                var freeEffect = new DungeonEffectId(
                    Guid.NewGuid(),
                    DungeonPersistentEffectKinds.CardRewardFreeCommit,
                    DungeonEffectScope.Player,
                    characterId);
                var freeCards = new List<ClearRewardGenerator.CardReward>
                {
                    new ClearRewardGenerator.CardReward
                    {
                        IsGold = true,
                        GoldAmount = 50,
                    },
                    new ClearRewardGenerator.CardReward
                    {
                        ItemId = 3309,
                        StackCount = 3,
                    },
                };
                var freeCommitted = effects.TryApplyCardReward(
                    freeEffect,
                    lease,
                    sessionId,
                    CardRewardSide.Free,
                    paidGoldCost: 0,
                    consumeGoldCardContractUse: false,
                    freeCards,
                    out var freeResult,
                    out _);
                var inboxAfterFree = mailbox.LoadInbox(characterId, 20);
                var freeDuplicate = effects.TryApplyCardReward(
                    freeEffect,
                    lease,
                    sessionId,
                    CardRewardSide.Free,
                    paidGoldCost: 0,
                    consumeGoldCardContractUse: false,
                    freeCards,
                    out var freeDuplicateResult,
                    out _);
                var inboxAfterFreeDuplicate = mailbox.LoadInbox(
                    characterId,
                    20);

                var paidEffect = new DungeonEffectId(
                    Guid.NewGuid(),
                    DungeonPersistentEffectKinds.CardRewardPaidCommit,
                    DungeonEffectScope.Player,
                    characterId);
                var paidCards = Enumerable.Repeat(
                        default(ClearRewardGenerator.CardReward),
                        6)
                    .ToList();
                paidCards[5] = new ClearRewardGenerator.CardReward
                {
                    ItemId = 3309,
                    StackCount = 2,
                };
                var paidCommitted = effects.TryApplyCardReward(
                    paidEffect,
                    lease,
                    sessionId,
                    CardRewardSide.Paid,
                    paidGoldCost: 100,
                    consumeGoldCardContractUse: false,
                    paidCards,
                    out var paidResult,
                    out _);
                var inboxAfterPaid = mailbox.LoadInbox(characterId, 20);

                var rejectingEffects =
                    new DungeonPersistentEffectApplicationService(
                        database.ConnectionString,
                        database: database);
                var rejectedEffect = new DungeonEffectId(
                    Guid.NewGuid(),
                    DungeonPersistentEffectKinds.CardRewardPaidCommit,
                    DungeonEffectScope.Player,
                    characterId);
                var goldBeforeRejected = CountMainItem(
                    lease,
                    InventoryService.MainVirtualCurrencySlotStart);
                var rejected = rejectingEffects.TryApplyCardReward(
                    rejectedEffect,
                    lease,
                    sessionId,
                    CardRewardSide.Paid,
                    paidGoldCost: 40,
                    consumeGoldCardContractUse: false,
                    paidCards,
                    out _,
                    out _);

                Check(
                    "full inventory free card commits gold and item mail",
                    freeCommitted
                    && freeDuplicate
                    && freeResult != null
                    && freeResult.DeliveredToMailbox
                    && freeDuplicateResult?.DeliveredToMailbox == true
                    && freeResult.Changes.Any(change =>
                        change.ListType == InventoryListType.Main
                        && change.SlotIndex
                            == InventoryService.MainVirtualCurrencySlotStart)
                    && CountMainItem(
                            lease,
                            InventoryService.MainVirtualCurrencySlotStart)
                        == 950
                    && inboxAfterFree.Count == 1
                    && inboxAfterFree[0].Attachments.Count == 1
                    && inboxAfterFree[0].Attachments[0].ItemTemplateId == 3309
                    && inboxAfterFree[0].Attachments[0].ItemCount == 3
                    && inboxAfterFree[0].Attachments[0].ItemCoreData.Length
                        == ItemCore.Size
                    && inboxAfterFreeDuplicate.Count == 1,
                    ref failures);
                Check(
                    "full inventory paid card commits cost and item mail",
                    paidCommitted
                    && paidResult != null
                    && paidResult.DeliveredToMailbox
                    && inboxAfterPaid.Count == 2
                    && inboxAfterPaid.Sum(mail => mail.Attachments.Count) == 2
                    && inboxAfterPaid.Sum(mail =>
                            mail.Attachments.Sum(value => value.ItemCount))
                        == 5,
                    ref failures);
                Check(
                    "mail rejection rolls back paid card cost and effect",
                    !rejected
                    && CountMainItem(
                            lease,
                            InventoryService.MainVirtualCurrencySlotStart)
                        == goldBeforeRejected
                    && mailbox.LoadInbox(characterId, 20).Count == 2,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, characterId);
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static void VerifyFourParticipantIndependentPlanning(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var roster = BuildRoster(instance, 4, characterIdBase: 64000);
            var definition = BuildRewardDefinition(
                "1 7001 0 1 7002 1");
            var rolls = new Queue<int>(new[]
            {
                0, 0,
                1, 0,
                0, 1,
                1, 1,
            });
            var rollCalls = 0;
            var loadCalls = 0;
            var rewards = new AntonAwakeningDailyCardService(
                null,
                groupItemId =>
                {
                    Interlocked.Increment(ref loadCalls);
                    return groupItemId == 7001
                        ? BuildUpgradableLegacy(
                            (90001, 1, 1),
                            (90002, 1, 2))
                        : BuildUpgradableLegacy(
                            (90003, 1, 3),
                            (90004, 1, 4));
                },
                maximum =>
                {
                    Interlocked.Increment(ref rollCalls);
                    lock (rolls)
                        return rolls.Dequeue();
                });
            var runtime = new AntonAwakeningRewardRuntime();
            var sourceEventId = Guid.NewGuid();
            AntonAwakeningRewardPlan firstPlan = null;
            AntonAwakeningRewardPlan secondPlan = null;

            var first = System.Threading.Tasks.Task.Run(() =>
                TryCreateAndPublishPlan(
                    runtime,
                    sourceEventId,
                    roster,
                    rewards,
                    definition,
                    247,
                    out firstPlan));
            var second = System.Threading.Tasks.Task.Run(() =>
                TryCreateAndPublishPlan(
                    runtime,
                    sourceEventId,
                    roster.Reverse().ToList().AsReadOnly(),
                    rewards,
                    definition,
                    247,
                    out secondPlan));
            System.Threading.Tasks.Task.WaitAll(first, second);

            Check(
                "concurrent event planning publishes one immutable four-member plan",
                first.Result
                && second.Result
                && ReferenceEquals(firstPlan, secondPlan)
                && firstPlan.Entries.Count == 4
                && loadCalls == 2
                && rollCalls == 8
                && rolls.Count == 0,
                ref failures);
            Check(
                "four participants receive independent two-stage final rewards",
                firstPlan != null
                && firstPlan.Entries.Select(value => value.Reward.ItemId)
                    .SequenceEqual(new[] { 90001, 90003, 90002, 90004 })
                && firstPlan.Entries.Select(value => value.Reward.Quantity)
                    .SequenceEqual(new[] { 1, 3, 2, 4 }),
                ref failures);

            var projected = AntonAwakeningRewardCoordinator
                .BuildProjectedEntries(firstPlan);
            Check(
                "projection uses each frozen final item and quantity",
                projected.Select(value => value.ItemId)
                    .SequenceEqual(new uint[] { 90001, 90003, 90002, 90004 })
                && projected.Select(value => value.Quantity)
                    .SequenceEqual(new uint[] { 1, 3, 2, 4 })
                && projected.All(value => value.ItemId != 7001
                    && value.ItemId != 7002),
                ref failures);
        }

        private static void VerifyParticipantFailureIsolation(ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var roster = BuildRoster(instance, 3, characterIdBase: 65000);
            var definition = BuildRewardDefinition("1 7001 0");
            var rolls = new Queue<int>(new[]
            {
                0, 0,
                0, 1,
                0, 0,
            });
            var rollCalls = 0;
            var rewards = new AntonAwakeningDailyCardService(
                null,
                _ => BuildUpgradableLegacy((90001, 1, 2)),
                maximum =>
                {
                    rollCalls++;
                    var value = rolls.Dequeue();
                    return value < maximum ? value : maximum;
                });
            var runtime = new AntonAwakeningRewardRuntime();
            var sourceEventId = Guid.NewGuid();
            var created = TryCreateAndPublishPlan(
                runtime,
                sourceEventId,
                roster,
                rewards,
                definition,
                247,
                out var plan);
            var replayed = TryCreateAndPublishPlan(
                runtime,
                sourceEventId,
                roster,
                rewards,
                definition,
                247,
                out var replayedPlan);
            Check(
                "one participant draw failure does not discard successful peers",
                created
                && replayed
                && ReferenceEquals(plan, replayedPlan)
                && plan.Entries.Count == 2
                && plan.Entries.Select(value => value.Participant.CharacterId)
                    .SequenceEqual(new[] { 65000, 65002 })
                && rollCalls == 6,
                ref failures);
            Check(
                "failed participant terminal result is frozen for the event",
                runtime.TryGetParticipantResolution(
                    sourceEventId,
                    roster[1].RunIdentity.ParticipantIdentity,
                    out var failedResolution)
                && !failedResolution.Succeeded
                && failedResolution.Failure.RewardGroupItemId == 7001
                && failedResolution.Failure.CardState == 0,
                ref failures);

            var allFailedRollCalls = 0;
            var allFailedRewards = new AntonAwakeningDailyCardService(
                null,
                _ => BuildUpgradableLegacy((90001, 1, 2)),
                maximum =>
                {
                    allFailedRollCalls++;
                    return maximum;
                });
            var allFailedRuntime = new AntonAwakeningRewardRuntime();
            var allFailedEventId = Guid.NewGuid();
            var allFailed = TryCreateAndPublishPlan(
                allFailedRuntime,
                allFailedEventId,
                roster,
                allFailedRewards,
                definition,
                247,
                out _);
            var allFailedReplay = TryCreateAndPublishPlan(
                allFailedRuntime,
                allFailedEventId,
                roster,
                allFailedRewards,
                definition,
                247,
                out _);
            Check(
                "all-failed planning attempt is terminal and never rerolls",
                !allFailed
                && !allFailedReplay
                && allFailedRollCalls == roster.Count
                && roster.All(participant =>
                    allFailedRuntime.TryGetParticipantResolution(
                        allFailedEventId,
                        participant.RunIdentity.ParticipantIdentity,
                        out var resolution)
                    && !resolution.Succeeded),
                ref failures);
        }

        private static IReadOnlyList<DungeonParticipantRosterEntry> BuildRoster(
            DungeonInstance instance,
            int count,
            int characterIdBase)
        {
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var roster = new List<DungeonParticipantRosterEntry>();
            for (var index = 0; index < count; index++)
            {
                var run = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    1,
                    DungeonRunState.Active);
                roster.Add(new DungeonParticipantRosterEntry(
                    characterIdBase + index,
                    (ushort)(500 + index),
                    run,
                    run.CaptureIdentity(),
                    room,
                    1,
                    partySlot: (byte)index));
            }
            return roster.AsReadOnly();
        }

        private static bool TryCreateAndPublishPlan(
            AntonAwakeningRewardRuntime runtime,
            Guid sourceEventId,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            AntonAwakeningDailyCardService rewards,
            DfoServer.GameWorld.SequentialDungeonDefinition definition,
            int rewardableDungeonId,
            out AntonAwakeningRewardPlan plan)
        {
            plan = null;
            if (runtime == null
                || !runtime.TryGetOrRegisterPlanCreation(
                    sourceEventId,
                    roster,
                    rewards,
                    definition,
                    rewardableDungeonId,
                    out var creation)
                || !runtime.TryEvaluatePlanCreation(
                    creation,
                    out var outcome)
                || !runtime.TryPublishPlanCreation(creation, outcome))
            {
                return false;
            }

            plan = outcome.Plan;
            return plan != null && plan.Entries.Count > 0;
        }

        private static AntonAwakeningDailyCardService CreateRewardService(
            DailyResetService dailyReset,
            int finalItemId,
            int quantity)
            => new AntonAwakeningDailyCardService(
                dailyReset,
                _ => BuildUpgradableLegacy((finalItemId, 1, quantity)),
                _ => 0);

        private static PvfLib.StackableItemFile BuildUpgradableLegacy(
            params (int ItemId, int Weight, int Count)[] entries)
        {
            var stackable = new PvfLib.StackableItemFile
            {
                StackableType = "[upgradable legacy]",
            };
            foreach (var entry in entries)
            {
                stackable.UpgradableLegacyRewards.Add(
                    new PvfLib.BoosterRewardEntry
                    {
                        RewardKind = "upgradable legacy",
                        ItemId = entry.ItemId,
                        Weight = entry.Weight,
                        Count = entry.Count,
                    });
            }
            return stackable;
        }

        private static DfoServer.GameWorld.SequentialDungeonDefinition
            BuildRewardDefinition(string rewards)
        {
            var catalog = DfoServer.GameWorld
                .SequentialDungeonDefinitionCatalog.Parse(
                    "[sequential dungeon]\n99\n"
                    + "[dungeon index check]\n247\n[/dungeon index check]\n"
                    + "[rewardable dungeon index]\n247\n"
                    + "[/rewardable dungeon index]\n"
                    + "[clear reward item]\n"
                    + rewards
                    + "\n[/clear reward item]\n[/sequential dungeon]",
                    _ => (byte)2);
            if (!catalog.TryGetByGroupKey(99, out var definition))
            {
                throw new InvalidOperationException(
                    "sequential reward fixture failed to parse");
            }
            return definition;
        }

        private static int CountMainItem(InventoryLease lease, int itemId)
        {
            lock (lease.SyncRoot)
                return lease.Inventory.CountMainItem(itemId);
        }

        private static void FillMainInventory(InventoryService inventory)
        {
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));

            for (short slot = InventoryService.MainSlotStart;
                 slot <= InventoryService.MainSlotEnd;
                 slot++)
            {
                if (!inventory.SetItem(
                        InventoryListType.Main,
                        slot,
                        new ItemCore
                        {
                            ItemKind = ItemCore.KindEquipment,
                            ItemId = 200000000 + slot,
                            InstanceValue = 1,
                            Marker16 = -1,
                            RandomOptionChangedIndex = 0xFF,
                        }))
                {
                    throw new InvalidOperationException(
                        $"failed to fill main inventory slot {slot}");
                }
            }
        }

        private static void Seed(
            IGameDatabase database,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@mid", "anton-auto-a");
                command.Parameters.AddWithValue("@name", "anton-auto-c");
                command.ExecuteNonQuery();
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // SQLite may still be releasing a test handle.
            }
        }

        private sealed class BarrierPartyFixture : IDisposable
        {
            internal BarrierPartyFixture(
                int leftCharacterId,
                int rightCharacterId,
                TimeSpan? sendLockTimeout = null,
                bool prepareImmediately = true)
            {
                Instance = new DungeonInstance(247, 0);
                LeftRun = new DungeonRun(
                    Instance,
                    DungeonIdentityGenerator.NextRunId(),
                    1,
                    DungeonRunState.Active);
                RightRun = new DungeonRun(
                    Instance,
                    DungeonIdentityGenerator.NextRunId(),
                    1,
                    DungeonRunState.Active);
                var source = DungeonEventEnvelope.Create(
                    LeftRun,
                    leftCharacterId,
                    "anton-barrier-fixture",
                    sourceEventId: Guid.NewGuid());
                ClearFact = Instance.GetOrCreateClearedFact(
                    new DungeonClearIntent(source, "selftest", 0),
                    out _);
                LeftRun.TryBeginClearCommit(ClearFact);
                LeftRun.TryCompleteClearCommit(ClearFact);
                RightRun.TryBeginClearCommit(ClearFact);
                RightRun.TryCompleteClearCommit(ClearFact);
                var room = new DungeonRoomIdentity(Instance.Identity, 1);
                LeftParticipant = new DungeonParticipantRosterEntry(
                    leftCharacterId,
                    441,
                    LeftRun,
                    LeftRun.CaptureIdentity(),
                    room,
                    1,
                    partySlot: 0);
                RightParticipant = new DungeonParticipantRosterEntry(
                    rightCharacterId,
                    442,
                    RightRun,
                    RightRun.CaptureIdentity(),
                    room,
                    1,
                    partySlot: 1);
                var roster = new[]
                {
                    LeftParticipant,
                    RightParticipant,
                };
                var journal = Instance.ParticipantEffects;
                journal.TryFreeze(
                    ClearFact.Source,
                    DungeonParticipantEffectAudience.Instance,
                    roster,
                    out _);
                foreach (var participant in roster)
                {
                    journal.TryBegin(
                        ClearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        participant,
                        DungeonParticipantEffectKinds.DungeonClear,
                        out var clearReservation,
                        out _);
                    journal.TryCommit(clearReservation);
                }

                Sessions = new SessionDirectory();
                Left = new ConnectedSession();
                Right = new ConnectedSession();
                Left.Session.Player.CharacterId = leftCharacterId;
                Left.Session.Player.UserId =
                    LeftParticipant.ParticipantUserId;
                Left.Session.Player.CurrentRun = LeftRun;
                Right.Session.Player.CharacterId = rightCharacterId;
                Right.Session.Player.UserId =
                    RightParticipant.ParticipantUserId;
                Right.Session.Player.CurrentRun = RightRun;
                Sessions.Register(leftCharacterId, Left.Session);
                Sessions.Register(rightCharacterId, Right.Session);

                var rewards = CreateRewardService(
                    dailyReset: null,
                    finalItemId: 3309,
                    quantity: 2);
                Coordinator = new AntonAwakeningRewardCoordinator(
                    rewards,
                    new AntonAwakeningRewardGrantService(rewards),
                    Sessions,
                    null,
                    new AntonNormalConquestNotificationSender(
                        new PartyPacketSender(
                            Sessions,
                            sendLockTimeout)));
                if (prepareImmediately)
                    Prepare();
            }

            internal DungeonInstance Instance { get; }
            internal DungeonRun LeftRun { get; }
            internal DungeonRun RightRun { get; }
            internal DungeonClearedFact ClearFact { get; }
            internal DungeonParticipantRosterEntry LeftParticipant { get; }
            internal DungeonParticipantRosterEntry RightParticipant { get; }
            internal SessionDirectory Sessions { get; }
            internal ConnectedSession Left { get; }
            internal ConnectedSession Right { get; }
            internal AntonAwakeningRewardCoordinator Coordinator { get; }

            internal void Prepare()
            {
                Coordinator.PrepareClearAsync(LeftRun, ClearFact)
                    .GetAwaiter()
                    .GetResult();
            }

            internal void CommitCard(
                DungeonParticipantRosterEntry participant,
                EnhancedClientSession session,
                CardRewardSide side)
            {
                participant.Run.Effects.TryReserve(
                    CardRewardRules.GetEffectId(participant.Run, side),
                    out var reservation);
                participant.Run.Effects.TryCommit(reservation);
                Coordinator.OnCardCommittedAsync(
                        session,
                        participant.Run,
                        side)
                    .GetAwaiter()
                    .GetResult();
            }

            public void Dispose()
            {
                LeftRun.Timers.CancelAll();
                RightRun.Timers.CancelAll();
                Sessions.UnregisterAsync(
                        LeftParticipant.CharacterId,
                        Left.Session)
                    .GetAwaiter()
                    .GetResult();
                Sessions.UnregisterAsync(
                        RightParticipant.CharacterId,
                        Right.Session)
                    .GetAwaiter()
                    .GetResult();
                Left.Dispose();
                Right.Dispose();
            }
        }

        private sealed class GateObservingCardSender
            : ICardRewardNotificationSender
        {
            private readonly Action _observe;

            internal GateObservingCardSender(Action observe)
            {
                _observe = observe ?? throw new ArgumentNullException(
                    nameof(observe));
            }

            internal int CardInfoCalls { get; private set; }
            internal int ItemUpdateCalls { get; private set; }

            public System.Threading.Tasks.Task SendLayoutAsync(
                EnhancedClientSession session,
                CardRewardPartyProjection projection)
            {
                _observe();
                return System.Threading.Tasks.Task.CompletedTask;
            }

            public System.Threading.Tasks.Task SendCardInfoAsync(
                EnhancedClientSession session,
                CardRewardPartyProjection projection)
            {
                _observe();
                CardInfoCalls++;
                return System.Threading.Tasks.Task.CompletedTask;
            }

            public System.Threading.Tasks.Task SendExitAsync(
                EnhancedClientSession session,
                byte state,
                byte option)
            {
                _observe();
                return System.Threading.Tasks.Task.CompletedTask;
            }

            public System.Threading.Tasks.Task SendItemUpdatesAsync(
                EnhancedClientSession session,
                IReadOnlyList<InventorySlotMutation> changes)
            {
                _observe();
                ItemUpdateCalls++;
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        private sealed class BlockingLayoutCardSender
            : ICardRewardNotificationSender, IDisposable
        {
            internal ManualResetEventSlim Entered { get; } = new ManualResetEventSlim();
            internal ManualResetEventSlim Release { get; } = new ManualResetEventSlim();

            public System.Threading.Tasks.Task SendLayoutAsync(
                EnhancedClientSession session,
                CardRewardPartyProjection projection)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("layout release was not signaled");
                return System.Threading.Tasks.Task.CompletedTask;
            }

            public System.Threading.Tasks.Task SendCardInfoAsync(
                EnhancedClientSession session,
                CardRewardPartyProjection projection)
                => System.Threading.Tasks.Task.CompletedTask;

            public System.Threading.Tasks.Task SendExitAsync(
                EnhancedClientSession session,
                byte state,
                byte option)
                => System.Threading.Tasks.Task.CompletedTask;

            public System.Threading.Tasks.Task SendItemUpdatesAsync(
                EnhancedClientSession session,
                IReadOnlyList<InventorySlotMutation> changes)
                => System.Threading.Tasks.Task.CompletedTask;

            public void Dispose()
            {
                Release.Set();
                Entered.Dispose();
                Release.Dispose();
            }
        }

        private sealed class ConnectedSession : IDisposable
        {
            private readonly TcpClient _reader;

            internal ConnectedSession()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    _reader = new TcpClient();
                    var connect = _reader.ConnectAsync(IPAddress.Loopback, port);
                    var writer = listener.AcceptTcpClient();
                    connect.GetAwaiter().GetResult();
                    Session = new EnhancedClientSession(
                        writer,
                        new GamePacketHeader());
                }
                finally
                {
                    listener.Stop();
                }
            }

            internal EnhancedClientSession Session { get; }
            internal int AvailableByteCount => _reader.Available;

            internal void ConstrainSocketBuffers(int size)
            {
                Session.TcpClient.SendBufferSize = size;
                _reader.ReceiveBufferSize = size;
            }

            internal bool FillWriterUntilWouldBlock()
            {
                var socket = Session.TcpClient.Client;
                var originalBlocking = socket.Blocking;
                var chunk = new byte[64 * 1024];
                try
                {
                    socket.Blocking = false;
                    for (var round = 0; round < 4; round++)
                    {
                        var blocked = false;
                        for (var sent = 0;
                             sent < 8 * 1024 * 1024;
                             sent += chunk.Length)
                        {
                            try
                            {
                                if (socket.Send(chunk) <= 0)
                                {
                                    blocked = true;
                                    break;
                                }
                            }
                            catch (SocketException ex)
                                when (ex.SocketErrorCode
                                          == SocketError.WouldBlock
                                      || ex.SocketErrorCode
                                          == SocketError.IOPending
                                      || ex.SocketErrorCode
                                          == SocketError.NoBufferSpaceAvailable)
                            {
                                blocked = true;
                                break;
                            }
                        }

                        if (!blocked)
                            return false;
                        if (round == 3)
                            return true;

                        socket.Blocking = true;
                        System.Threading.Thread.Sleep(25);
                        socket.Blocking = false;
                    }
                    return false;
                }
                finally
                {
                    socket.Blocking = originalBlocking;
                }
            }

            internal byte[] ReadBytes(int count)
                => ReadExact(_reader.GetStream(), count);

            internal List<byte[]> ReadPackets(int minimumCount)
            {
                var packets = new List<byte[]>();
                var stream = _reader.GetStream();
                var deadline = DateTime.UtcNow.AddSeconds(1);
                while (packets.Count < minimumCount
                       && DateTime.UtcNow < deadline)
                {
                    var wait = deadline - DateTime.UtcNow;
                    if (!_reader.Client.Poll(
                            (int)Math.Max(1, wait.TotalMilliseconds * 1000),
                            SelectMode.SelectRead))
                    {
                        continue;
                    }

                    var header = ReadExact(stream, 15);
                    var length = BitConverter.ToInt32(header, 3);
                    if (length < 15)
                        throw new InvalidOperationException("Invalid packet length.");
                    var packet = new byte[length];
                    Buffer.BlockCopy(header, 0, packet, 0, header.Length);
                    if (length > header.Length)
                    {
                        var body = ReadExact(stream, length - header.Length);
                        Buffer.BlockCopy(
                            body,
                            0,
                            packet,
                            header.Length,
                            body.Length);
                    }
                    packets.Add(packet);
                }
                if (packets.Count < minimumCount)
                {
                    throw new TimeoutException(
                        $"Captured {packets.Count}/{minimumCount} packets.");
                }
                return packets;
            }

            public void Dispose()
            {
                try
                {
                    Session?.TcpClient?.Close();
                }
                catch
                {
                }
                _reader?.Close();
            }

            private static byte[] ReadExact(NetworkStream stream, int count)
            {
                var result = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var read = stream.Read(result, offset, count - offset);
                    if (read <= 0)
                        throw new EndOfStreamException();
                    offset += read;
                }
                return result;
            }
        }
    }
}
