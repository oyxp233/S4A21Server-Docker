using System;

namespace DfoServer.Game.Pvp
{
    internal sealed class FreeDuelRoom
    {
        internal const int SeatCount = 8;
        internal const byte WaitingRoomState = 1;
        internal const byte StartedRoomState = 2;
        internal const byte OccupiedSeatState = 1;
        internal const byte ClosedSeatState = 0xFE;
        internal const byte EmptySeatState = 0xFF;
        internal const byte MinimumPlayerSeatState = 0;
        internal const byte ObserverSeatState = 3;
        internal const byte AlternateObserverSeatState = 4;
        internal const byte MaximumPlayerSeatState = AlternateObserverSeatState;
        internal const byte WaitingSettlementPhase = 0;
        internal const byte CombatSettlementPhase = 1;
        internal const byte AwaitingRankSettlementPhase = 2;
        internal const byte AwaitingEndSettlementPhase = 3;

        private readonly byte[] _roomNameBytes;
        private readonly byte[] _passwordBytes;
        private readonly byte[] _seatStates;
        private readonly Guid[] _seatSessionIds;
        private readonly int[] _seatCharacterIds;
        private readonly ushort[] _seatUserIds;
        private readonly bool[] _readyStates;
        private readonly bool[] _aliveStates;
        private readonly bool[] _rankAcknowledgements;
        private readonly bool[] _endAcknowledgements;
        private readonly int[] _killCounts;
        private readonly int[] _deathCounts;
        private readonly byte _roomState;
        private readonly byte _selectedMapIndex;
        private readonly byte _settlementPhase;
        private readonly byte _winnerSeat;

        internal FreeDuelRoom(
            ushort roomId,
            int listenerPort,
            int ownerCharacterId,
            Guid ownerSessionId,
            ushort ownerUserId,
            byte roomNameType,
            byte[] roomNameBytes,
            short mapIndex,
            bool hasPassword,
            byte[] passwordBytes,
            byte battleMode,
            byte[] seatStates = null,
            long revision = 0,
            Guid[] seatSessionIds = null,
            int[] seatCharacterIds = null,
            ushort[] seatUserIds = null,
            bool[] readyStates = null,
            byte roomState = WaitingRoomState,
            byte selectedMapIndex = 0,
            byte managerSeat = 0,
            Guid? generationId = null,
            bool[] aliveStates = null,
            bool[] rankAcknowledgements = null,
            bool[] endAcknowledgements = null,
            int[] killCounts = null,
            int[] deathCounts = null,
            byte settlementPhase = WaitingSettlementPhase,
            byte winnerSeat = byte.MaxValue,
            long matchGeneration = 0)
        {
            if (roomState != WaitingRoomState &&
                roomState != StartedRoomState)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(roomState));
            }
            if (managerSeat >= SeatCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(managerSeat));
            }
            if (settlementPhase > AwaitingEndSettlementPhase)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(settlementPhase));
            }
            if (winnerSeat != byte.MaxValue &&
                winnerSeat >= SeatCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(winnerSeat));
            }
            if (matchGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(matchGeneration));
            }

            RoomId = roomId;
            ListenerPort = listenerPort;
            OwnerCharacterId = ownerCharacterId;
            OwnerSessionId = ownerSessionId;
            OwnerUserId = ownerUserId;
            GenerationId =
                generationId.HasValue &&
                generationId.Value != Guid.Empty
                    ? generationId.Value
                    : Guid.NewGuid();
            ManagerSeat = managerSeat;
            RoomNameType = roomNameType;
            _roomNameBytes =
                roomNameBytes == null
                    ? Array.Empty<byte>()
                    : (byte[])roomNameBytes.Clone();
            MapIndex = mapIndex;
            HasPassword = hasPassword;
            _passwordBytes =
                passwordBytes == null
                    ? Array.Empty<byte>()
                    : (byte[])passwordBytes.Clone();
            BattleMode = battleMode;
            Revision = revision;
            _roomState = roomState;
            _selectedMapIndex = selectedMapIndex;
            _settlementPhase = settlementPhase;
            _winnerSeat = winnerSeat;
            MatchGeneration = matchGeneration;

            if (seatStates == null)
            {
                _seatStates = new byte[SeatCount];
                for (var seat = 0; seat < SeatCount; seat++)
                    _seatStates[seat] = EmptySeatState;
                _seatStates[ManagerSeat] = OccupiedSeatState;
            }
            else
            {
                if (seatStates.Length != SeatCount)
                {
                    throw new ArgumentException(
                        $"PvP room requires exactly {SeatCount} seats.",
                        nameof(seatStates));
                }

                _seatStates = (byte[])seatStates.Clone();
            }

            if (seatSessionIds == null &&
                seatCharacterIds == null &&
                seatUserIds == null)
            {
                _seatSessionIds = new Guid[SeatCount];
                _seatCharacterIds = new int[SeatCount];
                _seatUserIds = new ushort[SeatCount];
                _seatSessionIds[ManagerSeat] = ownerSessionId;
                _seatCharacterIds[ManagerSeat] = ownerCharacterId;
                _seatUserIds[ManagerSeat] = ownerUserId;
            }
            else
            {
                if (seatSessionIds == null ||
                    seatCharacterIds == null ||
                    seatUserIds == null ||
                    seatSessionIds.Length != SeatCount ||
                    seatCharacterIds.Length != SeatCount ||
                    seatUserIds.Length != SeatCount)
                {
                    throw new ArgumentException(
                        $"PvP room member arrays require exactly " +
                        $"{SeatCount} entries.");
                }

                _seatSessionIds =
                    (Guid[])seatSessionIds.Clone();
                _seatCharacterIds =
                    (int[])seatCharacterIds.Clone();
                _seatUserIds =
                    (ushort[])seatUserIds.Clone();
            }

            if (readyStates == null)
            {
                _readyStates = new bool[SeatCount];
            }
            else
            {
                if (readyStates.Length != SeatCount)
                {
                    throw new ArgumentException(
                        $"PvP room readiness requires exactly " +
                        $"{SeatCount} entries.",
                        nameof(readyStates));
                }

                _readyStates =
                    (bool[])readyStates.Clone();
            }

            _aliveStates = CloneSeatArray(
                aliveStates,
                nameof(aliveStates));
            _rankAcknowledgements = CloneSeatArray(
                rankAcknowledgements,
                nameof(rankAcknowledgements));
            _endAcknowledgements = CloneSeatArray(
                endAcknowledgements,
                nameof(endAcknowledgements));
            _killCounts = CloneSeatArray(
                killCounts,
                nameof(killCounts));
            _deathCounts = CloneSeatArray(
                deathCounts,
                nameof(deathCounts));
        }

        internal ushort RoomId { get; }

        internal int ListenerPort { get; }

        internal int OwnerCharacterId { get; }

        internal Guid OwnerSessionId { get; }

        internal ushort OwnerUserId { get; }

        internal Guid GenerationId { get; }

        internal byte RoomNameType { get; }

        internal byte[] RoomNameBytes =>
            (byte[])_roomNameBytes.Clone();

        internal short MapIndex { get; }

        internal bool HasPassword { get; }

        internal byte[] PasswordBytes =>
            (byte[])_passwordBytes.Clone();

        internal byte BattleMode { get; }

        internal long Revision { get; }

        internal byte RoomState => _roomState;

        internal byte SelectedMapIndex => _selectedMapIndex;

        internal byte ManagerSeat { get; }

        internal byte SettlementPhase => _settlementPhase;

        internal byte WinnerSeat => _winnerSeat;

        internal long MatchGeneration { get; }

        internal int MatchingType => 0;

        internal byte GetSeatState(int seat)
        {
            if (seat < 0 || seat >= SeatCount)
                throw new ArgumentOutOfRangeException(nameof(seat));

            return _seatStates[seat];
        }

        internal bool IsOccupiedSeat(int seat)
        {
            if (seat < 0 || seat >= SeatCount)
                throw new ArgumentOutOfRangeException(nameof(seat));

            return _seatSessionIds[seat] != Guid.Empty;
        }

        internal bool IsObserverSeat(int seat)
        {
            return IsOccupiedSeat(seat) &&
                   (_seatStates[seat] == ObserverSeatState ||
                    _seatStates[seat] == AlternateObserverSeatState);
        }

        internal bool GetReadyState(int seat)
        {
            if (seat < 0 || seat >= SeatCount)
                throw new ArgumentOutOfRangeException(nameof(seat));

            return _readyStates[seat];
        }

        internal bool GetAliveState(int seat)
        {
            ValidateSeat(seat);
            return _aliveStates[seat];
        }

        internal int GetKillCount(int seat)
        {
            ValidateSeat(seat);
            return _killCounts[seat];
        }

        internal int GetDeathCount(int seat)
        {
            ValidateSeat(seat);
            return _deathCounts[seat];
        }

        internal ushort GetSeatUserId(int seat)
        {
            return IsOccupiedSeat(seat)
                ? _seatUserIds[seat]
                : ushort.MaxValue;
        }

        internal Guid GetSeatSessionId(int seat)
        {
            if (seat < 0 || seat >= SeatCount)
                throw new ArgumentOutOfRangeException(nameof(seat));

            return _seatSessionIds[seat];
        }

        internal int GetSeatCharacterId(int seat)
        {
            if (seat < 0 || seat >= SeatCount)
                throw new ArgumentOutOfRangeException(nameof(seat));

            return _seatCharacterIds[seat];
        }

        internal bool TryGetSeatForSession(
            Guid sessionId,
            out byte seat)
        {
            seat = 0;
            if (sessionId == Guid.Empty)
                return false;

            for (var index = 0; index < SeatCount; index++)
            {
                if (_seatSessionIds[index] == sessionId)
                {
                    seat = (byte)index;
                    return true;
                }
            }

            return false;
        }

        internal Guid[] GetMemberSessionIds()
        {
            var result = new Guid[SeatCount];
            Array.Copy(
                _seatSessionIds,
                result,
                SeatCount);
            return result;
        }

        internal bool PasswordMatches(
            bool suppliedPassword,
            byte[] suppliedPasswordBytes)
        {
            // The password-present bit is part of the authenticated room
            // entry shape. A password-bearing request must not be accepted by
            // a public room, and a private room requires the exact bytes.
            if (HasPassword != suppliedPassword)
                return false;

            var candidate =
                suppliedPasswordBytes ?? Array.Empty<byte>();
            if (candidate.Length != _passwordBytes.Length)
                return false;

            var mismatch = 0;
            for (var index = 0;
                 index < _passwordBytes.Length;
                 index++)
            {
                mismatch |=
                    _passwordBytes[index] ^ candidate[index];
            }
            return mismatch == 0;
        }

        internal int OpenSeatCount
        {
            get
            {
                var count = 0;
                for (var seat = 0; seat < SeatCount; seat++)
                {
                    if (_seatStates[seat] == EmptySeatState)
                        count++;
                }

                return count;
            }
        }

        internal int NonObserverPlayerCount
        {
            get
            {
                var count = 0;
                for (var seat = 0; seat < SeatCount; seat++)
                {
                    if (IsOccupiedSeat(seat) &&
                        !IsObserverSeat(seat))
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        internal FreeDuelRoom WithSeatState(
            int seat,
            byte seatState)
        {
            if (seat < 0 || seat >= SeatCount)
                throw new ArgumentOutOfRangeException(nameof(seat));

            var states = (byte[])_seatStates.Clone();
            states[seat] = seatState;
            return Copy(
                BattleMode,
                states,
                _seatSessionIds,
                _seatCharacterIds,
                _seatUserIds);
        }

        internal FreeDuelRoom WithJoinedMember(
            int seat,
            int characterId,
            Guid sessionId,
            ushort userId,
            byte seatState)
        {
            if (seat < 0 ||
                seat >= SeatCount ||
                characterId <= 0 ||
                sessionId == Guid.Empty ||
                userId == 0 ||
                _seatStates[seat] != EmptySeatState ||
                IsOccupiedSeat(seat))
            {
                throw new ArgumentException(
                    "invalid PvP room join snapshot");
            }

            var states = (byte[])_seatStates.Clone();
            var sessions = (Guid[])_seatSessionIds.Clone();
            var characters = (int[])_seatCharacterIds.Clone();
            var users = (ushort[])_seatUserIds.Clone();
            var readyStates = (bool[])_readyStates.Clone();
            states[seat] = seatState;
            sessions[seat] = sessionId;
            characters[seat] = characterId;
            users[seat] = userId;
            readyStates[seat] = false;
            var aliveStates = (bool[])_aliveStates.Clone();
            var rankAcknowledgements =
                (bool[])_rankAcknowledgements.Clone();
            var endAcknowledgements =
                (bool[])_endAcknowledgements.Clone();
            var killCounts = (int[])_killCounts.Clone();
            var deathCounts = (int[])_deathCounts.Clone();
            aliveStates[seat] = false;
            rankAcknowledgements[seat] = false;
            endAcknowledgements[seat] = false;
            killCounts[seat] = 0;
            deathCounts[seat] = 0;
            return Copy(
                BattleMode,
                states,
                sessions,
                characters,
                users,
                readyStates,
                aliveStates: aliveStates,
                rankAcknowledgements: rankAcknowledgements,
                endAcknowledgements: endAcknowledgements,
                killCounts: killCounts,
                deathCounts: deathCounts);
        }

        internal FreeDuelRoom WithoutMember(int seat)
        {
            if (seat < 0 ||
                seat >= SeatCount ||
                !IsOccupiedSeat(seat) ||
                seat == ManagerSeat)
            {
                throw new ArgumentException(
                    "seat does not contain a removable non-manager member",
                    nameof(seat));
            }

            var states = (byte[])_seatStates.Clone();
            var sessions = (Guid[])_seatSessionIds.Clone();
            var characters = (int[])_seatCharacterIds.Clone();
            var users = (ushort[])_seatUserIds.Clone();
            var readyStates = (bool[])_readyStates.Clone();
            states[seat] = EmptySeatState;
            sessions[seat] = Guid.Empty;
            characters[seat] = 0;
            users[seat] = 0;
            readyStates[seat] = false;
            var aliveStates = (bool[])_aliveStates.Clone();
            var rankAcknowledgements =
                (bool[])_rankAcknowledgements.Clone();
            var endAcknowledgements =
                (bool[])_endAcknowledgements.Clone();
            var killCounts = (int[])_killCounts.Clone();
            var deathCounts = (int[])_deathCounts.Clone();
            aliveStates[seat] = false;
            rankAcknowledgements[seat] = false;
            endAcknowledgements[seat] = false;
            killCounts[seat] = 0;
            deathCounts[seat] = 0;
            return Copy(
                BattleMode,
                states,
                sessions,
                characters,
                users,
                readyStates,
                aliveStates: aliveStates,
                rankAcknowledgements: rankAcknowledgements,
                endAcknowledgements: endAcknowledgements,
                killCounts: killCounts,
                deathCounts: deathCounts);
        }

        internal bool TryRemoveOwnerAndPromote(
            out FreeDuelRoom room,
            out byte vacatedSeat)
        {
            room = null;
            vacatedSeat = byte.MaxValue;
            if (!IsOccupiedSeat(ManagerSeat) ||
                _seatSessionIds[ManagerSeat] != OwnerSessionId ||
                _seatCharacterIds[ManagerSeat] != OwnerCharacterId ||
                _seatUserIds[ManagerSeat] != OwnerUserId)
            {
                return false;
            }

            var successorSeat = -1;
            for (var seat = 0; seat < SeatCount; seat++)
            {
                if (seat != ManagerSeat &&
                    IsOccupiedSeat(seat))
                {
                    successorSeat = seat;
                    break;
                }
            }
            if (successorSeat < 0)
                return false;

            var states = (byte[])_seatStates.Clone();
            var sessions = (Guid[])_seatSessionIds.Clone();
            var characters = (int[])_seatCharacterIds.Clone();
            var users = (ushort[])_seatUserIds.Clone();
            var readyStates = (bool[])_readyStates.Clone();
            vacatedSeat = ManagerSeat;
            states[vacatedSeat] = EmptySeatState;
            sessions[vacatedSeat] = Guid.Empty;
            characters[vacatedSeat] = 0;
            users[vacatedSeat] = 0;
            readyStates[vacatedSeat] = false;

            // Native PvP_Room::select_new_manager applies in both waiting and
            // started rooms: choose the first occupied seat and clear that
            // member's ready bit before removing the old manager.
            readyStates[successorSeat] = false;
            var aliveStates = (bool[])_aliveStates.Clone();
            var rankAcknowledgements =
                (bool[])_rankAcknowledgements.Clone();
            var endAcknowledgements =
                (bool[])_endAcknowledgements.Clone();
            var killCounts = (int[])_killCounts.Clone();
            var deathCounts = (int[])_deathCounts.Clone();
            aliveStates[vacatedSeat] = false;
            rankAcknowledgements[vacatedSeat] = false;
            endAcknowledgements[vacatedSeat] = false;
            killCounts[vacatedSeat] = 0;
            deathCounts[vacatedSeat] = 0;
            room = Copy(
                BattleMode,
                states,
                sessions,
                characters,
                users,
                readyStates,
                ownerCharacterId: characters[successorSeat],
                ownerSessionId: sessions[successorSeat],
                ownerUserId: users[successorSeat],
                managerSeat: (byte)successorSeat,
                aliveStates: aliveStates,
                rankAcknowledgements: rankAcknowledgements,
                endAcknowledgements: endAcknowledgements,
                killCounts: killCounts,
                deathCounts: deathCounts);
            return true;
        }

        internal FreeDuelRoom WithBattleMode(byte battleMode)
        {
            var states = (byte[])_seatStates.Clone();

            // This mirrors the 2014 PvP_Room::set_pvp_mode transition in
            // occupied-seat order, while preserving closed and empty slots.
            if (battleMode == 1 || battleMode == 4)
            {
                for (var seat = 0; seat < SeatCount; seat++)
                {
                    if (IsOccupiedSeat(seat))
                        states[seat] = 0;
                }
            }
            else
            {
                var shouldAlternate =
                    battleMode == 5
                    || battleMode == 3 && BattleMode == 1
                    || battleMode != 3 && BattleMode != 3;
                if (shouldAlternate)
                {
                    byte nextTeam = 1;
                    for (var seat = 0;
                         seat < SeatCount;
                         seat++)
                    {
                        if (!IsOccupiedSeat(seat))
                            continue;

                        states[seat] = nextTeam;
                        nextTeam =
                            nextTeam == 1
                                ? (byte)2
                                : (byte)1;
                    }
                }
            }

            return Copy(
                battleMode,
                states,
                _seatSessionIds,
                _seatCharacterIds,
                _seatUserIds);
        }

        internal FreeDuelRoom CreateResetSnapshot()
        {
            var states = new byte[SeatCount];
            for (var seat = 0; seat < SeatCount; seat++)
                states[seat] = EmptySeatState;

            return Copy(
                battleMode: 2,
                states,
                new Guid[SeatCount],
                new int[SeatCount],
                new ushort[SeatCount],
                new bool[SeatCount],
                WaitingRoomState,
                selectedMapIndex: 0);
        }

        internal FreeDuelRoom WithReadyState(
            int seat,
            bool isReady)
        {
            if (seat < 0 ||
                seat >= SeatCount ||
                !IsOccupiedSeat(seat))
            {
                throw new ArgumentException(
                    "seat does not contain a PvP room member",
                    nameof(seat));
            }

            var readyStates = (bool[])_readyStates.Clone();
            readyStates[seat] = isReady;
            return Copy(
                BattleMode,
                _seatStates,
                _seatSessionIds,
                _seatCharacterIds,
                _seatUserIds,
                readyStates);
        }

        internal FreeDuelRoom CreateStartedSnapshot(
            byte selectedMapIndex)
        {
            if (_roomState != WaitingRoomState)
            {
                throw new InvalidOperationException(
                    "PvP room is not waiting to start");
            }

            var aliveStates = new bool[SeatCount];
            for (var seat = 0; seat < SeatCount; seat++)
            {
                aliveStates[seat] =
                    IsOccupiedSeat(seat) &&
                    !IsObserverSeat(seat);
            }

            return Copy(
                BattleMode,
                _seatStates,
                _seatSessionIds,
                _seatCharacterIds,
                _seatUserIds,
                _readyStates,
                StartedRoomState,
                selectedMapIndex,
                aliveStates: aliveStates,
                rankAcknowledgements: new bool[SeatCount],
                endAcknowledgements: new bool[SeatCount],
                killCounts: new int[SeatCount],
                deathCounts: new int[SeatCount],
                settlementPhase: CombatSettlementPhase,
                winnerSeat: byte.MaxValue,
                matchGeneration:
                    checked(MatchGeneration + 1));
        }

        internal bool TryCreateDeathSnapshot(
            int deadSeat,
            int killerSeat,
            out FreeDuelRoom room,
            out bool terminal)
        {
            room = null;
            terminal = false;
            if (_roomState != StartedRoomState ||
                _settlementPhase != CombatSettlementPhase ||
                deadSeat < 0 ||
                deadSeat >= SeatCount ||
                !_aliveStates[deadSeat] ||
                IsObserverSeat(deadSeat) ||
                killerSeat == deadSeat ||
                killerSeat < -1 ||
                killerSeat >= SeatCount ||
                killerSeat >= 0 &&
                (!_aliveStates[killerSeat] ||
                 IsObserverSeat(killerSeat)))
            {
                return false;
            }

            var aliveStates = (bool[])_aliveStates.Clone();
            var killCounts = (int[])_killCounts.Clone();
            var deathCounts = (int[])_deathCounts.Clone();
            aliveStates[deadSeat] = false;
            deathCounts[deadSeat] =
                checked(deathCounts[deadSeat] + 1);
            if (killerSeat >= 0)
            {
                killCounts[killerSeat] =
                    checked(killCounts[killerSeat] + 1);
            }

            var winnerSeat =
                FindTerminalWinnerSeat(aliveStates);
            terminal = winnerSeat != -2;
            room = Copy(
                BattleMode,
                _seatStates,
                _seatSessionIds,
                _seatCharacterIds,
                _seatUserIds,
                aliveStates: aliveStates,
                killCounts: killCounts,
                deathCounts: deathCounts,
                settlementPhase:
                    terminal
                        ? AwaitingRankSettlementPhase
                        : CombatSettlementPhase,
                winnerSeat:
                    winnerSeat >= 0
                        ? (byte)winnerSeat
                        : byte.MaxValue);
            return true;
        }

        internal bool TryCreateDisconnectSettlementSnapshot(
            out FreeDuelRoom room)
        {
            room = null;
            if (_roomState != StartedRoomState ||
                _settlementPhase != CombatSettlementPhase)
            {
                return false;
            }

            var winnerSeat = FindTerminalWinnerSeat(_aliveStates);
            if (winnerSeat == -2)
                return false;

            room = Copy(
                BattleMode,
                _seatStates,
                _seatSessionIds,
                _seatCharacterIds,
                _seatUserIds,
                settlementPhase: AwaitingRankSettlementPhase,
                winnerSeat:
                    winnerSeat >= 0
                        ? (byte)winnerSeat
                        : byte.MaxValue);
            return true;
        }

        internal bool TryCreateRankAcknowledgedSnapshot(
            int seat,
            out FreeDuelRoom room,
            out bool completed)
        {
            return TryCreateAcknowledgedSnapshot(
                seat,
                AwaitingRankSettlementPhase,
                _rankAcknowledgements,
                out room,
                out completed);
        }

        internal bool TryCreateEndAcknowledgedSnapshot(
            int seat,
            out FreeDuelRoom room,
            out bool completed)
        {
            return TryCreateAcknowledgedSnapshot(
                seat,
                AwaitingEndSettlementPhase,
                _endAcknowledgements,
                out room,
                out completed);
        }

        internal FreeDuelRoom CreateAwaitingEndSnapshot()
        {
            if (_roomState != StartedRoomState ||
                _settlementPhase != AwaitingRankSettlementPhase)
            {
                throw new InvalidOperationException(
                    "PvP room is not awaiting rank acknowledgement");
            }

            return Copy(
                BattleMode,
                _seatStates,
                _seatSessionIds,
                _seatCharacterIds,
                _seatUserIds,
                settlementPhase: AwaitingEndSettlementPhase);
        }

        internal FreeDuelRoom CreateWaitingAfterSettlementSnapshot()
        {
            if (_roomState != StartedRoomState)
            {
                throw new InvalidOperationException(
                    "PvP room is not started");
            }

            return Copy(
                BattleMode,
                _seatStates,
                _seatSessionIds,
                _seatCharacterIds,
                _seatUserIds,
                new bool[SeatCount],
                WaitingRoomState,
                selectedMapIndex: 0,
                aliveStates: new bool[SeatCount],
                rankAcknowledgements: new bool[SeatCount],
                endAcknowledgements: new bool[SeatCount],
                killCounts: new int[SeatCount],
                deathCounts: new int[SeatCount],
                settlementPhase: WaitingSettlementPhase,
                winnerSeat: byte.MaxValue);
        }

        internal static bool IsSupportedSeatState(byte seatState)
        {
            return seatState >= MinimumPlayerSeatState
                       && seatState <= MaximumPlayerSeatState
                   || seatState == ClosedSeatState
                   || seatState == EmptySeatState;
        }

        private FreeDuelRoom Copy(
            byte battleMode,
            byte[] seatStates,
            Guid[] seatSessionIds,
            int[] seatCharacterIds,
            ushort[] seatUserIds,
            bool[] readyStates = null,
            byte? roomState = null,
            byte? selectedMapIndex = null,
            int? ownerCharacterId = null,
            Guid? ownerSessionId = null,
            ushort? ownerUserId = null,
            byte? managerSeat = null,
            bool[] aliveStates = null,
            bool[] rankAcknowledgements = null,
            bool[] endAcknowledgements = null,
            int[] killCounts = null,
            int[] deathCounts = null,
            byte? settlementPhase = null,
            byte? winnerSeat = null,
            long? matchGeneration = null)
        {
            return new FreeDuelRoom(
                RoomId,
                ListenerPort,
                ownerCharacterId ?? OwnerCharacterId,
                ownerSessionId ?? OwnerSessionId,
                ownerUserId ?? OwnerUserId,
                RoomNameType,
                _roomNameBytes,
                MapIndex,
                HasPassword,
                _passwordBytes,
                battleMode,
                seatStates,
                checked(Revision + 1),
                seatSessionIds,
                seatCharacterIds,
                seatUserIds,
                readyStates ?? _readyStates,
                roomState ?? _roomState,
                selectedMapIndex ?? _selectedMapIndex,
                managerSeat ?? ManagerSeat,
                GenerationId,
                aliveStates ?? _aliveStates,
                rankAcknowledgements ?? _rankAcknowledgements,
                endAcknowledgements ?? _endAcknowledgements,
                killCounts ?? _killCounts,
                deathCounts ?? _deathCounts,
                settlementPhase ?? _settlementPhase,
                winnerSeat ?? _winnerSeat,
                matchGeneration ?? MatchGeneration);
        }

        private bool TryCreateAcknowledgedSnapshot(
            int seat,
            byte expectedPhase,
            bool[] acknowledgements,
            out FreeDuelRoom room,
            out bool completed)
        {
            room = null;
            completed = false;
            if (_roomState != StartedRoomState ||
                _settlementPhase != expectedPhase ||
                seat < 0 ||
                seat >= SeatCount ||
                !IsOccupiedSeat(seat) ||
                IsObserverSeat(seat) ||
                acknowledgements[seat])
            {
                return false;
            }

            var next = (bool[])acknowledgements.Clone();
            next[seat] = true;
            completed = AllCombatantsAcknowledged(next);
            room = Copy(
                BattleMode,
                _seatStates,
                _seatSessionIds,
                _seatCharacterIds,
                _seatUserIds,
                rankAcknowledgements:
                    expectedPhase == AwaitingRankSettlementPhase
                        ? next
                        : null,
                endAcknowledgements:
                    expectedPhase == AwaitingEndSettlementPhase
                        ? next
                        : null,
                settlementPhase:
                    completed &&
                    expectedPhase == AwaitingRankSettlementPhase
                        ? AwaitingEndSettlementPhase
                        : expectedPhase);
            return true;
        }

        private bool AllCombatantsAcknowledged(
            bool[] acknowledgements)
        {
            for (var seat = 0; seat < SeatCount; seat++)
            {
                if (IsOccupiedSeat(seat) &&
                    !IsObserverSeat(seat) &&
                    !acknowledgements[seat])
                {
                    return false;
                }
            }

            return true;
        }

        // -2 means combat continues, -1 means a terminal draw, otherwise the
        // value is the first seat on the surviving side.
        private int FindTerminalWinnerSeat(bool[] aliveStates)
        {
            var firstAliveSeat = -1;
            var firstAliveTeam = -1;
            var aliveCount = 0;
            for (var seat = 0; seat < SeatCount; seat++)
            {
                if (!aliveStates[seat] ||
                    !IsOccupiedSeat(seat) ||
                    IsObserverSeat(seat))
                {
                    continue;
                }

                aliveCount++;
                if (firstAliveSeat < 0)
                {
                    firstAliveSeat = seat;
                    firstAliveTeam = _seatStates[seat];
                    continue;
                }

                if (BattleMode == 1 || BattleMode == 4)
                    return -2;
                if (_seatStates[seat] != firstAliveTeam)
                    return -2;
            }

            if (aliveCount == 0)
                return -1;
            return firstAliveSeat;
        }

        private static bool[] CloneSeatArray(
            bool[] value,
            string paramName)
        {
            if (value == null)
                return new bool[SeatCount];
            if (value.Length != SeatCount)
                throw new ArgumentException(
                    $"PvP room array requires exactly {SeatCount} entries.",
                    paramName);
            return (bool[])value.Clone();
        }

        private static int[] CloneSeatArray(
            int[] value,
            string paramName)
        {
            if (value == null)
                return new int[SeatCount];
            if (value.Length != SeatCount)
                throw new ArgumentException(
                    $"PvP room array requires exactly {SeatCount} entries.",
                    paramName);
            return (int[])value.Clone();
        }

        private static void ValidateSeat(int seat)
        {
            if (seat < 0 || seat >= SeatCount)
                throw new ArgumentOutOfRangeException(nameof(seat));
        }
    }
}
