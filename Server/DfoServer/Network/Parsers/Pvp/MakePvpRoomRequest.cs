using System;

namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// 2014 client payload for CMD 0x0032 MAKE_PVP_ROOM.
    ///
    /// u8 roomNameType
    /// if roomNameType == 0:
    ///   i32 roomNameByteLength (0..29)
    ///   u8[roomNameByteLength] roomName
    /// i16 mapIndex
    /// u8 hasPassword
    /// if hasPassword == 1:
    ///   i32 passwordByteLength (0..8)
    ///   u8[passwordByteLength] password
    /// u8 specialBattleMode (only 1 selects battle mode 6; otherwise mode 2)
    ///
    /// Strings are raw length-delimited bytes. They do not carry a trailing
    /// NUL byte on the wire.
    /// </summary>
    internal sealed class MakePvpRoomRequest
    {
        internal const int MaximumRoomNameBytes = 29;
        internal const int MaximumPasswordBytes = 8;

        private MakePvpRoomRequest(
            byte roomNameType,
            byte[] roomNameBytes,
            short mapIndex,
            bool hasPassword,
            byte[] passwordBytes,
            byte specialBattleModeRaw)
        {
            RoomNameType = roomNameType;
            RoomNameBytes =
                roomNameBytes == null
                    ? Array.Empty<byte>()
                    : (byte[])roomNameBytes.Clone();
            MapIndex = mapIndex;
            HasPassword = hasPassword;
            PasswordBytes =
                passwordBytes == null
                    ? Array.Empty<byte>()
                    : (byte[])passwordBytes.Clone();
            SpecialBattleModeRaw = specialBattleModeRaw;
        }

        internal byte RoomNameType { get; }

        internal byte[] RoomNameBytes { get; }

        internal short MapIndex { get; }

        internal bool HasPassword { get; }

        internal byte[] PasswordBytes { get; }

        internal byte SpecialBattleModeRaw { get; }

        internal byte BattleMode =>
            SpecialBattleModeRaw == 1
                ? (byte)6
                : (byte)2;

        internal static bool TryParse(
            byte[] body,
            out MakePvpRoomRequest request,
            out string error)
        {
            request = null;
            error = null;

            if (body == null)
            {
                error = "body is null";
                return false;
            }

            var offset = 0;
            if (!TryReadByte(body, ref offset, out var roomNameType))
            {
                error = "missing room-name type";
                return false;
            }

            // The legacy parser reads this field as a signed char and rejects
            // negative values.
            if (roomNameType > 0x7F)
            {
                error = "room-name type exceeds signed-byte range";
                return false;
            }

            var roomNameBytes = Array.Empty<byte>();
            if (roomNameType == 0)
            {
                if (!TryReadBoundedBytes(
                        body,
                        ref offset,
                        MaximumRoomNameBytes,
                        out roomNameBytes,
                        out error,
                        "room name"))
                {
                    return false;
                }
            }

            if (!TryReadInt16(body, ref offset, out var mapIndex))
            {
                error = "missing map index";
                return false;
            }

            if (!TryReadByte(body, ref offset, out var hasPasswordRaw))
            {
                error = "missing password flag";
                return false;
            }
            if (hasPasswordRaw > 1)
            {
                error = "password flag is not boolean";
                return false;
            }

            var hasPassword = hasPasswordRaw == 1;
            var passwordBytes = Array.Empty<byte>();
            if (hasPassword &&
                !TryReadBoundedBytes(
                    body,
                    ref offset,
                    MaximumPasswordBytes,
                    out passwordBytes,
                    out error,
                    "password"))
            {
                return false;
            }

            if (!TryReadByte(
                    body,
                    ref offset,
                    out var specialBattleModeRaw))
            {
                error = "missing battle-mode flag";
                return false;
            }

            if (offset != body.Length)
            {
                error =
                    $"unexpected trailing bytes: {body.Length - offset}";
                return false;
            }

            request = new MakePvpRoomRequest(
                roomNameType,
                roomNameBytes,
                mapIndex,
                hasPassword,
                passwordBytes,
                specialBattleModeRaw);
            return true;
        }

        private static bool TryReadBoundedBytes(
            byte[] body,
            ref int offset,
            int maximumLength,
            out byte[] value,
            out string error,
            string fieldName)
        {
            value = Array.Empty<byte>();
            error = null;

            if (!TryReadInt32(body, ref offset, out var length))
            {
                error = $"missing {fieldName} length";
                return false;
            }
            if (length < 0 || length > maximumLength)
            {
                error =
                    $"{fieldName} length {length} is outside 0..{maximumLength}";
                return false;
            }
            if (body.Length - offset < length)
            {
                error =
                    $"{fieldName} is truncated: need {length}, " +
                    $"have {body.Length - offset}";
                return false;
            }

            value = new byte[length];
            if (length > 0)
                Buffer.BlockCopy(body, offset, value, 0, length);
            offset += length;
            return true;
        }

        private static bool TryReadByte(
            byte[] body,
            ref int offset,
            out byte value)
        {
            value = 0;
            if (offset >= body.Length)
                return false;

            value = body[offset++];
            return true;
        }

        private static bool TryReadInt16(
            byte[] body,
            ref int offset,
            out short value)
        {
            value = 0;
            if (body.Length - offset < sizeof(short))
                return false;

            value = BitConverter.ToInt16(body, offset);
            offset += sizeof(short);
            return true;
        }

        private static bool TryReadInt32(
            byte[] body,
            ref int offset,
            out int value)
        {
            value = 0;
            if (body.Length - offset < sizeof(int))
                return false;

            value = BitConverter.ToInt32(body, offset);
            offset += sizeof(int);
            return true;
        }
    }
}
