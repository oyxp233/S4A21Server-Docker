using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;

namespace DfoServer.SelfTests
{
    internal static class DungeonRejoinProtocolSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== DUNGEON_REJOIN_PROTOCOL selftest ===");
            var failures = 0;
            var now = new DateTime(2026, 7, 27, 1, 0, 0, DateTimeKind.Utc);
            using var registry = new DungeonInstanceRegistry(
                ClockService.Instance,
                new DungeonParticipantAttachmentOptions(
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromMinutes(2)),
                () => now);
            var sessionDirectory = new SessionDirectory();
            var characterTransitions =
                new CharacterTransitionCoordinator(sessionDirectory);
            var packets = new List<byte[]>();
            var partyRestoreCount = 0;
            var partyRollbackCount = 0;
            var townLeaveCount = 0;
            var coordinator = new DungeonRejoinCoordinator(
                registry,
                characterTransitions,
                (session, partyId) =>
                {
                    partyRestoreCount++;
                    return Task.FromResult(true);
                },
                (session, partyId) =>
                {
                    partyRollbackCount++;
                    return Task.CompletedTask;
                },
                session =>
                {
                    townLeaveCount++;
                    return Task.CompletedTask;
                },
                (session, packet) =>
                {
                    packets.Add(packet);
                    return Task.CompletedTask;
                });

            var infoBody = DungeonRejoinNotificationBuilder
                .BuildDisconnectedDungeonInfo(
                    partyId: 77,
                    reservedInt32: unchecked((int)0x89ABCDEF),
                    rejoinUiState: 6);
            Check(
                "0x02A7 body keeps int32 + reservedInt32 + byte widths",
                infoBody.Length == 9
                && BitConverter.ToInt32(infoBody, 0) == 77
                && BitConverter.ToInt32(infoBody, 4)
                    == unchecked((int)0x89ABCDEF)
                && infoBody[8] == 6,
                ref failures);
            Check(
                "participant and rejoinable notification bodies keep u16/i32 widths",
                DungeonRejoinNotificationBuilder.BuildParticipant(1501).Length == 2
                && DungeonRejoinNotificationBuilder.BuildRejoinableDungeon(77).Length == 4,
                ref failures);
            Check(
                "0x02D7 parser reads int32 PartyId and promoted participant userId",
                DungeonRejoinRequestParser.TryParseRejoin(
                    BuildRejoinRequest(77, 1502),
                    out var parsedRejoin,
                    out _)
                && parsedRejoin.PartyId == 77
                && parsedRejoin.TargetParticipantUserId == 1502,
                ref failures);
            Check(
                "0x02D8 parser rejects missing or non-positive PartyId",
                !DungeonRejoinRequestParser.TryParseCancel(
                    Array.Empty<byte>(),
                    out _,
                    out _)
                && !DungeonRejoinRequestParser.TryParseCancel(
                    BitConverter.GetBytes(0),
                    out _,
                    out _),
                ref failures);
            Check(
                "rejoin parsers reject trailing bytes instead of accepting an ambiguous command",
                !DungeonRejoinRequestParser.TryParseRejoin(
                    new byte[9],
                    out _,
                    out _)
                && !DungeonRejoinRequestParser.TryParseCancel(
                    new byte[5],
                    out _,
                    out _),
                ref failures);

            var instance = new DungeonInstance(1002, 0);
            using var oldClient = new TcpClient();
            using var targetClient = new TcpClient();
            using var newClient = new TcpClient();
            var oldSession = CreateSession(oldClient, 601, 1601);
            var targetSession = CreateSession(targetClient, 602, 1602);
            var newSession = CreateSession(newClient, 601, 1601);
            var oldRun = CreateRun(oldSession, instance);
            var targetRun = CreateRun(targetSession, instance);
            registry.RegisterActive(
                Registration(oldSession, oldRun, partyId: 77));
            registry.RegisterActive(
                Registration(targetSession, targetRun, partyId: 77));
            registry.TryDetach(
                oldSession.Account.AccountId,
                oldSession.Player.CharacterId,
                oldSession.Player.UserId,
                oldSession.SessionId,
                oldRun.CaptureIdentity(),
                out _);
            sessionDirectory.Register(
                newSession.Player.CharacterId,
                newSession);

            coordinator.ProjectCandidateAsync(newSession)
                .GetAwaiter()
                .GetResult();
            Check(
                "candidate projection sends 0x02A7 followed by participant 0x02A8 records",
                packets.Count == 3
                && IsEnvelope(packets[0], 0x00, 0x02A7)
                && BitConverter.ToInt32(packets[0], 15) == 77
                && BitConverter.ToInt32(packets[0], 19) == 0
                && IsEnvelope(packets[1], 0x00, 0x02A8)
                && IsEnvelope(packets[2], 0x00, 0x02A8),
                ref failures);

            packets.Clear();
            coordinator.HandleRejoinAsync(
                    newSession,
                    Header(0x02D7),
                    BuildRejoinRequest(77, targetSession.Player.UserId))
                .GetAwaiter()
                .GetResult();
            Check(
                "accepted rejoin restores party/town once and attaches the preserved run",
                partyRestoreCount == 1
                && partyRollbackCount == 0
                && townLeaveCount == 1
                && ReferenceEquals(newSession.Player.CurrentRun, oldRun),
                ref failures);
            Check(
                "accepted rejoin returns common ACK then 0x02AA PartyId",
                packets.Count == 2
                && IsEnvelope(packets[0], 0x01, 0x02D7)
                && packets[0][15] == 1
                && IsEnvelope(packets[1], 0x00, 0x02AA)
                && BitConverter.ToInt32(packets[1], 15) == 77,
                ref failures);

            packets.Clear();
            coordinator.HandleRejoinAsync(
                    newSession,
                    Header(0x02D7),
                    BuildRejoinRequest(77, targetSession.Player.UserId))
                .GetAwaiter()
                .GetResult();
            Check(
                "replayed accepted request does not repeat application effects",
                packets.Count == 2
                && packets[0][15] == 1
                && partyRestoreCount == 1
                && partyRollbackCount == 0
                && townLeaveCount == 1
                && ReferenceEquals(newSession.Player.CurrentRun, oldRun),
                ref failures);

            using var concurrentOldClient = new TcpClient();
            using var concurrentNewClient = new TcpClient();
            var concurrentOld = CreateSession(
                concurrentOldClient,
                606,
                1606);
            var concurrentNew = CreateSession(
                concurrentNewClient,
                606,
                1606);
            var concurrentRun = CreateRun(
                concurrentOld,
                new DungeonInstance(1007, 0));
            registry.RegisterActive(
                Registration(concurrentOld, concurrentRun, partyId: 81));
            registry.TryDetach(
                concurrentOld.Account.AccountId,
                concurrentOld.Player.CharacterId,
                concurrentOld.Player.UserId,
                concurrentOld.SessionId,
                concurrentRun.CaptureIdentity(),
                out _);
            var concurrentPackets = new List<byte[]>();
            var concurrentRestoreCount = 0;
            var concurrentTownLeaveCount = 0;
            var concurrentCoordinator = new DungeonRejoinCoordinator(
                registry,
                characterTransitions,
                async (session, partyId) =>
                {
                    Interlocked.Increment(ref concurrentRestoreCount);
                    await Task.Delay(25);
                    return true;
                },
                (session, partyId) => Task.CompletedTask,
                session =>
                {
                    Interlocked.Increment(ref concurrentTownLeaveCount);
                    return Task.CompletedTask;
                },
                (session, packet) =>
                {
                    lock (concurrentPackets)
                        concurrentPackets.Add(packet);
                    return Task.CompletedTask;
                });
            sessionDirectory.Register(
                concurrentNew.Player.CharacterId,
                concurrentNew);
            concurrentCoordinator.ProjectCandidateAsync(concurrentNew)
                .GetAwaiter()
                .GetResult();
            concurrentPackets.Clear();
            var concurrentRequest = BuildRejoinRequest(
                81,
                concurrentOld.Player.UserId);
            Task.WhenAll(
                    concurrentCoordinator.HandleRejoinAsync(
                        concurrentNew,
                        Header(0x02D7),
                        concurrentRequest),
                    concurrentCoordinator.HandleRejoinAsync(
                        concurrentNew,
                        Header(0x02D7),
                        concurrentRequest))
                .GetAwaiter()
                .GetResult();
            Check(
                "concurrent accepted requests execute application effects once",
                concurrentRestoreCount == 1
                && concurrentTownLeaveCount == 1
                && concurrentPackets.Count == 4
                && ReferenceEquals(
                    concurrentNew.Player.CurrentRun,
                    concurrentRun),
                ref failures);

            using var restoreFailureOldClient = new TcpClient();
            using var restoreFailureNewClient = new TcpClient();
            var restoreFailureOld = CreateSession(
                restoreFailureOldClient,
                604,
                1604);
            var restoreFailureNew = CreateSession(
                restoreFailureNewClient,
                604,
                1604);
            var restoreFailureRun = CreateRun(
                restoreFailureOld,
                new DungeonInstance(1004, 0));
            registry.RegisterActive(
                Registration(
                    restoreFailureOld,
                    restoreFailureRun,
                    partyId: 79));
            registry.TryDetach(
                restoreFailureOld.Account.AccountId,
                restoreFailureOld.Player.CharacterId,
                restoreFailureOld.Player.UserId,
                restoreFailureOld.SessionId,
                restoreFailureRun.CaptureIdentity(),
                out _);
            var restoreFailurePackets = new List<byte[]>();
            var restoreFailureAttempts = 0;
            var restoreFailureCoordinator = new DungeonRejoinCoordinator(
                registry,
                characterTransitions,
                (session, partyId) =>
                {
                    restoreFailureAttempts++;
                    return Task.FromResult(false);
                },
                (session, partyId) => Task.CompletedTask,
                session => Task.CompletedTask,
                (session, packet) =>
                {
                    restoreFailurePackets.Add(packet);
                    return Task.CompletedTask;
                });
            sessionDirectory.Register(
                restoreFailureNew.Player.CharacterId,
                restoreFailureNew);
            restoreFailureCoordinator.ProjectCandidateAsync(
                    restoreFailureNew)
                .GetAwaiter()
                .GetResult();
            restoreFailurePackets.Clear();
            restoreFailureCoordinator.HandleRejoinAsync(
                    restoreFailureNew,
                    Header(0x02D7),
                    BuildRejoinRequest(
                        79,
                        restoreFailureOld.Player.UserId))
                .GetAwaiter()
                .GetResult();
            var restoreFailureCandidateStatus = registry.TryGetCandidate(
                restoreFailureNew.Account.AccountId,
                restoreFailureNew.Player.CharacterId,
                restoreFailureNew.Player.UserId,
                out var restoreFailureCandidate);
            Check(
                "party restore failure redetaches the preserved run",
                restoreFailureAttempts == 1
                && restoreFailurePackets.Count == 1
                && IsGenericReject(restoreFailurePackets[0], 0x02D7)
                && restoreFailureCandidateStatus
                    == DungeonAttachmentOperationStatus.Success
                && restoreFailureCandidate.State
                    == DungeonParticipantAttachmentState.Detached
                && restoreFailureNew.Player.CurrentRun == null,
                ref failures);

            using var attachFailureOldClient = new TcpClient();
            using var attachFailureNewClient = new TcpClient();
            var attachFailureOld = CreateSession(
                attachFailureOldClient,
                605,
                1605);
            var attachFailureNew = CreateSession(
                attachFailureNewClient,
                605,
                1605);
            var attachFailureRun = CreateRun(
                attachFailureOld,
                new DungeonInstance(1005, 0));
            var blockingRun = CreateRun(
                attachFailureNew,
                new DungeonInstance(1006, 0));
            registry.RegisterActive(
                Registration(
                    attachFailureOld,
                    attachFailureRun,
                    partyId: 80));
            registry.TryDetach(
                attachFailureOld.Account.AccountId,
                attachFailureOld.Player.CharacterId,
                attachFailureOld.Player.UserId,
                attachFailureOld.SessionId,
                attachFailureRun.CaptureIdentity(),
                out _);
            var attachFailurePackets = new List<byte[]>();
            var attachFailurePartyRestoreCount = 0;
            var attachFailurePartyRollbackCount = 0;
            var attachFailureCoordinator = new DungeonRejoinCoordinator(
                registry,
                characterTransitions,
                (session, partyId) =>
                {
                    attachFailurePartyRestoreCount++;
                    return Task.FromResult(true);
                },
                (session, partyId) =>
                {
                    attachFailurePartyRollbackCount++;
                    return Task.CompletedTask;
                },
                session => Task.CompletedTask,
                (session, packet) =>
                {
                    attachFailurePackets.Add(packet);
                    return Task.CompletedTask;
                });
            sessionDirectory.Register(
                attachFailureNew.Player.CharacterId,
                attachFailureNew);
            attachFailureCoordinator.ProjectCandidateAsync(attachFailureNew)
                .GetAwaiter()
                .GetResult();
            attachFailurePackets.Clear();
            attachFailureCoordinator.HandleRejoinAsync(
                    attachFailureNew,
                    Header(0x02D7),
                    BuildRejoinRequest(
                        80,
                        attachFailureOld.Player.UserId))
                .GetAwaiter()
                .GetResult();
            var attachFailureCandidateStatus = registry.TryGetCandidate(
                attachFailureNew.Account.AccountId,
                attachFailureNew.Player.CharacterId,
                attachFailureNew.Player.UserId,
                out var attachFailureCandidate);
            Check(
                "run attach failure rolls back party and redetaches registry",
                attachFailurePartyRestoreCount == 1
                && attachFailurePartyRollbackCount == 1
                && attachFailurePackets.Count == 1
                && IsGenericReject(attachFailurePackets[0], 0x02D7)
                && attachFailureCandidateStatus
                    == DungeonAttachmentOperationStatus.Success
                && attachFailureCandidate.State
                    == DungeonParticipantAttachmentState.Detached
                && ReferenceEquals(
                    attachFailureNew.Player.CurrentRun,
                    blockingRun),
                ref failures);

            using var cancelOldClient = new TcpClient();
            using var cancelNewClient = new TcpClient();
            var cancelOld = CreateSession(cancelOldClient, 603, 1603);
            var cancelNew = CreateSession(cancelNewClient, 603, 1603);
            var cancelRun = CreateRun(
                cancelOld,
                new DungeonInstance(1003, 0));
            registry.RegisterActive(
                Registration(cancelOld, cancelRun, partyId: 78));
            registry.TryDetach(
                cancelOld.Account.AccountId,
                cancelOld.Player.CharacterId,
                cancelOld.Player.UserId,
                cancelOld.SessionId,
                cancelRun.CaptureIdentity(),
                out _);
            sessionDirectory.Register(
                cancelNew.Player.CharacterId,
                cancelNew);
            packets.Clear();
            coordinator.ProjectCandidateAsync(cancelNew)
                .GetAwaiter()
                .GetResult();
            packets.Clear();
            coordinator.HandleCancelAsync(
                    cancelNew,
                    Header(0x02D8),
                    BitConverter.GetBytes(78))
                .GetAwaiter()
                .GetResult();
            Check(
                "cancel returns common ACK and participant 0x02A9 then ends the run",
                packets.Count == 2
                && IsEnvelope(packets[0], 0x01, 0x02D8)
                && packets[0][15] == 1
                && IsEnvelope(packets[1], 0x00, 0x02A9)
                && BitConverter.ToUInt16(packets[1], 15)
                    == cancelNew.Player.UserId
                && cancelRun.RunState == DungeonRunState.Ended,
                ref failures);

            packets.Clear();
            coordinator.HandleRejoinAsync(
                    cancelNew,
                    Header(0x02D7),
                    new byte[1])
                .GetAwaiter()
                .GetResult();
            Check(
                "malformed request uses the shared generic CMD error envelope",
                packets.Count == 1
                && IsEnvelope(packets[0], 0x01, 0x02D7)
                && packets[0][15] == 0
                && packets[0][16] == 0x04,
                ref failures);

            using var staleOldClient = new TcpClient();
            using var staleNewClient = new TcpClient();
            using var replacementClient = new TcpClient();
            var staleOld = CreateSession(staleOldClient, 607, 1607);
            var staleNew = CreateSession(staleNewClient, 607, 1607);
            var replacement = CreateSession(replacementClient, 607, 1607);
            var staleRun = CreateRun(
                staleOld,
                new DungeonInstance(1008, 0));
            registry.RegisterActive(
                Registration(staleOld, staleRun, partyId: 82));
            registry.TryDetach(
                staleOld.Account.AccountId,
                staleOld.Player.CharacterId,
                staleOld.Player.UserId,
                staleOld.SessionId,
                staleRun.CaptureIdentity(),
                out _);
            var stalePackets = new List<byte[]>();
            var staleRestoreCount = 0;
            var staleCoordinator = new DungeonRejoinCoordinator(
                registry,
                characterTransitions,
                (session, partyId) =>
                {
                    staleRestoreCount++;
                    return Task.FromResult(true);
                },
                (session, partyId) => Task.CompletedTask,
                session => Task.CompletedTask,
                (session, packet) =>
                {
                    stalePackets.Add(packet);
                    return Task.CompletedTask;
                });
            sessionDirectory.Register(
                staleNew.Player.CharacterId,
                staleNew);
            staleCoordinator.ProjectCandidateAsync(staleNew)
                .GetAwaiter()
                .GetResult();
            stalePackets.Clear();
            sessionDirectory.Register(
                replacement.Player.CharacterId,
                replacement);
            staleCoordinator.HandleRejoinAsync(
                    staleNew,
                    Header(0x02D7),
                    BuildRejoinRequest(82, staleOld.Player.UserId))
                .GetAwaiter()
                .GetResult();
            var staleCandidateStatus = registry.TryGetCandidate(
                staleNew.Account.AccountId,
                staleNew.Player.CharacterId,
                staleNew.Player.UserId,
                out var staleCandidate);
            Check(
                "replaced session generation cannot consume or attach a rejoin offer",
                staleRestoreCount == 0
                && stalePackets.Count == 1
                && IsGenericReject(stalePackets[0], 0x02D7)
                && staleCandidateStatus
                    == DungeonAttachmentOperationStatus.Success
                && staleCandidate.State
                    == DungeonParticipantAttachmentState.Detached
                && staleNew.Player.CurrentRun == null,
                ref failures);

            registry.Terminate(
                targetSession.Player.CharacterId,
                targetRun.CaptureIdentity(),
                "selftest_cleanup");
            registry.Terminate(
                restoreFailureNew.Player.CharacterId,
                restoreFailureRun.CaptureIdentity(),
                "selftest_cleanup");
            registry.Terminate(
                attachFailureNew.Player.CharacterId,
                attachFailureRun.CaptureIdentity(),
                "selftest_cleanup");
            registry.Terminate(
                concurrentNew.Player.CharacterId,
                concurrentRun.CaptureIdentity(),
                "selftest_cleanup");
            registry.Terminate(
                staleNew.Player.CharacterId,
                staleRun.CaptureIdentity(),
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
            return session;
        }

        private static DungeonRun CreateRun(
            EnhancedClientSession session,
            DungeonInstance instance)
        {
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                runGeneration: 1,
                DungeonRunState.Active);
            if (!session.Player.TryAttachResumedDungeonRun(run))
                throw new InvalidOperationException("Selftest run attach failed.");
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

        private static byte[] BuildRejoinRequest(
            int partyId,
            int participantUserId)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(partyId);
            writer.WriteInt32(participantUserId);
            return writer.ToArray();
        }

        private static GamePacketHeader Header(ushort type) =>
            new GamePacketHeader { cmd = 1, type = type };

        private static bool IsEnvelope(
            byte[] packet,
            byte cmd,
            ushort type)
        {
            return packet != null
                && packet.Length >= 15
                && packet[0] == cmd
                && BitConverter.ToUInt16(packet, 1) == type;
        }

        private static bool IsGenericReject(byte[] packet, ushort type)
        {
            return IsEnvelope(packet, 0x01, type)
                && packet.Length >= 17
                && packet[15] == 0
                && packet[16] == 0x04;
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
