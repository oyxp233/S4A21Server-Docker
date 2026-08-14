using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Network.Parsers.Pvp;

namespace DfoServer.Game.Pvp
{
    internal enum FreeDuelRoomDepartureKind : byte
    {
        None = 0,
        MemberRemoved = 1,
        OwnerPromoted = 2,
        RoomRemoved = 3
    }

    internal readonly struct FreeDuelRoomDeparture
    {
        internal FreeDuelRoomDeparture(
            FreeDuelRoomDepartureKind kind,
            FreeDuelRoom room,
            byte vacatedSeat,
            bool wasActiveCombatant)
        {
            Kind = kind;
            Room = room;
            VacatedSeat = vacatedSeat;
            WasActiveCombatant = wasActiveCombatant;
        }

        internal FreeDuelRoomDepartureKind Kind { get; }

        internal FreeDuelRoom Room { get; }

        internal byte VacatedSeat { get; }

        internal bool WasActiveCombatant { get; }
    }

    internal sealed class FreeDuelRoomRegistry
    {
        internal const int MaximumRooms = 600;

        private readonly object _sync = new object();
        private readonly SortedSet<ushort> _freeRoomIds =
            new SortedSet<ushort>();
        private readonly Dictionary<ushort, FreeDuelRoom> _rooms =
            new Dictionary<ushort, FreeDuelRoom>();
        private readonly Dictionary<Guid, ushort> _roomByOwnerSession =
            new Dictionary<Guid, ushort>();
        private readonly Dictionary<Guid, ushort> _roomByMemberSession =
            new Dictionary<Guid, ushort>();
        private readonly Dictionary<ushort, Guid> _retiringRoomIds =
            new Dictionary<ushort, Guid>();

        internal FreeDuelRoomRegistry()
        {
            for (ushort roomId = 0;
                 roomId < MaximumRooms;
                 roomId++)
            {
                _freeRoomIds.Add(roomId);
            }
        }

        internal bool TryCreate(
            int listenerPort,
            int ownerCharacterId,
            Guid ownerSessionId,
            ushort ownerUserId,
            MakePvpRoomRequest request,
            out FreeDuelRoom room,
            out byte errorCode)
        {
            room = null;
            errorCode = 0;

            if (request == null ||
                listenerPort <= 0 ||
                ownerCharacterId <= 0 ||
                ownerSessionId == Guid.Empty ||
                ownerUserId == 0)
            {
                errorCode = 19;
                return false;
            }

            lock (_sync)
            {
                if (_roomByMemberSession.ContainsKey(ownerSessionId) ||
                    ContainsMemberIdentity(
                        ownerCharacterId,
                        ownerUserId))
                {
                    errorCode = 19;
                    return false;
                }
                if (_freeRoomIds.Count == 0)
                {
                    errorCode = 4;
                    return false;
                }

                var roomId = _freeRoomIds.Min;
                _freeRoomIds.Remove(roomId);
                room = new FreeDuelRoom(
                    roomId,
                    listenerPort,
                    ownerCharacterId,
                    ownerSessionId,
                    ownerUserId,
                    request.RoomNameType,
                    request.RoomNameBytes,
                    request.MapIndex,
                    request.HasPassword,
                    request.PasswordBytes,
                    request.BattleMode);
                _rooms.Add(roomId, room);
                _roomByOwnerSession.Add(ownerSessionId, roomId);
                _roomByMemberSession.Add(ownerSessionId, roomId);
                return true;
            }
        }

        internal bool TryJoin(
            int listenerPort,
            int characterId,
            Guid sessionId,
            ushort userId,
            EnterPvpRoomRequest request,
            out FreeDuelRoom room,
            out byte seat,
            out byte errorCode)
        {
            room = null;
            seat = byte.MaxValue;
            errorCode = 0;
            if (!AreValidJoinArguments(
                    listenerPort,
                    characterId,
                    sessionId,
                    userId,
                    request))
            {
                errorCode = 19;
                return false;
            }

            lock (_sync)
            {
                if (!TrySelectJoinSeat(
                        listenerPort,
                        characterId,
                        sessionId,
                        userId,
                        request,
                        out var current,
                        out var availableSeat,
                        out errorCode))
                {
                    return false;
                }

                var seatState =
                    SelectJoinSeatState(current);
                room = current.WithJoinedMember(
                    availableSeat,
                    characterId,
                    sessionId,
                    userId,
                    seatState);
                _rooms[current.RoomId] = room;
                _roomByMemberSession.Add(
                    sessionId,
                    current.RoomId);
                seat = (byte)availableSeat;
                return true;
            }
        }

        internal bool TryPrepareJoin(
            int listenerPort,
            int characterId,
            Guid sessionId,
            ushort userId,
            EnterPvpRoomRequest request,
            out FreeDuelRoom predictedRoom,
            out byte seat,
            out long baseRevision,
            out Guid ownerSessionId,
            out byte errorCode)
        {
            predictedRoom = null;
            seat = byte.MaxValue;
            baseRevision = -1;
            ownerSessionId = Guid.Empty;
            errorCode = 0;
            if (!AreValidJoinArguments(
                    listenerPort,
                    characterId,
                    sessionId,
                    userId,
                    request))
            {
                errorCode = 19;
                return false;
            }

            lock (_sync)
            {
                if (!TrySelectJoinSeat(
                        listenerPort,
                        characterId,
                        sessionId,
                        userId,
                        request,
                        out var current,
                        out var availableSeat,
                        out errorCode))
                {
                    return false;
                }

                predictedRoom = current.WithJoinedMember(
                    availableSeat,
                    characterId,
                    sessionId,
                    userId,
                    SelectJoinSeatState(current));
                seat = (byte)availableSeat;
                baseRevision = current.Revision;
                ownerSessionId = current.OwnerSessionId;
                return true;
            }
        }

        internal bool TryCommitPreparedJoin(
            int listenerPort,
            int characterId,
            Guid sessionId,
            ushort userId,
            EnterPvpRoomRequest request,
            long expectedBaseRevision,
            Guid expectedOwnerSessionId,
            byte expectedSeat,
            out FreeDuelRoom room,
            out byte errorCode)
        {
            room = null;
            errorCode = 0;
            if (!AreValidJoinArguments(
                    listenerPort,
                    characterId,
                    sessionId,
                    userId,
                    request) ||
                expectedBaseRevision < 0 ||
                expectedOwnerSessionId == Guid.Empty ||
                expectedSeat >= FreeDuelRoom.SeatCount)
            {
                errorCode = 19;
                return false;
            }

            lock (_sync)
            {
                if (_roomByMemberSession.ContainsKey(sessionId) ||
                    ContainsMemberIdentity(
                        characterId,
                        userId))
                {
                    errorCode = 19;
                    return false;
                }
                if (!_rooms.TryGetValue(
                        request.RoomId,
                        out var current) ||
                    current.ListenerPort != listenerPort ||
                    current.OwnerSessionId !=
                    expectedOwnerSessionId)
                {
                    errorCode = 22;
                    return false;
                }
                if (current.RoomState !=
                        FreeDuelRoom.WaitingRoomState ||
                    current.Revision != expectedBaseRevision)
                {
                    errorCode = 19;
                    return false;
                }
                if (!current.PasswordMatches(
                        request.HasPassword,
                        request.PasswordBytes))
                {
                    errorCode = 6;
                    return false;
                }
                if (current.IsOccupiedSeat(expectedSeat) ||
                    current.GetSeatState(expectedSeat) !=
                    FreeDuelRoom.EmptySeatState)
                {
                    errorCode = 4;
                    return false;
                }

                room = current.WithJoinedMember(
                    expectedSeat,
                    characterId,
                    sessionId,
                    userId,
                    SelectJoinSeatState(current));
                _rooms[current.RoomId] = room;
                _roomByMemberSession.Add(
                    sessionId,
                    current.RoomId);
                return true;
            }
        }

        internal bool TryRemoveNonOwnerMember(
            int characterId,
            Guid sessionId,
            out FreeDuelRoom room,
            out byte vacatedSeat,
            out byte errorCode)
        {
            room = null;
            vacatedSeat = byte.MaxValue;
            errorCode = 0;
            if (characterId <= 0 ||
                sessionId == Guid.Empty)
            {
                errorCode = 8;
                return false;
            }

            lock (_sync)
            {
                if (!_roomByMemberSession.TryGetValue(
                        sessionId,
                        out var roomId) ||
                    !_rooms.TryGetValue(roomId, out var current) ||
                    current.OwnerSessionId == sessionId ||
                    !current.TryGetSeatForSession(
                        sessionId,
                        out var seat) ||
                    current.GetSeatCharacterId(seat) != characterId)
                {
                    errorCode = 8;
                    return false;
                }

                room = current.WithoutMember(seat);
                _rooms[roomId] = room;
                _roomByMemberSession.Remove(sessionId);
                vacatedSeat = seat;
                return true;
            }
        }

        internal bool TryRemoveOwnerAndPromote(
            int ownerCharacterId,
            Guid ownerSessionId,
            out FreeDuelRoom room,
            out byte vacatedSeat,
            out byte errorCode)
        {
            room = null;
            vacatedSeat = byte.MaxValue;
            errorCode = 0;
            if (ownerCharacterId <= 0 ||
                ownerSessionId == Guid.Empty)
            {
                errorCode = 8;
                return false;
            }

            lock (_sync)
            {
                if (!_roomByOwnerSession.TryGetValue(
                        ownerSessionId,
                        out var roomId) ||
                    !_rooms.TryGetValue(roomId, out var current) ||
                    current.OwnerCharacterId != ownerCharacterId ||
                    current.OwnerSessionId != ownerSessionId)
                {
                    errorCode = 8;
                    return false;
                }
                if (!current.TryRemoveOwnerAndPromote(
                        out room,
                        out vacatedSeat))
                {
                    errorCode = 4;
                    return false;
                }
                if (_roomByOwnerSession.TryGetValue(
                        room.OwnerSessionId,
                        out var existingOwnerRoomId) &&
                    existingOwnerRoomId != roomId)
                {
                    room = null;
                    vacatedSeat = byte.MaxValue;
                    errorCode = 8;
                    return false;
                }

                _rooms[roomId] = room;
                _roomByOwnerSession.Remove(ownerSessionId);
                _roomByOwnerSession[room.OwnerSessionId] =
                    roomId;
                _roomByMemberSession.Remove(ownerSessionId);
                return true;
            }
        }

        internal bool TryRemoveEndingSession(
            int characterId,
            Guid sessionId,
            out FreeDuelRoomDeparture departure)
        {
            departure = default;
            if (characterId <= 0 || sessionId == Guid.Empty)
                return false;

            lock (_sync)
            {
                if (!_roomByMemberSession.TryGetValue(
                        sessionId,
                        out var roomId) ||
                    !_rooms.TryGetValue(roomId, out var current) ||
                    !current.TryGetSeatForSession(
                        sessionId,
                        out var seat) ||
                    current.GetSeatCharacterId(seat) != characterId)
                {
                    return false;
                }

                var wasActiveCombatant =
                    current.RoomState == FreeDuelRoom.StartedRoomState &&
                    current.SettlementPhase ==
                        FreeDuelRoom.CombatSettlementPhase &&
                    !current.IsObserverSeat(seat);

                if (current.OwnerSessionId != sessionId)
                {
                    var updated = current.WithoutMember(seat);
                    _rooms[roomId] = updated;
                    _roomByMemberSession.Remove(sessionId);
                    departure = new FreeDuelRoomDeparture(
                        FreeDuelRoomDepartureKind.MemberRemoved,
                        updated,
                        seat,
                        wasActiveCombatant);
                    return true;
                }

                if (current.TryRemoveOwnerAndPromote(
                        out var promoted,
                        out var vacatedSeat))
                {
                    if (_roomByOwnerSession.TryGetValue(
                            promoted.OwnerSessionId,
                            out var existingOwnerRoomId) &&
                        existingOwnerRoomId != roomId)
                    {
                        return false;
                    }

                    _rooms[roomId] = promoted;
                    _roomByOwnerSession.Remove(sessionId);
                    _roomByOwnerSession[promoted.OwnerSessionId] = roomId;
                    _roomByMemberSession.Remove(sessionId);
                    departure = new FreeDuelRoomDeparture(
                        FreeDuelRoomDepartureKind.OwnerPromoted,
                        promoted,
                        vacatedSeat,
                        wasActiveCombatant);
                    return true;
                }

                if (current.GetMemberSessionIds().Count(
                        memberSessionId =>
                            memberSessionId != Guid.Empty) != 1)
                {
                    return false;
                }

                _roomByOwnerSession.Remove(sessionId);
                _roomByMemberSession.Remove(sessionId);
                _rooms.Remove(roomId);
                _retiringRoomIds.Add(roomId, sessionId);
                departure = new FreeDuelRoomDeparture(
                    FreeDuelRoomDepartureKind.RoomRemoved,
                    current,
                    seat,
                    wasActiveCombatant);
                return true;
            }
        }

        internal bool TryRollbackJoinedMember(
            int characterId,
            Guid sessionId,
            out FreeDuelRoom room,
            out byte vacatedSeat)
        {
            return TryRemoveNonOwnerMember(
                characterId,
                sessionId,
                out room,
                out vacatedSeat,
                out _);
        }

        internal bool TryTakeOwnedRoomForRemoval(
            int ownerCharacterId,
            Guid ownerSessionId,
            out FreeDuelRoom room)
        {
            room = null;
            if (ownerCharacterId <= 0 ||
                ownerSessionId == Guid.Empty)
            {
                return false;
            }

            lock (_sync)
            {
                if (!_roomByOwnerSession.TryGetValue(
                        ownerSessionId,
                        out var roomId) ||
                    !_rooms.TryGetValue(roomId, out var current) ||
                    current.OwnerCharacterId != ownerCharacterId ||
                    current.OwnerSessionId != ownerSessionId)
                {
                    return false;
                }

                _roomByOwnerSession.Remove(ownerSessionId);
                foreach (var memberSessionId in
                         current.GetMemberSessionIds())
                {
                    if (memberSessionId != Guid.Empty)
                    {
                        _roomByMemberSession.Remove(
                            memberSessionId);
                    }
                }
                _rooms.Remove(roomId);
                _retiringRoomIds.Add(
                    roomId,
                    current.OwnerSessionId);
                room = current;
                return true;
            }
        }

        internal bool TryTakeSoleOwnedRoomForRemoval(
            int ownerCharacterId,
            Guid ownerSessionId,
            out FreeDuelRoom room)
        {
            room = null;
            if (ownerCharacterId <= 0 ||
                ownerSessionId == Guid.Empty)
            {
                return false;
            }

            lock (_sync)
            {
                if (!_roomByOwnerSession.TryGetValue(
                        ownerSessionId,
                        out var roomId) ||
                    !_rooms.TryGetValue(roomId, out var current) ||
                    current.OwnerCharacterId != ownerCharacterId ||
                    current.OwnerSessionId != ownerSessionId)
                {
                    return false;
                }

                var occupiedSessionIds =
                    current.GetMemberSessionIds()
                        .Where(
                            sessionId =>
                                sessionId != Guid.Empty)
                        .ToArray();
                if (occupiedSessionIds.Length != 1 ||
                    occupiedSessionIds[0] != ownerSessionId)
                {
                    return false;
                }

                _roomByOwnerSession.Remove(ownerSessionId);
                _roomByMemberSession.Remove(ownerSessionId);
                _rooms.Remove(roomId);
                _retiringRoomIds.Add(
                    roomId,
                    current.OwnerSessionId);
                room = current;
                return true;
            }
        }

        internal bool ReleaseRemovedRoomId(FreeDuelRoom room)
        {
            if (room == null)
                return false;

            lock (_sync)
            {
                if (!_retiringRoomIds.TryGetValue(
                        room.RoomId,
                        out var ownerSessionId) ||
                    ownerSessionId != room.OwnerSessionId)
                {
                    return false;
                }

                _retiringRoomIds.Remove(room.RoomId);
                _freeRoomIds.Add(room.RoomId);
                return true;
            }
        }

        internal bool TrySetSeatState(
            int actorCharacterId,
            Guid actorSessionId,
            byte seat,
            byte seatState,
            out FreeDuelRoom room,
            out byte errorCode)
        {
            room = null;
            errorCode = 0;
            if (actorCharacterId <= 0 ||
                actorSessionId == Guid.Empty ||
                seat >= FreeDuelRoom.SeatCount ||
                !FreeDuelRoom.IsSupportedSeatState(seatState))
            {
                errorCode = 8;
                return false;
            }

            lock (_sync)
            {
                if (!_roomByMemberSession.TryGetValue(
                        actorSessionId,
                        out var roomId) ||
                    !_rooms.TryGetValue(roomId, out var current) ||
                    !current.TryGetSeatForSession(
                        actorSessionId,
                        out var actorSeat) ||
                    current.GetSeatCharacterId(actorSeat) !=
                        actorCharacterId)
                {
                    errorCode = 8;
                    return false;
                }
                if (current.RoomState !=
                    FreeDuelRoom.WaitingRoomState)
                {
                    errorCode = 19;
                    return false;
                }
                if (seatState == FreeDuelRoom.ClosedSeatState)
                {
                    if (current.OwnerSessionId !=
                        actorSessionId)
                    {
                        errorCode = 8;
                        return false;
                    }

                    // Occupied-seat walkout is handled by the exact member
                    // departure path. Never let a slot toggle rewrite a
                    // different member's state.
                    if (current.IsOccupiedSeat(seat))
                    {
                        errorCode = 8;
                        return false;
                    }

                    // Legacy PvP_Room::is_closeable_seat_state keeps one
                    // joinable slot only while there is a single active
                    // (non-observer) player. With two active players every
                    // remaining empty slot may be closed.
                    if (current.NonObserverPlayerCount == 1 &&
                        current.OpenSeatCount <= 1)
                    {
                        errorCode = 19;
                        return false;
                    }
                }
                else if (seatState == FreeDuelRoom.EmptySeatState)
                {
                    if (current.OwnerSessionId !=
                            actorSessionId ||
                        current.IsOccupiedSeat(seat))
                    {
                        errorCode = 8;
                        return false;
                    }
                }
                else if (!current.IsOccupiedSeat(seat) ||
                         current.GetSeatSessionId(seat) !=
                            actorSessionId ||
                         actorSeat != seat)
                {
                    // Native check_authority permits the manager to operate
                    // room controls, but non-special team/observer states are
                    // only mutable by the exact occupant of that wire seat.
                    errorCode = 8;
                    return false;
                }

                if (current.GetSeatState(seat) == seatState)
                {
                    room = current;
                    return true;
                }

                room = current.WithSeatState(
                    seat,
                    seatState);
                _rooms[current.RoomId] = room;
                return true;
            }
        }

        internal bool TrySetBattleMode(
            int ownerCharacterId,
            Guid ownerSessionId,
            byte battleMode,
            out FreeDuelRoom room,
            out byte errorCode)
        {
            room = null;
            errorCode = 0;
            if (ownerCharacterId <= 0 ||
                ownerSessionId == Guid.Empty ||
                battleMode < 1 ||
                battleMode > 6)
            {
                errorCode = 8;
                return false;
            }

            lock (_sync)
            {
                if (!TryGetOwnedRoom(
                        ownerCharacterId,
                        ownerSessionId,
                        out var current))
                {
                    errorCode = 8;
                    return false;
                }
                if (current.RoomState !=
                    FreeDuelRoom.WaitingRoomState)
                {
                    errorCode = 19;
                    return false;
                }
                room = current.WithBattleMode(battleMode);
                _rooms[current.RoomId] = room;
                return true;
            }
        }

        internal bool TrySetReadyState(
            int characterId,
            Guid sessionId,
            bool isReady,
            out FreeDuelRoom room,
            out byte seat,
            out bool started,
            out byte errorCode)
        {
            room = null;
            seat = byte.MaxValue;
            started = false;
            errorCode = 0;
            if (characterId <= 0 ||
                sessionId == Guid.Empty)
            {
                errorCode = 19;
                return false;
            }

            lock (_sync)
            {
                if (!_roomByMemberSession.TryGetValue(
                        sessionId,
                        out var roomId) ||
                    !_rooms.TryGetValue(roomId, out var current) ||
                    !current.TryGetSeatForSession(
                        sessionId,
                        out seat) ||
                    current.GetSeatCharacterId(seat) != characterId ||
                    current.RoomState !=
                        FreeDuelRoom.WaitingRoomState)
                {
                    errorCode = 19;
                    return false;
                }

                // The legacy practice-room mode has its own start path.
                // Keep it fail-closed until that protocol is implemented.
                if (current.BattleMode == 6)
                {
                    errorCode = 19;
                    return false;
                }

                if (sessionId != current.OwnerSessionId)
                {
                    room = current.WithReadyState(
                        seat,
                        isReady);
                    _rooms[roomId] = room;
                    return true;
                }

                if (!isReady)
                {
                    room = current;
                    return true;
                }

                var candidate =
                    current.WithReadyState(
                        seat,
                        true);
                if (!AreAllNonObserverPlayersReady(candidate))
                {
                    if (current.GetReadyState(seat))
                    {
                        _rooms[roomId] =
                            current.WithReadyState(
                                seat,
                                false);
                    }
                    errorCode = 22;
                    return false;
                }
                if (!HasBalancedTeams(candidate))
                {
                    // Native PvP_Room keeps the manager's internal ready bit
                    // set on team-balance error 14, but does not publish it.
                    _rooms[roomId] = candidate;
                    errorCode = 14;
                    return false;
                }

                if (CountOccupiedMembers(candidate) <= 1)
                {
                    // Native start_pvp is a void no-op for a singleton after
                    // the manager-ready publication; the room remains in
                    // waiting state and its state snapshot is rebroadcast.
                    room = candidate;
                    _rooms[roomId] = room;
                    return true;
                }

                room = candidate.CreateStartedSnapshot(
                    SelectStartMap(candidate));
                _rooms[roomId] = room;
                started = true;
                return true;
            }
        }

        internal bool TryReportDeath(
            int characterId,
            Guid sessionId,
            ushort reportedDeadUserId,
            out FreeDuelRoom room,
            out byte deadSeat,
            out byte killerSeat,
            out bool terminal)
        {
            room = null;
            deadSeat = byte.MaxValue;
            killerSeat = byte.MaxValue;
            terminal = false;
            if (characterId <= 0 ||
                sessionId == Guid.Empty)
            {
                return false;
            }

            lock (_sync)
            {
                if (!_roomByMemberSession.TryGetValue(
                        sessionId,
                        out var roomId) ||
                    !_rooms.TryGetValue(roomId, out var current) ||
                    !current.TryGetSeatForSession(
                        sessionId,
                        out deadSeat) ||
                    current.GetSeatCharacterId(deadSeat) !=
                        characterId ||
                    current.IsObserverSeat(deadSeat) ||
                    current.GetSeatUserId(deadSeat) !=
                        reportedDeadUserId)
                {
                    return false;
                }

                // DIE_PVP_CHARACTER identifies the reporting victim.  Do not
                // reinterpret that value as a killer-controlled identity.
                // A killer can be credited only when exactly one live
                // opposing seat exists; otherwise keep attribution unknown
                // while still allowing authoritative death/win settlement.
                var killerIndex = -1;
                for (var seat = 0;
                     seat < FreeDuelRoom.SeatCount;
                     seat++)
                {
                    if (seat == deadSeat ||
                        !current.IsOccupiedSeat(seat) ||
                        current.IsObserverSeat(seat) ||
                        !current.GetAliveState(seat) ||
                        current.BattleMode != 1 &&
                        current.BattleMode != 4 &&
                        current.GetSeatState(seat) ==
                            current.GetSeatState(deadSeat))
                    {
                        continue;
                    }

                    if (killerIndex >= 0)
                    {
                        killerIndex = -1;
                        break;
                    }
                    killerIndex = seat;
                }

                if (!current.TryCreateDeathSnapshot(
                        deadSeat,
                        killerIndex,
                        out room,
                        out terminal))
                {
                    return false;
                }

                killerSeat =
                    killerIndex >= 0
                        ? (byte)killerIndex
                        : byte.MaxValue;
                _rooms[roomId] = room;
                return true;
            }
        }

        internal bool TryAcknowledgeRank(
            int characterId,
            Guid sessionId,
            out FreeDuelRoom room,
            out bool completed)
        {
            return TryAcknowledgeSettlement(
                characterId,
                sessionId,
                rank: true,
                out room,
                out completed);
        }

        internal bool TryAcknowledgeEnd(
            int characterId,
            Guid sessionId,
            out FreeDuelRoom room,
            out bool completed)
        {
            if (!TryAcknowledgeSettlement(
                    characterId,
                    sessionId,
                    rank: false,
                    out room,
                    out completed))
            {
                return false;
            }

            if (completed)
            {
                lock (_sync)
                {
                    if (_rooms.TryGetValue(
                            room.RoomId,
                            out var current) &&
                        current.GenerationId ==
                        room.GenerationId &&
                        current.Revision == room.Revision &&
                        current.SettlementPhase ==
                        FreeDuelRoom.AwaitingEndSettlementPhase)
                    {
                        room =
                            current
                                .CreateWaitingAfterSettlementSnapshot();
                        _rooms[room.RoomId] = room;
                    }
                    else
                    {
                        completed = false;
                        return false;
                    }
                }
            }

            return true;
        }

        internal bool TryForceRankSettlement(
            ushort roomId,
            Guid generationId,
            long matchGeneration,
            out FreeDuelRoom room)
        {
            room = null;
            lock (_sync)
            {
                if (!_rooms.TryGetValue(roomId, out var current) ||
                    current.GenerationId != generationId ||
                    current.MatchGeneration != matchGeneration ||
                    current.SettlementPhase !=
                    FreeDuelRoom.AwaitingRankSettlementPhase)
                {
                    return false;
                }

                room = current.CreateAwaitingEndSnapshot();
                _rooms[roomId] = room;
                return true;
            }
        }

        internal bool TryForceEndSettlement(
            ushort roomId,
            Guid generationId,
            long matchGeneration,
            out FreeDuelRoom room)
        {
            room = null;
            lock (_sync)
            {
                if (!_rooms.TryGetValue(roomId, out var current) ||
                    current.GenerationId != generationId ||
                    current.MatchGeneration != matchGeneration ||
                    current.SettlementPhase !=
                    FreeDuelRoom.AwaitingEndSettlementPhase)
                {
                    return false;
                }

                room =
                    current.CreateWaitingAfterSettlementSnapshot();
                _rooms[roomId] = room;
                return true;
            }
        }

        internal bool TryForceCombatAbandonment(
            ushort roomId,
            Guid generationId,
            long matchGeneration,
            out FreeDuelRoom room)
        {
            room = null;
            lock (_sync)
            {
                if (!_rooms.TryGetValue(roomId, out var current) ||
                    current.GenerationId != generationId ||
                    current.MatchGeneration != matchGeneration ||
                    current.RoomState !=
                    FreeDuelRoom.StartedRoomState ||
                    current.SettlementPhase !=
                    FreeDuelRoom.CombatSettlementPhase)
                {
                    return false;
                }

                room =
                    current.CreateWaitingAfterSettlementSnapshot();
                _rooms[roomId] = room;
                return true;
            }
        }

        internal bool TrySettleCombatAfterDisconnect(
            ushort roomId,
            Guid generationId,
            long matchGeneration,
            out FreeDuelRoom room)
        {
            room = null;
            lock (_sync)
            {
                if (!_rooms.TryGetValue(roomId, out var current) ||
                    current.GenerationId != generationId ||
                    current.MatchGeneration != matchGeneration ||
                    !current.TryCreateDisconnectSettlementSnapshot(
                        out room))
                {
                    return false;
                }

                _rooms[roomId] = room;
                return true;
            }
        }

        private bool TryAcknowledgeSettlement(
            int characterId,
            Guid sessionId,
            bool rank,
            out FreeDuelRoom room,
            out bool completed)
        {
            room = null;
            completed = false;
            if (characterId <= 0 ||
                sessionId == Guid.Empty)
            {
                return false;
            }

            lock (_sync)
            {
                if (!_roomByMemberSession.TryGetValue(
                        sessionId,
                        out var roomId) ||
                    !_rooms.TryGetValue(roomId, out var current) ||
                    !current.TryGetSeatForSession(
                        sessionId,
                        out var seat) ||
                    current.GetSeatCharacterId(seat) != characterId)
                {
                    return false;
                }

                var accepted =
                    rank
                        ? current.TryCreateRankAcknowledgedSnapshot(
                            seat,
                            out room,
                            out completed)
                        : current.TryCreateEndAcknowledgedSnapshot(
                            seat,
                            out room,
                            out completed);
                if (!accepted)
                    return false;

                _rooms[roomId] = room;
                return true;
            }
        }

        internal IReadOnlyList<FreeDuelRoom> SnapshotForListener(
            int listenerPort)
        {
            var result = new List<FreeDuelRoom>();
            lock (_sync)
            {
                foreach (var room in _rooms.Values)
                {
                    if (room.ListenerPort == listenerPort)
                        result.Add(room);
                }
            }
            result.Sort(
                (left, right) =>
                    left.RoomId.CompareTo(right.RoomId));
            return result;
        }

        internal bool TryGetRoomForMember(
            int characterId,
            Guid sessionId,
            out FreeDuelRoom room,
            out byte seat)
        {
            room = null;
            seat = byte.MaxValue;
            if (characterId <= 0 ||
                sessionId == Guid.Empty)
            {
                return false;
            }

            lock (_sync)
            {
                return _roomByMemberSession.TryGetValue(
                           sessionId,
                           out var roomId)
                       && _rooms.TryGetValue(
                           roomId,
                           out room)
                       && room.TryGetSeatForSession(
                           sessionId,
                           out seat)
                       && room.GetSeatCharacterId(seat) ==
                          characterId;
            }
        }

        private bool TryGetOwnedRoom(
            int ownerCharacterId,
            Guid ownerSessionId,
            out FreeDuelRoom room)
        {
            room = null;
            return _roomByOwnerSession.TryGetValue(
                       ownerSessionId,
                       out var roomId)
                   && _rooms.TryGetValue(roomId, out room)
                   && room.OwnerCharacterId == ownerCharacterId
                   && room.OwnerSessionId == ownerSessionId;
        }

        private static byte SelectJoinSeatState(
            FreeDuelRoom room)
        {
            if (room.BattleMode == 1 ||
                room.BattleMode == 4)
            {
                return 0;
            }
            if (room.BattleMode == 5)
                return 1;

            var teamOne = 0;
            var teamTwo = 0;
            for (var seat = 0;
                 seat < FreeDuelRoom.SeatCount;
                 seat++)
            {
                if (!room.IsOccupiedSeat(seat))
                    continue;

                if (room.GetSeatState(seat) == 1)
                    teamOne++;
                else if (room.GetSeatState(seat) == 2)
                    teamTwo++;
            }

            // The original loop uses <= while walking team 1 then team 2,
            // so equal populations select team 2.
            return teamTwo <= teamOne
                ? (byte)2
                : (byte)1;
        }

        private static bool AreAllNonObserverPlayersReady(
            FreeDuelRoom room)
        {
            for (var seat = 0;
                 seat < FreeDuelRoom.SeatCount;
                 seat++)
            {
                if (!room.IsOccupiedSeat(seat) ||
                    room.IsObserverSeat(seat))
                {
                    continue;
                }

                if (!room.GetReadyState(seat))
                    return false;
            }

            return true;
        }

        private static int CountOccupiedMembers(
            FreeDuelRoom room)
        {
            var count = 0;
            for (var seat = 0;
                 seat < FreeDuelRoom.SeatCount;
                 seat++)
            {
                if (room.IsOccupiedSeat(seat))
                    count++;
            }

            return count;
        }

        private static bool HasBalancedTeams(
            FreeDuelRoom room)
        {
            if (room.BattleMode != 2 &&
                room.BattleMode != 3 &&
                room.BattleMode != 5)
            {
                return true;
            }

            var teamOne = 0;
            var teamTwo = 0;
            for (var seat = 0;
                 seat < FreeDuelRoom.SeatCount;
                 seat++)
            {
                if (!room.IsOccupiedSeat(seat) ||
                    room.IsObserverSeat(seat))
                {
                    continue;
                }

                if (room.GetSeatState(seat) == 1)
                    teamOne++;
                else if (room.GetSeatState(seat) == 2)
                    teamTwo++;
            }

            return teamOne == teamTwo;
        }

        private static byte SelectStartMap(
            FreeDuelRoom room)
        {
            // PvP map indices are one-based in the legacy candidate list.
            // A MAKE value of zero means "random"; use the first normal-map
            // candidate deterministically until map-rotation state exists.
            return room.MapIndex > 0 &&
                   room.MapIndex <= byte.MaxValue
                ? (byte)room.MapIndex
                : (byte)1;
        }

        private static bool AreValidJoinArguments(
            int listenerPort,
            int characterId,
            Guid sessionId,
            ushort userId,
            EnterPvpRoomRequest request)
        {
            return request != null
                   && listenerPort > 0
                   && characterId > 0
                   && sessionId != Guid.Empty
                   && userId != 0;
        }

        private bool TrySelectJoinSeat(
            int listenerPort,
            int characterId,
            Guid sessionId,
            ushort userId,
            EnterPvpRoomRequest request,
            out FreeDuelRoom room,
            out int seat,
            out byte errorCode)
        {
            room = null;
            seat = -1;
            errorCode = 0;
            if (_roomByMemberSession.ContainsKey(sessionId) ||
                ContainsMemberIdentity(
                    characterId,
                    userId))
            {
                errorCode = 19;
                return false;
            }
            if (!_rooms.TryGetValue(
                    request.RoomId,
                    out room) ||
                room.ListenerPort != listenerPort)
            {
                room = null;
                errorCode = 22;
                return false;
            }
            if (room.RoomState !=
                FreeDuelRoom.WaitingRoomState)
            {
                errorCode = 19;
                return false;
            }
            if (!room.PasswordMatches(
                    request.HasPassword,
                    request.PasswordBytes))
            {
                errorCode = 6;
                return false;
            }

            for (var index = 0;
                 index < FreeDuelRoom.SeatCount;
                 index++)
            {
                if (!room.IsOccupiedSeat(index) &&
                    room.GetSeatState(index) ==
                    FreeDuelRoom.EmptySeatState)
                {
                    seat = index;
                    return true;
                }
            }

            errorCode = 4;
            return false;
        }

        private bool ContainsMemberIdentity(
            int characterId,
            ushort userId)
        {
            foreach (var existing in _rooms.Values)
            {
                for (var seat = 0;
                     seat < FreeDuelRoom.SeatCount;
                     seat++)
                {
                    if (!existing.IsOccupiedSeat(seat))
                        continue;
                    if (existing.GetSeatCharacterId(seat) ==
                            characterId ||
                        existing.GetSeatUserId(seat) == userId)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
