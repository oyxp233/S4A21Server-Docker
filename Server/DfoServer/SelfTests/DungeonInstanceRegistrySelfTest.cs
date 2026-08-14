using System;
using System.Net.Sockets;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    internal static class DungeonInstanceRegistrySelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== DUNGEON_INSTANCE_REGISTRY selftest ===");
            var failures = 0;
            var now = new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc);
            using var registry = new DungeonInstanceRegistry(
                ClockService.Instance,
                new DungeonParticipantAttachmentOptions(
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromMinutes(2)),
                () => now);

            var instance = new DungeonInstance(1002, 1);
            using var firstClient = new TcpClient();
            using var secondClient = new TcpClient();
            var first = CreateSession(firstClient, 501, 1501);
            var second = CreateSession(secondClient, 502, 1502);
            var firstRun = CreateRun(first, instance, 1);
            var secondRun = CreateRun(second, instance, 1);
            var firstRegistration = registry.RegisterActive(
                Registration(first, firstRun, partyId: 77));
            registry.RegisterActive(
                Registration(second, secondRun, partyId: 77));

            Check(
                "active participants share the physical instance but keep personal runs",
                firstRegistration.RunIdentity.PartyDungeonInstanceId
                    == secondRun.PartyDungeonInstanceId
                && firstRegistration.RunIdentity.RunId != secondRun.RunId,
                ref failures);

            var timerKey = new RunTimerKey("selftest", "rejoin-stale");
            var timerTicket = firstRun.Timers.Begin(timerKey);
            var timerHandle = ClockService.Instance.ScheduleOneShot(
                "selftest:rejoin-stale:" + Guid.NewGuid().ToString("N"),
                now.AddHours(1),
                _ => { });
            firstRun.Timers.Attach(timerTicket, timerHandle);

            Check(
                "network teardown detaches instead of ending a registered run",
                DungeonRunLifecycle.DetachRunOnNetworkDisconnect(first, registry)
                && first.Player.CurrentRun == null
                && firstRun.RunState == DungeonRunState.Active,
                ref failures);
            Check(
                "network detach invalidates every old run timer ticket",
                !firstRun.Timers.IsCurrent(timerTicket),
                ref failures);

            var candidateStatus = registry.TryGetCandidate(
                first.Account.AccountId,
                first.Player.CharacterId,
                first.Player.UserId,
                out var candidate);
            Check(
                "detached candidate freezes owner identity and lists instance participants",
                candidateStatus == DungeonAttachmentOperationStatus.Success
                && candidate.State == DungeonParticipantAttachmentState.Detached
                && candidate.AttachmentGeneration > firstRegistration.AttachmentGeneration
                && candidate.ParticipantUserIds.Count == 2
                && candidate.ParticipantUserIds[0] == first.Player.UserId
                && candidate.ParticipantUserIds[1] == second.Player.UserId,
                ref failures);

            Check(
                "account mismatch cannot inspect another character attachment",
                registry.TryGetCandidate(
                    accountId: 9999,
                    first.Player.CharacterId,
                    first.Player.UserId,
                    out _) == DungeonAttachmentOperationStatus.IdentityMismatch,
                ref failures);

            using var resumedClient = new TcpClient();
            var resumed = CreateSession(resumedClient, 501, 1501);
            Check(
                "old attachment generation cannot resume a newer detached lease",
                registry.TryResume(
                    resumed.Account.AccountId,
                    resumed.Player.CharacterId,
                    resumed.Player.UserId,
                    candidate.PartyId,
                    second.Player.UserId,
                    firstRegistration.AttachmentGeneration,
                    resumed.SessionId,
                    out _) == DungeonAttachmentOperationStatus.StaleGeneration,
                ref failures);
            Check(
                "target participant must belong to the same physical instance",
                registry.TryResume(
                    resumed.Account.AccountId,
                    resumed.Player.CharacterId,
                    resumed.Player.UserId,
                    candidate.PartyId,
                    targetParticipantUserId: 65000,
                    candidate.AttachmentGeneration,
                    resumed.SessionId,
                    out _) == DungeonAttachmentOperationStatus.TargetParticipantMissing,
                ref failures);

            var resumeStatus = registry.TryResume(
                resumed.Account.AccountId,
                resumed.Player.CharacterId,
                resumed.Player.UserId,
                candidate.PartyId,
                second.Player.UserId,
                candidate.AttachmentGeneration,
                resumed.SessionId,
                out var resumedAttachment);
            Check(
                "valid resume creates one new active attachment generation",
                resumeStatus == DungeonAttachmentOperationStatus.Success
                && resumedAttachment.State == DungeonParticipantAttachmentState.Active
                && resumedAttachment.AttachmentGeneration
                    > candidate.AttachmentGeneration,
                ref failures);
            Check(
                "replayed resume request is an idempotent success",
                registry.TryResume(
                    resumed.Account.AccountId,
                    resumed.Player.CharacterId,
                    resumed.Player.UserId,
                    candidate.PartyId,
                    second.Player.UserId,
                    candidate.AttachmentGeneration,
                    resumed.SessionId,
                    out var replayedResume)
                    == DungeonAttachmentOperationStatus.Success
                && replayedResume.AttachmentGeneration
                    == resumedAttachment.AttachmentGeneration,
                ref failures);
            Check(
                "new player context attaches the preserved logical run generation",
                DungeonRunLifecycle.AttachResumedRun(
                    resumed,
                    resumedAttachment)
                && ReferenceEquals(resumed.Player.CurrentRun, firstRun)
                && resumed.Player.CurrentDungeonRunGeneration
                    == firstRun.RunGeneration,
                ref failures);

            DungeonRunLifecycle.EndRunAsync(
                    resumed,
                    DungeonRunEndReason.ReturnToTown,
                    firstRun.CaptureIdentity(),
                    registry)
                .GetAwaiter()
                .GetResult();
            Check(
                "active return-to-town terminates and releases the attachment",
                resumed.Player.CurrentRun == null
                && firstRun.RunState == DungeonRunState.Ended
                && !registry.TryGetForRun(
                    resumed.Player.CharacterId,
                    firstRun.CaptureIdentity(),
                    out _),
                ref failures);
            Check(
                "ending one participant does not end another participant run",
                secondRun.RunState == DungeonRunState.Active
                && ReferenceEquals(second.Player.CurrentRun, secondRun),
                ref failures);

            using var cancelClient = new TcpClient();
            var cancelSession = CreateSession(cancelClient, 503, 1503);
            var cancelRun = CreateRun(
                cancelSession,
                new DungeonInstance(1003, 0),
                1);
            registry.RegisterActive(
                Registration(cancelSession, cancelRun, partyId: 88));
            registry.TryDetach(
                cancelSession.Account.AccountId,
                cancelSession.Player.CharacterId,
                cancelSession.Player.UserId,
                cancelSession.SessionId,
                cancelRun.CaptureIdentity(),
                out var cancelOffer);
            var cancelStatus = registry.TryCancel(
                cancelSession.Account.AccountId,
                cancelSession.Player.CharacterId,
                cancelSession.Player.UserId,
                cancelOffer.PartyId,
                cancelOffer.AttachmentGeneration,
                out var cancelled);
            Check(
                "cancel ends the detached run exactly once",
                cancelStatus == DungeonAttachmentOperationStatus.Success
                && cancelled.State == DungeonParticipantAttachmentState.Cancelled
                && cancelRun.RunState == DungeonRunState.Ended,
                ref failures);
            Check(
                "replayed cancel request is an idempotent success",
                registry.TryCancel(
                    cancelSession.Account.AccountId,
                    cancelSession.Player.CharacterId,
                    cancelSession.Player.UserId,
                    cancelOffer.PartyId,
                    cancelOffer.AttachmentGeneration,
                    out var replayedCancel)
                    == DungeonAttachmentOperationStatus.Success
                && replayedCancel.AttachmentGeneration
                    == cancelled.AttachmentGeneration,
                ref failures);

            using var idleClient = new TcpClient();
            var idleSession = CreateSession(idleClient, 504, 1504);
            var idleRun = CreateRun(
                idleSession,
                new DungeonInstance(1004, 0),
                1);
            registry.RegisterActive(
                Registration(idleSession, idleRun, partyId: 89));
            registry.TryDetach(
                idleSession.Account.AccountId,
                idleSession.Player.CharacterId,
                idleSession.Player.UserId,
                idleSession.SessionId,
                idleRun.CaptureIdentity(),
                out _);
            now = now.AddMinutes(3);
            Check(
                "idle timeout expires and ends an abandoned detached run",
                registry.ExpireDue(now) == 1
                && idleRun.RunState == DungeonRunState.Ended,
                ref failures);

            using var hardClient = new TcpClient();
            var hardSession = CreateSession(hardClient, 505, 1505);
            var hardRun = CreateRun(
                hardSession,
                new DungeonInstance(1005, 0),
                1);
            registry.RegisterActive(
                Registration(hardSession, hardRun, partyId: 90));
            registry.TryDetach(
                hardSession.Account.AccountId,
                hardSession.Player.CharacterId,
                hardSession.Player.UserId,
                hardSession.SessionId,
                hardRun.CaptureIdentity(),
                out _);
            for (var i = 0; i < 6; i++)
            {
                now = now.AddMinutes(1.5);
                registry.TryGetCandidate(
                    hardSession.Account.AccountId,
                    hardSession.Player.CharacterId,
                    hardSession.Player.UserId,
                    out _);
            }
            Check(
                "candidate activity extends idle lease without crossing hard expiry",
                hardRun.RunState == DungeonRunState.Active,
                ref failures);
            now = now.AddMinutes(2);
            Check(
                "hard timeout ends a repeatedly touched detached run",
                registry.ExpireDue(now) == 1
                && hardRun.RunState == DungeonRunState.Ended,
                ref failures);

            using var soloClient = new TcpClient();
            var soloSession = CreateSession(soloClient, 506, 1506);
            var soloRun = CreateRun(
                soloSession,
                new DungeonInstance(1006, 0),
                1);
            registry.RegisterActive(
                Registration(soloSession, soloRun, partyId: 0));
            registry.TryDetach(
                soloSession.Account.AccountId,
                soloSession.Player.CharacterId,
                soloSession.Player.UserId,
                soloSession.SessionId,
                soloRun.CaptureIdentity(),
                out _);
            Check(
                "missing PartyId fails closed instead of inventing a rejoin key",
                registry.TryGetCandidate(
                    soloSession.Account.AccountId,
                    soloSession.Player.CharacterId,
                    soloSession.Player.UserId,
                    out _) == DungeonAttachmentOperationStatus.PartyUnavailable,
                ref failures);

            registry.Terminate(
                second.Player.CharacterId,
                secondRun.CaptureIdentity(),
                "selftest_cleanup");
            Console.WriteLine(failures == 0
                ? "PASS"
                : "FAILURES=" + failures);
            return failures == 0 ? 0 : 1;
        }

        private static EnhancedClientSession CreateSession(
            TcpClient client,
            int accountId,
            int characterId)
        {
            var session = new EnhancedClientSession(
                client,
                new GamePacketHeader());
            session.Account = new AccountRecord { AccountId = accountId };
            session.Player.CharacterId = characterId;
            session.Player.UserId = checked((ushort)characterId);
            session.Player.Level = 1;
            return session;
        }

        private static DungeonRun CreateRun(
            EnhancedClientSession session,
            DungeonInstance instance,
            long runGeneration)
        {
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                runGeneration,
                DungeonRunState.Active);
            if (!session.Player.TryAttachResumedDungeonRun(run))
                throw new InvalidOperationException("Selftest could not attach run.");
            return run;
        }

        private static DungeonParticipantRegistration Registration(
            EnhancedClientSession session,
            DungeonRun run,
            int partyId)
        {
            return new DungeonParticipantRegistration(
                session.Account.AccountId,
                session.Player.CharacterId,
                session.Player.UserId,
                partyId,
                session.SessionId,
                run);
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine((condition ? "[OK] " : "[FAIL] ") + name);
            if (!condition)
                failures++;
        }
    }
}
