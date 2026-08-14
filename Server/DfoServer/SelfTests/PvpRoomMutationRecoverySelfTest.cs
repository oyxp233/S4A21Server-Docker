using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Pvp;

namespace DfoServer.SelfTests
{
    public static class PvpRoomMutationRecoverySelfTest
    {
        public static int Run()
        {
            var failures = 0;
            var previousEnabled = GameNetworkConfig.FreeDuelListenerEnabled;
            try
            {
                GameNetworkConfig.Configure(
                    new[] { "--free-duel-channel-listener" });
                CheckKickedTownFailure(ref failures);
                CheckOwnerPromotionTownFailure(ref failures);
                CheckTeardownPublicationFailure(ref failures);
                CheckSettlementTimeoutReset(ref failures);
                CheckStaleSettlementTimeoutIsolation(ref failures);
                CheckNormalMatchLoadP2pBoundaries(ref failures);
                CheckRelayTurnDelayGenerationIsolation(ref failures);
                CheckRelayRequestSkipsMissingPeer(ref failures);
                CheckDeathPublicationFailureRecovery(ref failures);
                CheckTerminalDeathSkipsMissingPeer(ref failures);
                CheckDisposeCancelsScheduledWork(ref failures);
                CheckLobbySnapshotExcludesStaleOwner(ref failures);
                CheckDirectEnterRejectsStaleOwner(ref failures);
                CheckRoomInviteLifecycle(ref failures);
                CheckObserverMalformedAndPublicationFailure(ref failures);
                CheckUdpEndpointRefreshDefersMissingPeer(ref failures);
                CheckPvpTimeoutFailClosed(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] PvP room mutation recovery self-test threw: " +
                    ex);
                failures++;
            }
            finally
            {
                GameNetworkConfig.Configure(
                    previousEnabled
                        ? new[] { "--free-duel-channel-listener" }
                        : Array.Empty<string>());
            }

            Console.WriteLine(
                failures == 0
                    ? "PvpRoomMutationRecoverySelfTest OK"
                    : $"PvpRoomMutationRecoverySelfTest FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckKickedTownFailure(ref int failures)
        {
            using var fixture = new Fixture();
            var owner = fixture.CreateReadySession(6701);
            var member = fixture.CreateReadySession(6702);
            var room = fixture.CreateRoom(owner, member);
            room.TryGetSeatForSession(member.SessionId, out var memberSeat);
            fixture.ClearSends();
            fixture.FailTownFor(member);

            fixture.Handler.HandleSetSeatState(
                    owner,
                    new GamePacketHeader(),
                    new[] { memberSeat, FreeDuelRoom.ClosedSeatState })
                .GetAwaiter()
                .GetResult();

            Check(
                "kicked member Town failure keeps Registry removal and " +
                "suppresses direct vacancy",
                !fixture.Rooms.TryGetRoomForMember(
                    member.Player.CharacterId,
                    member.SessionId,
                    out _,
                    out _) &&
                fixture.Rooms.TryGetRoomForMember(
                    owner.Player.CharacterId,
                    owner.SessionId,
                    out var remaining,
                    out _) &&
                remaining.OwnerSessionId == owner.SessionId &&
                member.Player.UserState == 0 &&
                !fixture.Sends.Any(
                    sent => sent.SessionId == member.SessionId),
                ref failures);
        }

        private static void CheckOwnerPromotionTownFailure(ref int failures)
        {
            using var fixture = new Fixture();
            var owner = fixture.CreateReadySession(6711);
            var member = fixture.CreateReadySession(6712);
            var room = fixture.CreateRoom(owner, member);
            fixture.ClearSends();
            fixture.FailTownFor(owner);

            fixture.Handler.HandleSetSeatState(
                    owner,
                    new GamePacketHeader(),
                    new[]
                    {
                        room.ManagerSeat,
                        FreeDuelRoom.ClosedSeatState
                    })
                .GetAwaiter()
                .GetResult();

            Check(
                "owner Town failure preserves promoted Registry generation",
                !fixture.Rooms.TryGetRoomForMember(
                    owner.Player.CharacterId,
                    owner.SessionId,
                    out _,
                    out _) &&
                fixture.Rooms.TryGetRoomForMember(
                    member.Player.CharacterId,
                    member.SessionId,
                    out var promoted,
                    out _) &&
                promoted.OwnerSessionId == member.SessionId &&
                owner.Player.UserState == 0 &&
                !fixture.Sends.Any(
                    sent => sent.SessionId == owner.SessionId),
                ref failures);
        }

        private static void CheckTeardownPublicationFailure(ref int failures)
        {
            using var fixture = new Fixture();
            var owner = fixture.CreateReadySession(6721);
            var observer = fixture.CreateReadySession(6722);
            var room = fixture.CreateRoom(owner);
            fixture.ClearSends();
            fixture.FailQueuedWhen(
                (session, packet) =>
                    session.SessionId == observer.SessionId &&
                    PacketType(packet) ==
                        PvpRoomHandler.RoomStateNotificationType);

            fixture.Handler.HandleSetSeatState(
                    owner,
                    new GamePacketHeader(),
                    new[]
                    {
                        room.ManagerSeat,
                        FreeDuelRoom.ClosedSeatState
                    })
                .GetAwaiter()
                .GetResult();

            if (!MakePvpRoomRequest.TryParse(
                    Fixture.MakeRoomBody,
                    out var request,
                    out _))
            {
                throw new InvalidOperationException(
                    "replacement room request did not parse");
            }
            var replacementCreated = fixture.Rooms.TryCreate(
                GameNetworkConfig.FreeDuelGamePort,
                6799,
                Guid.NewGuid(),
                6799,
                request,
                out var replacement,
                out _);

            Check(
                "teardown publication failure isolates listener before " +
                "room id release",
                fixture.QueuedFailureCount == 1 &&
                replacementCreated &&
                replacement.RoomId == room.RoomId,
                ref failures);
        }

        private static void CheckSettlementTimeoutReset(ref int failures)
        {
            using var fixture = new Fixture(
                TimeSpan.FromMilliseconds(40));
            var owner = fixture.CreateReadySession(6731);
            var member = fixture.CreateReadySession(6732);
            var room = fixture.CreateRoom(owner, member);
            fixture.StartMatch(owner, member);
            room = fixture.GetRoom(room.RoomId);
            var matchGeneration = room.MatchGeneration;
            fixture.ReportDeath(member);

            var reset = WaitUntil(
                () =>
                {
                    var current = fixture.GetRoom(room.RoomId);
                    return current.RoomState ==
                               FreeDuelRoom.WaitingRoomState &&
                           current.SettlementPhase ==
                               FreeDuelRoom.WaitingSettlementPhase;
                },
                TimeSpan.FromSeconds(2));
            room = fixture.GetRoom(room.RoomId);
            Check(
                "rank/end timeout chain resets one exact match generation",
                reset && room.MatchGeneration == matchGeneration,
                ref failures);
        }

        private static void CheckStaleSettlementTimeoutIsolation(
            ref int failures)
        {
            using var fixture = new Fixture(
                TimeSpan.FromMilliseconds(150));
            var owner = fixture.CreateReadySession(6741);
            var member = fixture.CreateReadySession(6742);
            var room = fixture.CreateRoom(owner, member);
            fixture.StartMatch(owner, member);
            room = fixture.GetRoom(room.RoomId);
            var firstGeneration = room.MatchGeneration;
            fixture.ReportDeath(member);
            fixture.AcknowledgeSettlement(owner, member);
            fixture.StartMatch(owner, member);
            room = fixture.GetRoom(room.RoomId);
            var secondGeneration = room.MatchGeneration;
            Thread.Sleep(500);
            room = fixture.GetRoom(room.RoomId);

            Check(
                "old settlement timeouts cannot reset a new match",
                secondGeneration == firstGeneration + 1 &&
                room.MatchGeneration == secondGeneration &&
                room.RoomState == FreeDuelRoom.StartedRoomState &&
                room.SettlementPhase ==
                    FreeDuelRoom.CombatSettlementPhase,
                ref failures);
        }

        private static void CheckNormalMatchLoadP2pBoundaries(
            ref int failures)
        {
            using var fixture = new Fixture();
            var owner = fixture.CreateReadySession(6751);
            var member = fixture.CreateReadySession(6752);
            var room = fixture.CreateRoom(owner, member);
            fixture.StartMatch(owner, member);
            fixture.ClearSends();
            room = fixture.GetRoom(room.RoomId);

            fixture.Handler.HandleCompleteLoadPvp(
                    owner,
                    new GamePacketHeader(),
                    Array.Empty<byte>())
                .GetAwaiter()
                .GetResult();
            fixture.Handler.HandleConnectP2pPvp(
                    owner,
                    new GamePacketHeader(),
                    new byte[] { 0 })
                .GetAwaiter()
                .GetResult();
            var afterValidSignals = fixture.GetRoom(room.RoomId);
            Check(
                "normal-match load/P2P signals preserve state without " +
                "fabricated packets",
                SameMatchSnapshot(room, afterValidSignals) &&
                fixture.Sends.Count == 0,
                ref failures);

            fixture.Handler.HandleConnectP2pPvp(
                    owner,
                    new GamePacketHeader(),
                    new byte[] { 1, 0 })
                .GetAwaiter()
                .GetResult();
            var afterMalformedSignal = fixture.GetRoom(room.RoomId);
            Check(
                "malformed CONNECT_P2P is fail-closed and packet-silent",
                SameMatchSnapshot(room, afterMalformedSignal) &&
                fixture.Sends.Count == 0,
                ref failures);

            fixture.ReplaceCurrentSessionRegistration(owner);
            fixture.Handler.HandleCompleteLoadPvp(
                    owner,
                    new GamePacketHeader(),
                    Array.Empty<byte>())
                .GetAwaiter()
                .GetResult();
            fixture.Handler.HandleConnectP2pPvp(
                    owner,
                    new GamePacketHeader(),
                    new byte[] { 0 })
                .GetAwaiter()
                .GetResult();
            var afterStaleSignals = fixture.GetRoom(room.RoomId);
            Check(
                "stale character generation cannot advance load/P2P flow",
                SameMatchSnapshot(room, afterStaleSignals) &&
                fixture.Sends.Count == 0,
                ref failures);
        }

        private static void CheckRelayTurnDelayGenerationIsolation(
            ref int failures)
        {
            using var fixture = new Fixture(
                relayBattleStartDelay: TimeSpan.FromMilliseconds(600),
                relayBattleTurnDelay: TimeSpan.FromMilliseconds(300));
            var owner = fixture.CreateReadySession(6761);
            var memberOne = fixture.CreateReadySession(6762);
            var memberTwo = fixture.CreateReadySession(6763);
            var memberThree = fixture.CreateReadySession(6764);
            var room = fixture.CreateRoom(
                owner,
                memberOne,
                memberTwo,
                memberThree);
            fixture.SetBattleMode(owner, 3);
            fixture.StartMatch(
                owner,
                memberOne,
                memberTwo,
                memberThree);
            room = fixture.GetRoom(room.RoomId);
            var firstMatchGeneration = room.MatchGeneration;

            fixture.ReportDeath(memberOne);
            Thread.Sleep(200);
            fixture.ReportDeath(memberTwo);
            fixture.AcknowledgeSettlement(
                owner,
                memberOne,
                memberTwo,
                memberThree);
            fixture.StartMatch(
                owner,
                memberOne,
                memberTwo,
                memberThree);
            room = fixture.GetRoom(room.RoomId);
            var secondMatchGeneration = room.MatchGeneration;
            fixture.ClearSends();

            Thread.Sleep(480);
            var staleTurnCount = fixture.CountPackets(
                PvpRoomHandler.PvpTurnPlayerNotificationType);
            Check(
                "old relay start/turn delays cannot publish into a new match",
                secondMatchGeneration == firstMatchGeneration + 1 &&
                staleTurnCount == 0,
                ref failures);

            var currentTurnPublished = WaitUntil(
                () => fixture.CountPackets(
                    PvpRoomHandler.PvpTurnPlayerNotificationType) > 0,
                TimeSpan.FromSeconds(2));
            Check(
                "current relay match generation publishes its delayed turn",
                currentTurnPublished,
                ref failures);
        }

        private static void CheckRelayRequestSkipsMissingPeer(
            ref int failures)
        {
            using var fixture = new Fixture(
                relayBattleStartDelay: TimeSpan.FromSeconds(5));
            var owner = fixture.CreateReadySession(6771);
            var memberOne = fixture.CreateReadySession(6772);
            var memberTwo = fixture.CreateReadySession(6773);
            var missingMember = fixture.CreateReadySession(6774);
            var room = fixture.CreateRoom(
                owner,
                memberOne,
                memberTwo,
                missingMember);
            fixture.SetBattleMode(owner, 3);
            fixture.StartMatch(
                owner,
                memberOne,
                memberTwo,
                missingMember);
            room = fixture.GetRoom(room.RoomId);
            fixture.ReplaceCurrentSessionRegistration(missingMember);
            fixture.ClearSends();

            fixture.RequestFight(owner);
            var afterRequest = fixture.GetRoom(room.RoomId);
            Check(
                "relay fight request skips a directory-missing peer",
                SameMatchSnapshot(room, afterRequest) &&
                fixture.CountPackets(
                    PvpRoomHandler.PvpRequestFightNotificationType) == 3,
                ref failures);
        }

        private static void CheckDeathPublicationFailureRecovery(
            ref int failures)
        {
            using var fixture = new Fixture(
                relayBattleStartDelay: TimeSpan.FromSeconds(5),
                relayBattleTurnDelay: TimeSpan.FromMilliseconds(60));
            var owner = fixture.CreateReadySession(6781);
            var dead = fixture.CreateReadySession(6782);
            var memberTwo = fixture.CreateReadySession(6783);
            var failingTarget = fixture.CreateReadySession(6784);
            var room = fixture.CreateRoom(
                owner,
                dead,
                memberTwo,
                failingTarget);
            fixture.SetBattleMode(owner, 3);
            fixture.StartMatch(
                owner,
                dead,
                memberTwo,
                failingTarget);
            room = fixture.GetRoom(room.RoomId);
            room.TryGetSeatForSession(
                dead.SessionId,
                out var deadSeat);
            fixture.ClearSends();
            fixture.FailQueuedWhen(
                (target, packet) =>
                    target.SessionId == failingTarget.SessionId &&
                    PacketType(packet) ==
                        PvpRoomHandler.DiePvpCharacterNotificationType);

            fixture.ReportDeath(dead);
            var afterDeath = fixture.GetRoom(room.RoomId);
            Check(
                "death publication failure closes only its target and " +
                "keeps the authoritative mutation",
                !afterDeath.GetAliveState(deadSeat) &&
                afterDeath.SettlementPhase ==
                    FreeDuelRoom.CombatSettlementPhase &&
                fixture.QueuedFailureCount == 1 &&
                fixture.IsClientClosed(failingTarget) &&
                fixture.CountPackets(
                    PvpRoomHandler.DiePvpCharacterNotificationType) == 3,
                ref failures);

            var relayTurnContinued = WaitUntil(
                () => fixture.CountPackets(
                    PvpRoomHandler.PvpTurnPlayerNotificationType) > 0,
                TimeSpan.FromSeconds(2));
            Check(
                "nonterminal death still schedules the current relay turn",
                relayTurnContinued,
                ref failures);
        }

        private static void CheckTerminalDeathSkipsMissingPeer(
            ref int failures)
        {
            using var fixture = new Fixture(
                settlementAckTimeout: TimeSpan.FromMilliseconds(40),
                relayBattleStartDelay: TimeSpan.FromSeconds(5));
            var owner = fixture.CreateReadySession(6791);
            var member = fixture.CreateReadySession(6792);
            var room = fixture.CreateRoom(owner, member);
            fixture.StartMatch(owner, member);
            room = fixture.GetRoom(room.RoomId);
            var matchGeneration = room.MatchGeneration;
            fixture.ReplaceCurrentSessionRegistration(owner);
            fixture.ClearSends();

            fixture.ReportDeath(member);
            var reset = WaitUntil(
                () =>
                {
                    var current = fixture.GetRoom(room.RoomId);
                    return current.RoomState ==
                               FreeDuelRoom.WaitingRoomState &&
                           current.SettlementPhase ==
                               FreeDuelRoom.WaitingSettlementPhase;
                },
                TimeSpan.FromSeconds(2));
            room = fixture.GetRoom(room.RoomId);
            Check(
                "terminal death skips a missing peer and completes timeout " +
                "settlement",
                reset &&
                room.MatchGeneration == matchGeneration &&
                fixture.CountPackets(
                    PvpRoomHandler.DiePvpCharacterNotificationType) == 1 &&
                fixture.CountPackets(
                    PvpRoomHandler.RequestPvpRankNotificationType) == 1,
                ref failures);
        }

        private static void CheckDisposeCancelsScheduledWork(
            ref int failures)
        {
            using var fixture = new Fixture(
                settlementAckTimeout: TimeSpan.FromSeconds(5),
                relayBattleStartDelay: TimeSpan.FromSeconds(5));
            var owner = fixture.CreateReadySession(6801);
            var member = fixture.CreateReadySession(6802);
            fixture.CreateRoom(owner, member);
            fixture.SetBattleMode(owner, 3);
            fixture.StartMatch(owner, member);
            fixture.ReportDeath(member);
            var scheduledBeforeDispose =
                fixture.Handler.PendingScheduledWorkCountForTest;

            fixture.DisposeHandler();
            var drained = WaitUntil(
                () => fixture.Handler.PendingScheduledWorkCountForTest == 0,
                TimeSpan.FromSeconds(1));
            Check(
                "handler dispose cancels relay and settlement delays promptly",
                scheduledBeforeDispose >= 2 && drained,
                ref failures);
        }

        private static void CheckLobbySnapshotExcludesStaleOwner(
            ref int failures)
        {
            using var fixture = new Fixture();
            var owner = fixture.CreateReadySession(6811);
            fixture.CreateRoom(owner);
            var newcomer = fixture.CreatePendingLobbySession(6812);
            fixture.ReplaceCurrentSessionRegistration(owner);
            fixture.ClearSends();

            fixture.CompleteLobby(newcomer);
            var roomInfoBody = fixture.GetLastPacketBody(
                newcomer.SessionId,
                PvpRoomHandler.RoomInfoNotificationType);
            Check(
                "lobby snapshot excludes a stale owner generation",
                fixture.Handler.IsLobbyReadyForTest(newcomer.SessionId) &&
                roomInfoBody.Length >= 2 &&
                BitConverter.ToUInt16(roomInfoBody, 0) == 0,
                ref failures);
        }

        private static void CheckDirectEnterRejectsStaleOwner(
            ref int failures)
        {
            using var fixture = new Fixture();
            var owner = fixture.CreateReadySession(6821);
            var target = fixture.CreateReadySession(
                6822,
                connected: true);
            var room = fixture.CreateRoom(owner);
            fixture.ReplaceCurrentSessionRegistration(owner);

            fixture.EnterRoom(target, room.RoomId);
            var current = fixture.GetRoom(room.RoomId);
            Check(
                "direct enter rejects a room whose owner generation is stale",
                SameMatchSnapshot(room, current) &&
                target.Player.UserState == 0 &&
                !fixture.Rooms.TryGetRoomForMember(
                    target.Player.CharacterId,
                    target.SessionId,
                    out _,
                    out _),
                ref failures);
        }

        private static void CheckRoomInviteLifecycle(ref int failures)
        {
            using (var fixture = new Fixture())
            {
                var owner = fixture.CreateReadySession(6831);
                var target = fixture.CreateReadySession(
                    6832,
                    connected: true);
                var room = fixture.CreateRoom(owner);

                var delivered = fixture.RequestInvite(
                    owner,
                    target,
                    1101);
                var joined = fixture.RespondToInvite(
                    owner,
                    target,
                    1101);
                Check(
                    "current invite is consumed once and joins its exact room",
                    delivered &&
                    joined &&
                    fixture.Handler.PendingRoomInviteCountForTest == 0 &&
                    fixture.Rooms.TryGetRoomForMember(
                        target.Player.CharacterId,
                        target.SessionId,
                        out var joinedRoom,
                        out _) &&
                    joinedRoom.RoomId == room.RoomId &&
                    joinedRoom.GenerationId == room.GenerationId,
                    ref failures);
            }

            using (var fixture = new Fixture())
            {
                var owner = fixture.CreateReadySession(6841);
                var target = fixture.CreateReadySession(
                    6842,
                    connected: true);
                var room = fixture.CreateRoom(owner);
                var delivered = fixture.RequestInvite(
                    owner,
                    target,
                    1102);
                var replacement = fixture.RecycleRoomGeneration(
                    owner,
                    room);

                var joined = fixture.RespondToInvite(
                    owner,
                    target,
                    1102);
                Check(
                    "stale room-generation invite is rejected and consumed",
                    delivered &&
                    replacement.RoomId == room.RoomId &&
                    replacement.GenerationId != room.GenerationId &&
                    !joined &&
                    fixture.Handler.PendingRoomInviteCountForTest == 0 &&
                    !fixture.Rooms.TryGetRoomForMember(
                        target.Player.CharacterId,
                        target.SessionId,
                        out _,
                        out _),
                    ref failures);
            }

            using (var fixture = new Fixture())
            {
                var owner = fixture.CreateReadySession(6851);
                var target = fixture.CreateReadySession(
                    6852,
                    connected: true);
                fixture.CreateRoom(owner);
                var delivered = fixture.RequestInvite(
                    owner,
                    target,
                    1103);

                fixture.EndSession(target);
                Check(
                    "target session-end removes the real Handler invite",
                    delivered &&
                    fixture.Handler.PendingRoomInviteCountForTest == 0,
                    ref failures);
            }

            using (var fixture = new Fixture())
            {
                var owner = fixture.CreateReadySession(6856);
                var inviter = fixture.CreateReadySession(6857);
                var target = fixture.CreateReadySession(
                    6858,
                    connected: true);
                fixture.CreateRoom(owner, inviter);
                fixture.ReplaceCurrentSessionRegistration(owner);

                var delivered = fixture.RequestInvite(
                    inviter,
                    target,
                    1104);
                Check(
                    "member cannot invite into a stale-owner room",
                    !delivered &&
                    fixture.Handler.PendingRoomInviteCountForTest == 0,
                    ref failures);
            }

            using (var fixture = new Fixture())
            {
                var owner = fixture.CreateReadySession(6861);
                var target = fixture.CreateReadySession(
                    6862,
                    connected: true);
                fixture.CreateRoom(owner);
                fixture.DisposeHandler();

                var delivered = fixture.RequestInvite(
                    owner,
                    target,
                    1105);
                Check(
                    "disposed Handler cannot publish an untracked invite",
                    !delivered &&
                    fixture.Handler.PendingRoomInviteCountForTest == 0,
                    ref failures);
            }
        }

        private static void CheckObserverMalformedAndPublicationFailure(
            ref int failures)
        {
            using var fixture = new Fixture();
            var owner = fixture.CreateReadySession(6871);
            var member = fixture.CreateReadySession(
                6872,
                connected: true);
            var room = fixture.CreateRoom(owner, member);
            room.TryGetSeatForSession(
                member.SessionId,
                out var memberSeat);
            fixture.ClearSends();

            fixture.SetSeatState(
                member,
                new[] { memberSeat });
            var afterMalformed = fixture.GetRoom(room.RoomId);
            Check(
                "malformed observer mutation preserves the room snapshot",
                SameMatchSnapshot(room, afterMalformed) &&
                fixture.CountPackets(
                    PvpRoomHandler.SeatStateNotificationType) == 0,
                ref failures);

            fixture.FailQueuedWhen(
                (target, packet) =>
                    target.SessionId == owner.SessionId &&
                    PacketType(packet) ==
                        PvpRoomHandler.SeatStateNotificationType);
            fixture.SetSeatState(
                member,
                new[]
                {
                    memberSeat,
                    FreeDuelRoom.ObserverSeatState
                });
            room = fixture.GetRoom(room.RoomId);
            Check(
                "observer publication failure closes one target and keeps " +
                "the authoritative seat state",
                room.IsObserverSeat(memberSeat) &&
                fixture.QueuedFailureCount == 1 &&
                fixture.IsClientClosed(owner) &&
                fixture.CountPackets(
                    PvpRoomHandler.SeatStateNotificationType) == 1,
                ref failures);
        }

        private static void CheckUdpEndpointRefreshDefersMissingPeer(
            ref int failures)
        {
            using var fixture = new Fixture();
            var owner = fixture.CreateReadySession(6881);
            var member = fixture.CreateReadySession(6882);
            var room = fixture.CreateRoom(owner, member);
            fixture.ReplaceCurrentSessionRegistration(member);
            fixture.ClearSends();

            fixture.RefreshUdpEndpoint(owner);
            var current = fixture.GetRoom(room.RoomId);
            Check(
                "UDP endpoint refresh defers a directory-missing peer",
                SameMatchSnapshot(room, current) &&
                fixture.Sends.Count == 0,
                ref failures);
        }

        private static void CheckPvpTimeoutFailClosed(ref int failures)
        {
            using var fixture = new Fixture();
            var owner = fixture.CreateReadySession(6891);
            var member = fixture.CreateReadySession(6892);
            var room = fixture.CreateRoom(owner, member);
            fixture.StartMatch(owner, member);
            room = fixture.GetRoom(room.RoomId);
            fixture.ClearSends();

            fixture.ReportPvpTimeout(
                owner,
                new byte[PvpTimeOutRequest.BodyLength]);
            fixture.ReportPvpTimeout(
                owner,
                new byte[PvpTimeOutRequest.BodyLength - 1]);
            var afterReports = fixture.GetRoom(room.RoomId);
            Check(
                "valid and malformed PVP_TIME_OUT remain fail-closed",
                SameMatchSnapshot(room, afterReports) &&
                fixture.Sends.Count == 0,
                ref failures);
        }

        private static bool SameMatchSnapshot(
            FreeDuelRoom expected,
            FreeDuelRoom actual)
        {
            return expected != null &&
                   actual != null &&
                   actual.RoomId == expected.RoomId &&
                   actual.GenerationId == expected.GenerationId &&
                   actual.Revision == expected.Revision &&
                   actual.MatchGeneration == expected.MatchGeneration &&
                   actual.RoomState == expected.RoomState &&
                   actual.SettlementPhase == expected.SettlementPhase;
        }

        private static bool WaitUntil(
            Func<bool> predicate,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                    return true;
                Thread.Sleep(10);
            }
            return predicate();
        }

        private static ushort PacketType(byte[] packet)
        {
            return packet != null && packet.Length >= 3
                ? BitConverter.ToUInt16(packet, 1)
                : (ushort)0;
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                $"[{(condition ? "PASS" : "FAIL")}] {label}");
            if (!condition)
                failures++;
        }

        private sealed class Fixture : IDisposable
        {
            internal static readonly byte[] MakeRoomBody =
                { 0x06, 0x00, 0x00, 0x00, 0x00 };

            private readonly string _databasePath;
            private readonly GameDatabase _database;
            private readonly SessionDirectory _sessions;
            private readonly List<TcpClient> _clients =
                new List<TcpClient>();
            private readonly HashSet<Guid> _townFailures =
                new HashSet<Guid>();
            private Func<EnhancedClientSession, byte[], bool>
                _queuedFailure;

            internal Fixture(
                TimeSpan? settlementAckTimeout = null,
                TimeSpan? relayBattleStartDelay = null,
                TimeSpan? relayBattleTurnDelay = null)
            {
                _databasePath = Path.Combine(
                    Path.GetTempPath(),
                    "pvp_room_mutation_" +
                    Guid.NewGuid().ToString("N") + ".db");
                _database = new GameDatabase(
                    _databasePath,
                    ServerPaths.SchemaFilePath);
                _sessions = new SessionDirectory();
                Rooms = new FreeDuelRoomRegistry();
                Handler = new PvpRoomHandler(
                    _sessions,
                    _ => new byte[] { 0x01 },
                    new CharacterTransitionCoordinator(_sessions),
                    isFreeDuelAvailable: () => true,
                    rooms: Rooms,
                    announceTownArrivalWithinTransition:
                        _ => Task.FromResult(true),
                    sendQueuedPacket: SendQueuedAsync,
                    database: _database,
                    sendTownPacket: SendTownAsync,
                    settlementAckTimeout:
                        settlementAckTimeout,
                    relayBattleStartDelay:
                        relayBattleStartDelay,
                    relayBattleTurnDelay:
                        relayBattleTurnDelay);
            }

            internal PvpRoomHandler Handler { get; }

            internal FreeDuelRoomRegistry Rooms { get; }

            internal List<SentPacket> Sends { get; } =
                new List<SentPacket>();

            internal int QueuedFailureCount { get; private set; }

            internal EnhancedClientSession CreateReadySession(
                int characterId,
                bool connected = false)
            {
                return CreateSession(
                    characterId,
                    connected,
                    completeLobby: true);
            }

            internal EnhancedClientSession CreatePendingLobbySession(
                int characterId)
            {
                return CreateSession(
                    characterId,
                    connected: false,
                    completeLobby: false);
            }

            private EnhancedClientSession CreateSession(
                int characterId,
                bool connected,
                bool completeLobby)
            {
                var client = CreateClient(connected);
                var session = new EnhancedClientSession(
                    client,
                    new GamePacketHeader(),
                    GameNetworkConfig.FreeDuelGamePort);
                session.Account = new AccountRecord
                {
                    AccountId = characterId,
                    MId = "pvp-mutation-" + characterId
                };
                session.Player.CharacterId = characterId;
                session.Player.UserId = (ushort)characterId;
                session.Player.UserState = 0;
                session.Player.Name =
                    Encoding.UTF8.GetBytes("Pvp" + characterId);
                session.Player.CurTownId = 1;
                session.Player.CurAreaId = 0;
                session.GameSession = new GameSession(
                    session,
                    _database);
                _sessions.Register(characterId, session);
                if (!completeLobby)
                    return session;

                CompleteLobby(session);
                return session;
            }

            internal void CompleteLobby(
                EnhancedClientSession session)
            {
                if (!Handler.CanPublishLobbySnapshotForTest(session))
                {
                    throw new InvalidOperationException(
                        "fixture is not eligible for PvP lobby snapshot: " +
                        $"cid={session.Player.CharacterId} " +
                        $"uid={session.Player.UserId} " +
                        $"port={session.ListenerPort} " +
                        $"enabled={GameNetworkConfig.FreeDuelListenerEnabled}");
                }
                Handler.HandleLobbyReadyAsync(session)
                    .GetAwaiter()
                    .GetResult();
                if (!Handler.IsLobbyReadyForTest(session.SessionId))
                {
                    throw new InvalidOperationException(
                        "fixture did not complete PvP lobby readiness");
                }
            }

            private TcpClient CreateClient(bool connected)
            {
                var client = new TcpClient();
                _clients.Add(client);
                if (!connected)
                    return client;

                var listener = new TcpListener(
                    IPAddress.Loopback,
                    0);
                listener.Start();
                try
                {
                    var endpoint = (IPEndPoint)listener.LocalEndpoint;
                    client.Connect(
                        IPAddress.Loopback,
                        endpoint.Port);
                    _clients.Add(listener.AcceptTcpClient());
                }
                finally
                {
                    listener.Stop();
                }
                return client;
            }

            internal FreeDuelRoom CreateRoom(
                EnhancedClientSession owner,
                params EnhancedClientSession[] members)
            {
                Handler.HandleMakeRoom(
                        owner,
                        new GamePacketHeader(),
                        MakeRoomBody)
                    .GetAwaiter()
                    .GetResult();
                var room = Rooms.SnapshotForListener(
                        GameNetworkConfig.FreeDuelGamePort)
                    .Single(candidate =>
                        candidate.OwnerSessionId == owner.SessionId);
                foreach (var member in members)
                {
                    Handler.HandleEnterRoom(
                            member,
                            new GamePacketHeader(),
                            new byte[]
                            {
                                (byte)room.RoomId,
                                (byte)(room.RoomId >> 8),
                                0x00
                            })
                        .GetAwaiter()
                        .GetResult();
                    room = Rooms.SnapshotForListener(
                            GameNetworkConfig.FreeDuelGamePort)
                        .Single(candidate =>
                            candidate.RoomId == room.RoomId);
                }
                return room;
            }

            internal FreeDuelRoom GetRoom(ushort roomId)
            {
                return Rooms.SnapshotForListener(
                        GameNetworkConfig.FreeDuelGamePort)
                    .Single(room => room.RoomId == roomId);
            }

            internal void EnterRoom(
                EnhancedClientSession session,
                ushort roomId)
            {
                Handler.HandleEnterRoom(
                        session,
                        new GamePacketHeader(),
                        new byte[]
                        {
                            (byte)roomId,
                            (byte)(roomId >> 8),
                            0x00
                        })
                    .GetAwaiter()
                    .GetResult();
            }

            internal bool RequestInvite(
                EnhancedClientSession inviter,
                EnhancedClientSession target,
                int peerToken)
            {
                return Handler.HandleRoomInviteRequestAsync(
                        inviter,
                        target,
                        peerToken)
                    .GetAwaiter()
                    .GetResult();
            }

            internal bool RespondToInvite(
                EnhancedClientSession inviter,
                EnhancedClientSession target,
                int peerToken)
            {
                return Handler.HandleRoomInviteResponseAsync(
                        inviter,
                        target,
                        peerToken,
                        commit =>
                        {
                            commit();
                            return Task.CompletedTask;
                        })
                    .GetAwaiter()
                    .GetResult();
            }

            internal FreeDuelRoom RecycleRoomGeneration(
                EnhancedClientSession owner,
                FreeDuelRoom expectedRoom)
            {
                if (!Rooms.TryTakeOwnedRoomForRemoval(
                        owner.Player.CharacterId,
                        owner.SessionId,
                        out var removed) ||
                    removed.RoomId != expectedRoom.RoomId ||
                    !Rooms.ReleaseRemovedRoomId(removed) ||
                    !MakePvpRoomRequest.TryParse(
                        MakeRoomBody,
                        out var request,
                        out _) ||
                    !Rooms.TryCreate(
                        GameNetworkConfig.FreeDuelGamePort,
                        owner.Player.CharacterId,
                        owner.SessionId,
                        owner.Player.UserId,
                        request,
                        out var replacement,
                        out _))
                {
                    throw new InvalidOperationException(
                        "fixture could not recycle PvP room generation");
                }

                return replacement;
            }

            internal void SetBattleMode(
                EnhancedClientSession owner,
                byte battleMode)
            {
                Handler.HandleSetTeamMode(
                        owner,
                        new GamePacketHeader(),
                        new[] { battleMode })
                    .GetAwaiter()
                    .GetResult();
            }

            internal void StartMatch(
                EnhancedClientSession owner,
                params EnhancedClientSession[] members)
            {
                foreach (var member in members)
                {
                    Handler.HandleSetReadyState(
                            member,
                            new GamePacketHeader(),
                            new byte[] { 1 })
                        .GetAwaiter()
                        .GetResult();
                }
                Handler.HandleSetReadyState(
                        owner,
                        new GamePacketHeader(),
                        new byte[] { 1 })
                    .GetAwaiter()
                    .GetResult();
            }

            internal void ReportDeath(EnhancedClientSession dead)
            {
                Handler.HandleDiePvpCharacter(
                        dead,
                        new GamePacketHeader(),
                        BitConverter.GetBytes(dead.Player.UserId))
                    .GetAwaiter()
                    .GetResult();
            }

            internal void RequestFight(EnhancedClientSession session)
            {
                Handler.HandlePvpRequestFight(
                        session,
                        new GamePacketHeader(),
                        Array.Empty<byte>())
                    .GetAwaiter()
                    .GetResult();
            }

            internal void SetSeatState(
                EnhancedClientSession session,
                byte[] body)
            {
                Handler.HandleSetSeatState(
                        session,
                        new GamePacketHeader(),
                        body)
                    .GetAwaiter()
                    .GetResult();
            }

            internal void RefreshUdpEndpoint(
                EnhancedClientSession session)
            {
                session.Player.UpdateReportedUdpEndpoint(
                    natType: 0,
                    IPAddress.Loopback,
                    IPAddress.Loopback,
                    port: 30000,
                    mtu: 1500);
                Handler.HandleReportedUdpEndpointChanged(session)
                    .GetAwaiter()
                    .GetResult();
            }

            internal void ReportPvpTimeout(
                EnhancedClientSession session,
                byte[] body)
            {
                Handler.HandlePvpTimeOut(
                        session,
                        new GamePacketHeader(),
                        body)
                    .GetAwaiter()
                    .GetResult();
            }

            internal void AcknowledgeSettlement(
                EnhancedClientSession owner,
                params EnhancedClientSession[] members)
            {
                var rankBody = new byte[
                    PvpRankResponseRequest.BodyLength];
                Handler.HandlePvpRankResponse(
                        owner,
                        new GamePacketHeader(),
                        rankBody)
                    .GetAwaiter()
                    .GetResult();
                foreach (var member in members)
                {
                    Handler.HandlePvpRankResponse(
                            member,
                            new GamePacketHeader(),
                            rankBody)
                        .GetAwaiter()
                        .GetResult();
                }
                Handler.HandleEndPvpResult(
                        owner,
                        new GamePacketHeader(),
                        Array.Empty<byte>())
                    .GetAwaiter()
                    .GetResult();
                foreach (var member in members)
                {
                    Handler.HandleEndPvpResult(
                            member,
                            new GamePacketHeader(),
                            Array.Empty<byte>())
                        .GetAwaiter()
                        .GetResult();
                }
            }

            internal void ReplaceCurrentSessionRegistration(
                EnhancedClientSession staleSession)
            {
                var client = new TcpClient();
                _clients.Add(client);
                var replacement = new EnhancedClientSession(
                    client,
                    new GamePacketHeader(),
                    GameNetworkConfig.FreeDuelGamePort);
                replacement.Account = new AccountRecord
                {
                    AccountId = staleSession.Account.AccountId,
                    MId = staleSession.Account.MId + "-replacement"
                };
                replacement.Player.CharacterId =
                    staleSession.Player.CharacterId;
                replacement.Player.UserId = staleSession.Player.UserId;
                replacement.GameSession = new GameSession(
                    replacement,
                    _database);
                _sessions.Register(
                    replacement.Player.CharacterId,
                    replacement);
            }

            internal void ClearSends()
            {
                lock (Sends)
                    Sends.Clear();
                QueuedFailureCount = 0;
            }

            internal int CountPackets(ushort packetType)
            {
                lock (Sends)
                {
                    return Sends.Count(
                        sent => PacketType(sent.Packet) == packetType);
                }
            }

            internal byte[] GetLastPacketBody(
                Guid sessionId,
                ushort packetType)
            {
                lock (Sends)
                {
                    var packet = Sends.Last(
                        sent => sent.SessionId == sessionId &&
                                PacketType(sent.Packet) == packetType)
                        .Packet;
                    if (packet.Length < 15)
                    {
                        throw new InvalidOperationException(
                            "captured packet has no game envelope body");
                    }

                    return packet.Skip(15).ToArray();
                }
            }

            internal bool IsClientClosed(
                EnhancedClientSession session)
            {
                try
                {
                    return session?.TcpClient?.Client == null ||
                           session.TcpClient.Client.SafeHandle.IsClosed;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }

            internal void DisposeHandler()
            {
                Handler.Dispose();
            }

            internal void EndSession(EnhancedClientSession session)
            {
                _sessions.UnregisterAsync(
                        session.Player.CharacterId,
                        session)
                    .GetAwaiter()
                    .GetResult();
            }

            internal void FailTownFor(EnhancedClientSession session)
            {
                _townFailures.Add(session.SessionId);
            }

            internal void FailQueuedWhen(
                Func<EnhancedClientSession, byte[], bool> predicate)
            {
                _queuedFailure = predicate;
            }

            private Task SendQueuedAsync(
                EnhancedClientSession session,
                byte[] packet,
                CancellationToken cancellationToken)
            {
                if (_queuedFailure?.Invoke(session, packet) == true)
                {
                    QueuedFailureCount++;
                    throw new IOException("injected queued publication");
                }
                lock (Sends)
                {
                    Sends.Add(
                        new SentPacket(session.SessionId, packet));
                }
                return Task.CompletedTask;
            }

            private Task SendTownAsync(
                EnhancedClientSession session,
                byte[] packet,
                CancellationToken cancellationToken)
            {
                if (_townFailures.Contains(session.SessionId))
                    throw new IOException("injected Town publication");
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                Handler.Dispose();
                foreach (var client in _clients)
                    client.Dispose();
                DeleteDatabaseFiles(_databasePath);
            }

            private static void DeleteDatabaseFiles(string path)
            {
                foreach (var candidate in
                         new[] { path, path + "-wal", path + "-shm" })
                {
                    try
                    {
                        if (File.Exists(candidate))
                            File.Delete(candidate);
                    }
                    catch
                    {
                    }
                }
            }
        }

        internal sealed class SentPacket
        {
            internal SentPacket(Guid sessionId, byte[] packet)
            {
                SessionId = sessionId;
                Packet = packet;
            }

            internal Guid SessionId { get; }

            internal byte[] Packet { get; }
        }
    }
}
