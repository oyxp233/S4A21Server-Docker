using System;
using DfoServer.Game.Pvp;
using DfoServer.Network;
using DfoServer.Network.Parsers.Pvp;

namespace DfoServer.SelfTests
{
    public static class FreeDuelRoomCoreSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            var parsedMake = MakePvpRoomRequest.TryParse(
                new byte[] { 0x06, 0x00, 0x00, 0x00, 0x00 },
                out var makeRequest,
                out _);
            var registry = new FreeDuelRoomRegistry();
            var ownerSession = Guid.NewGuid();
            FreeDuelRoom room = null;
            byte createError = byte.MaxValue;
            var created = parsedMake &&
                registry.TryCreate(
                    GameNetworkConfig.FreeDuelGamePort,
                    5001,
                    ownerSession,
                    5001,
                    makeRequest,
                    out room,
                    out createError);
            Check(
                "public room request creates the first legacy room id",
                created && createError == 0 && room.RoomId == 0,
                ref failures);

            var parsedEnter = EnterPvpRoomRequest.TryParse(
                new byte[] { 0x00, 0x00, 0x00 },
                out var enterRequest,
                out _);
            var memberSession = Guid.NewGuid();
            byte memberSeat = byte.MaxValue;
            byte joinError = byte.MaxValue;
            var joined = parsedEnter &&
                registry.TryJoin(
                    GameNetworkConfig.FreeDuelGamePort,
                    5002,
                    memberSession,
                    5002,
                    enterRequest,
                    out room,
                    out memberSeat,
                    out joinError);
            Check(
                "second player joins a deterministic open seat",
                joined && joinError == 0 && memberSeat == 1 &&
                room.NonObserverPlayerCount == 2,
                ref failures);

            var observerChanged = registry.TrySetSeatState(
                5002,
                memberSession,
                memberSeat,
                FreeDuelRoom.AlternateObserverSeatState,
                out room,
                out var observerError);
            Check(
                "alternate observer state is preserved and excluded from combat count",
                observerChanged && observerError == 0 &&
                room.IsObserverSeat(memberSeat) &&
                room.GetSeatState(memberSeat) ==
                    FreeDuelRoom.AlternateObserverSeatState &&
                room.NonObserverPlayerCount == 1,
                ref failures);

            var ownerChangedMode = registry.TrySetBattleMode(
                5001,
                ownerSession,
                6,
                out room,
                out var modeError);
            var memberCannotChangeMode = !registry.TrySetBattleMode(
                5002,
                memberSession,
                2,
                out _,
                out var memberModeError);
            Check(
                "only the exact owner session can change battle mode",
                ownerChangedMode && modeError == 0 && room.BattleMode == 6 &&
                memberCannotChangeMode && memberModeError == 8,
                ref failures);

            var removed = registry.TryTakeOwnedRoomForRemoval(
                5001,
                ownerSession,
                out var retired);
            var released = removed && registry.ReleaseRemovedRoomId(retired);
            FreeDuelRoom recycledRoom = null;
            var recycled = released &&
                registry.TryCreate(
                    GameNetworkConfig.FreeDuelGamePort,
                    5010,
                    Guid.NewGuid(),
                    5010,
                    makeRequest,
                    out recycledRoom,
                    out _);
            Check(
                "retired room ids are recycled only after explicit release",
                recycled && recycledRoom.RoomId == 0,
                ref failures);

            Check(
                "session ending promotes one exact owner generation atomically",
                VerifyOwnerPromotion(makeRequest, enterRequest),
                ref failures);
            Check(
                "combatant disconnect produces one terminal settlement",
                VerifyCombatDisconnectSettlement(
                    makeRequest,
                    enterRequest,
                    out var settlementCompleted),
                ref failures);
            Check(
                "remaining combatant acknowledgements reset the same match",
                settlementCompleted,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "FreeDuelRoomCoreSelfTest OK"
                    : $"FreeDuelRoomCoreSelfTest FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }

        private static bool VerifyOwnerPromotion(
            MakePvpRoomRequest makeRequest,
            EnterPvpRoomRequest enterRequest)
        {
            var registry = new FreeDuelRoomRegistry();
            var ownerSession = Guid.NewGuid();
            var memberSession = Guid.NewGuid();
            if (!registry.TryCreate(
                    GameNetworkConfig.FreeDuelGamePort,
                    5101,
                    ownerSession,
                    5101,
                    makeRequest,
                    out var room,
                    out _) ||
                !registry.TryJoin(
                    GameNetworkConfig.FreeDuelGamePort,
                    5102,
                    memberSession,
                    5102,
                    enterRequest,
                    out room,
                    out var memberSeat,
                    out _) ||
                !registry.TryRemoveEndingSession(
                    5101,
                    ownerSession,
                    out var promoted) ||
                promoted.Kind !=
                    FreeDuelRoomDepartureKind.OwnerPromoted ||
                promoted.VacatedSeat != 0 ||
                promoted.Room.OwnerSessionId != memberSession ||
                promoted.Room.ManagerSeat != memberSeat)
            {
                return false;
            }

            var staleOwnerRejected =
                !registry.TrySetBattleMode(
                    5101,
                    ownerSession,
                    5,
                    out _,
                    out var staleError) &&
                staleError == 8;
            var promotedOwnerAccepted =
                registry.TrySetBattleMode(
                    5102,
                    memberSession,
                    5,
                    out room,
                    out var promotedError) &&
                promotedError == 0 &&
                room.BattleMode == 5;
            var removed =
                registry.TryRemoveEndingSession(
                    5102,
                    memberSession,
                    out var retired) &&
                retired.Kind ==
                    FreeDuelRoomDepartureKind.RoomRemoved &&
                registry.ReleaseRemovedRoomId(retired.Room);
            return staleOwnerRejected && promotedOwnerAccepted && removed;
        }

        private static bool VerifyCombatDisconnectSettlement(
            MakePvpRoomRequest makeRequest,
            EnterPvpRoomRequest enterRequest,
            out bool settlementCompleted)
        {
            settlementCompleted = false;
            var registry = new FreeDuelRoomRegistry();
            var ownerSession = Guid.NewGuid();
            var memberSession = Guid.NewGuid();
            if (!registry.TryCreate(
                    GameNetworkConfig.FreeDuelGamePort,
                    5201,
                    ownerSession,
                    5201,
                    makeRequest,
                    out var room,
                    out _) ||
                !registry.TryJoin(
                    GameNetworkConfig.FreeDuelGamePort,
                    5202,
                    memberSession,
                    5202,
                    enterRequest,
                    out room,
                    out _,
                    out _) ||
                !registry.TrySetReadyState(
                    5202,
                    memberSession,
                    true,
                    out room,
                    out _,
                    out _,
                    out _) ||
                !registry.TrySetReadyState(
                    5201,
                    ownerSession,
                    true,
                    out room,
                    out _,
                    out var started,
                    out _) ||
                !started)
            {
                return false;
            }

            var matchGeneration = room.MatchGeneration;
            if (!registry.TryRemoveEndingSession(
                    5202,
                    memberSession,
                    out var departure) ||
                departure.Kind !=
                    FreeDuelRoomDepartureKind.MemberRemoved ||
                !departure.WasActiveCombatant ||
                !registry.TrySettleCombatAfterDisconnect(
                    departure.Room.RoomId,
                    departure.Room.GenerationId,
                    matchGeneration,
                    out var terminal) ||
                terminal.WinnerSeat != terminal.ManagerSeat ||
                terminal.SettlementPhase !=
                    FreeDuelRoom.AwaitingRankSettlementPhase)
            {
                return false;
            }

            settlementCompleted =
                registry.TryAcknowledgeRank(
                    5201,
                    ownerSession,
                    out room,
                    out var rankCompleted) &&
                rankCompleted &&
                registry.TryAcknowledgeEnd(
                    5201,
                    ownerSession,
                    out room,
                    out var endCompleted) &&
                endCompleted &&
                room.RoomState == FreeDuelRoom.WaitingRoomState &&
                room.SettlementPhase ==
                    FreeDuelRoom.WaitingSettlementPhase &&
                !registry.TryForceEndSettlement(
                    room.RoomId,
                    room.GenerationId,
                    matchGeneration,
                    out _);
            return true;
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {label}");
            if (!condition)
                failures++;
        }
    }
}
