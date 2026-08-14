using System;
using System.Collections.Generic;
using DfoServer.Game.Pvp;

namespace DfoServer.Network.Builders.Pvp
{
    internal static class PvpRoomNotificationBuilder
    {
        internal static byte[] BuildRoomInfoBody(
            IReadOnlyList<FreeDuelRoom> rooms)
        {
            var writer = new GamePacketWriter();
            var count = rooms?.Count ?? 0;
            if (count > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(rooms));

            writer.WriteUInt16((ushort)count);
            for (var i = 0; i < count; i++)
                WriteRoomInfo(writer, rooms[i]);
            return writer.ToArray();
        }

        internal static byte[] BuildDestroyedRoomStateBody(
            ushort roomId)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(roomId);
            writer.WriteByte(0); // room state after reset
            writer.WriteByte(0); // manager seat
            writer.WriteInt16(0); // map index
            writer.WriteByte(2); // normal PvP battle mode
            writer.WriteInt32(0); // normal matching type
            return writer.ToArray();
        }

        internal static byte[] BuildRoomStateBody(
            FreeDuelRoom room)
        {
            if (room == null)
                throw new ArgumentNullException(nameof(room));

            var writer = new GamePacketWriter();
            writer.WriteUInt16(room.RoomId);
            writer.WriteByte(room.RoomState);
            writer.WriteByte(room.ManagerSeat);
            writer.WriteInt16(room.MapIndex);
            writer.WriteByte(room.BattleMode);
            writer.WriteInt32(room.MatchingType);
            return writer.ToArray();
        }

        internal static byte[] BuildReadyStateBody(
            int seat,
            bool isReady)
        {
            if (seat < 0 || seat >= FreeDuelRoom.SeatCount)
                throw new ArgumentOutOfRangeException(nameof(seat));

            return new[]
            {
                (byte)seat,
                isReady
                    ? (byte)1
                    : (byte)0
            };
        }

        internal static byte[] BuildStartPvpBody(
            FreeDuelRoom room)
        {
            if (room == null)
                throw new ArgumentNullException(nameof(room));
            if (room.RoomState !=
                FreeDuelRoom.StartedRoomState)
            {
                throw new InvalidOperationException(
                    "PvP room has not started");
            }

            return new[]
            {
                room.SelectedMapIndex,
                room.BattleMode
            };
        }

        internal static byte[] BuildDeathBody(
            int deadSeat,
            int killerSeat)
        {
            if (deadSeat < 0 ||
                deadSeat >= FreeDuelRoom.SeatCount ||
                killerSeat < -1 ||
                killerSeat >= FreeDuelRoom.SeatCount)
            {
                throw new ArgumentOutOfRangeException();
            }

            return new[]
            {
                (byte)deadSeat,
                killerSeat >= 0
                    ? (byte)killerSeat
                    : byte.MaxValue
            };
        }

        internal static byte[] BuildRankRequestBody()
        {
            return Array.Empty<byte>();
        }

        internal static byte[] BuildEndPvpBody(
            FreeDuelRoom room,
            int recipientSeat)
        {
            if (room == null)
                throw new ArgumentNullException(nameof(room));
            if (recipientSeat < 0 ||
                recipientSeat >= FreeDuelRoom.SeatCount ||
                !room.IsOccupiedSeat(recipientSeat))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(recipientSeat));
            }
            if (room.SettlementPhase !=
                    FreeDuelRoom.AwaitingEndSettlementPhase)
            {
                throw new InvalidOperationException(
                    "PvP room is not ready for settlement output");
            }

            var combatantCount = 0;
            for (var seat = 0;
                 seat < FreeDuelRoom.SeatCount;
                 seat++)
            {
                if (room.IsOccupiedSeat(seat) &&
                    !room.IsObserverSeat(seat))
                {
                    combatantCount++;
                }
            }

            var writer = new GamePacketWriter();
            // The deployed A14 client interprets this byte as its own
            // win/lose/draw result. Sending the older native winner-seat value
            // makes seat 0 (red) appear correct by accident, while a blue win
            // is rendered as a draw/red win.
            writer.WriteByte(GetRecipientResultCode(room, recipientSeat));
            writer.WriteInt32(0); // free-duel win point
            writer.WriteByte(0); // free-duel PvP grade
            writer.WriteInt32(0); // free-duel experience delta
            writer.WriteByte((byte)combatantCount);
            for (var seat = 0;
                 seat < FreeDuelRoom.SeatCount;
                 seat++)
            {
                if (!room.IsOccupiedSeat(seat) ||
                    room.IsObserverSeat(seat))
                {
                    continue;
                }

                writer.WriteUInt16(
                    room.GetSeatUserId(seat));
                writer.WriteInt32(0); // client rank score is not trusted
                writer.WriteByte(0); // PvP grade
                writer.WriteInt32(0); // PvP experience
                writer.WriteInt32(0); // current rank point
                writer.WriteInt32(0); // next rank point
                writer.WriteInt32(room.GetKillCount(seat));
                writer.WriteInt32(room.GetDeathCount(seat));
            }
            if (combatantCount > 0)
                writer.WriteUInt16(0); // ace is intentionally unset
            writer.WriteUInt16(ushort.MaxValue); // no all-kill recipient
            writer.WriteInt32(0); // no reward experience
            writer.WriteByte(
                room.WinnerSeat == byte.MaxValue
                    ? (byte)1
                    : (byte)0);
            writer.WriteByte(0); // no within-mission result
            writer.WriteByte(byte.MaxValue); // not relay battle
            return writer.ToArray();
        }

        private static byte GetRecipientResultCode(
            FreeDuelRoom room,
            int recipientSeat)
        {
            const byte victory = 0;
            const byte defeat = 1;
            const byte draw = 2;

            if (room.IsObserverSeat(recipientSeat) ||
                room.WinnerSeat == byte.MaxValue)
            {
                return draw;
            }
            if (recipientSeat == room.WinnerSeat)
                return victory;

            if (room.BattleMode == 1 || room.BattleMode == 4)
                return defeat;
            if (!room.IsOccupiedSeat(room.WinnerSeat))
                return defeat;

            return room.GetSeatState(recipientSeat) ==
                   room.GetSeatState(room.WinnerSeat)
                ? victory
                : defeat;
        }

        internal static byte[] BuildEnterSuccessBody(
            FreeDuelRoom room)
        {
            if (room == null)
                throw new ArgumentNullException(nameof(room));

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            for (var seat = 0;
                 seat < FreeDuelRoom.SeatCount;
                 seat++)
            {
                writer.WriteByte(
                    room.GetReadyState(seat)
                        ? (byte)1
                        : (byte)0);
            }
            return writer.ToArray();
        }

        internal static byte[] BuildSeatStateBody(
            FreeDuelRoom room,
            int seat)
        {
            if (room == null)
                throw new ArgumentNullException(nameof(room));
            if (seat < 0 || seat >= FreeDuelRoom.SeatCount)
                throw new ArgumentOutOfRangeException(nameof(seat));

            var writer = new GamePacketWriter();
            writer.WriteUInt16(room.RoomId);
            writer.WriteByte(room.BattleMode);
            writer.WriteByte(1);
            WriteSeatState(writer, room, seat);
            return writer.ToArray();
        }

        internal static byte[] BuildOccupiedSeatStatesBody(
            FreeDuelRoom room)
        {
            if (room == null)
                throw new ArgumentNullException(nameof(room));

            var occupiedCount = 0;
            for (var seat = 0;
                 seat < FreeDuelRoom.SeatCount;
                 seat++)
            {
                if (room.IsOccupiedSeat(seat))
                    occupiedCount++;
            }

            var writer = new GamePacketWriter();
            writer.WriteUInt16(room.RoomId);
            writer.WriteByte(room.BattleMode);
            writer.WriteByte((byte)occupiedCount);
            for (var seat = 0;
                 seat < FreeDuelRoom.SeatCount;
                 seat++)
            {
                if (room.IsOccupiedSeat(seat))
                    WriteSeatState(writer, room, seat);
            }

            return writer.ToArray();
        }

        private static void WriteRoomInfo(
            GamePacketWriter writer,
            FreeDuelRoom room)
        {
            if (room == null)
                throw new ArgumentNullException(nameof(room));

            writer.WriteUInt16(room.RoomId);
            writer.WriteByte(room.RoomNameType);
            if (room.RoomNameType == 0)
            {
                writer.WriteInt32(room.RoomNameBytes.Length);
                writer.WriteBytes(room.RoomNameBytes);
            }

            writer.WriteByte(room.RoomState);
            writer.WriteByte(room.ManagerSeat);
            writer.WriteInt16(room.MapIndex);
            writer.WriteByte(room.BattleMode);

            for (var seat = 0;
                 seat < FreeDuelRoom.SeatCount;
                 seat++)
            {
                writer.WriteByte(room.GetSeatState(seat));
                writer.WriteUInt16(room.GetSeatUserId(seat));
                writer.WriteByte(0); // location/geo sharing disabled
            }

            // Native PvP_Room::make_room_info writes IsExistPassword here.
            writer.WriteByte(
                room.HasPassword
                    ? (byte)1
                    : (byte)0);
            writer.WriteInt32(room.MatchingType);
        }

        internal static byte[] BuildRelayTurnBody(
            FreeDuelRoom room)
        {
            if (room == null)
                throw new ArgumentNullException(nameof(room));
            if (room.BattleMode != 3 ||
                room.RoomState != FreeDuelRoom.StartedRoomState)
            {
                throw new InvalidOperationException(
                    "PvP room is not an active relay battle");
            }

            // Native CRelayBattleMgr::TurnPlayer:
            // team-count, then for teams 1 and 2: active seat followed by the
            // zero-length requested-substitution list.
            var writer = new GamePacketWriter();
            writer.WriteByte(2);
            for (byte team = 1; team <= 2; team++)
            {
                var selectedSeat = byte.MaxValue;
                for (var seat = 0;
                     seat < FreeDuelRoom.SeatCount;
                     seat++)
                {
                    if (room.IsOccupiedSeat(seat) &&
                        !room.IsObserverSeat(seat) &&
                        room.GetAliveState(seat) &&
                        room.GetSeatState(seat) == team)
                    {
                        selectedSeat = (byte)seat;
                        break;
                    }
                }

                writer.WriteByte(selectedSeat);
                writer.WriteByte(0);
            }

            return writer.ToArray();
        }

        internal static byte[] BuildRelayRequestFightBody(
            int seat)
        {
            if (seat < 0 || seat >= FreeDuelRoom.SeatCount)
                throw new ArgumentOutOfRangeException(nameof(seat));

            return new[] { (byte)seat };
        }

        private static void WriteSeatState(
            GamePacketWriter writer,
            FreeDuelRoom room,
            int seat)
        {
            writer.WriteByte((byte)seat);
            writer.WriteByte(room.GetSeatState(seat));
            writer.WriteUInt16(room.GetSeatUserId(seat));
            writer.WriteByte(0); // location/geo sharing disabled
        }
    }
}
