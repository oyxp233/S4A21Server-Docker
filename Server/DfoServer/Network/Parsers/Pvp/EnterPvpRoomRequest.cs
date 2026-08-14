using System;

namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// 2014 client payload for CMD 0x0033 ENTER_PVP_ROOM.
    ///
    /// i16 roomId
    /// u8 hasPassword
    /// if hasPassword == 1:
    ///   i32 passwordByteLength (0..8)
    ///   u8[passwordByteLength] password
    /// </summary>
    internal sealed class EnterPvpRoomRequest
    {
        private EnterPvpRoomRequest(
            ushort roomId,
            bool hasPassword,
            byte[] passwordBytes)
        {
            RoomId = roomId;
            HasPassword = hasPassword;
            PasswordBytes =
                passwordBytes == null
                    ? Array.Empty<byte>()
                    : (byte[])passwordBytes.Clone();
        }

        internal ushort RoomId { get; }

        internal bool HasPassword { get; }

        internal byte[] PasswordBytes { get; }

        internal static bool TryParse(
            byte[] body,
            out EnterPvpRoomRequest request,
            out string error)
        {
            request = null;
            error = null;
            if (body == null || body.Length < 3)
            {
                error = "body is truncated";
                return false;
            }

            var offset = 0;
            var roomId = BitConverter.ToUInt16(body, offset);
            offset += sizeof(ushort);

            var hasPasswordRaw = body[offset++];
            if (hasPasswordRaw > 1)
            {
                error = "password flag is not boolean";
                return false;
            }

            var passwordBytes = Array.Empty<byte>();
            if (hasPasswordRaw == 1)
            {
                if (body.Length - offset < sizeof(int))
                {
                    error = "missing password length";
                    return false;
                }

                var passwordLength =
                    BitConverter.ToInt32(body, offset);
                offset += sizeof(int);
                if (passwordLength < 0 ||
                    passwordLength >
                    MakePvpRoomRequest.MaximumPasswordBytes)
                {
                    error =
                        $"password length {passwordLength} is outside " +
                        $"0..{MakePvpRoomRequest.MaximumPasswordBytes}";
                    return false;
                }
                if (body.Length - offset < passwordLength)
                {
                    error = "password is truncated";
                    return false;
                }

                passwordBytes = new byte[passwordLength];
                if (passwordLength > 0)
                {
                    Buffer.BlockCopy(
                        body,
                        offset,
                        passwordBytes,
                        0,
                        passwordLength);
                }
                offset += passwordLength;
            }

            if (offset != body.Length)
            {
                error =
                    $"unexpected trailing bytes: {body.Length - offset}";
                return false;
            }

            request = new EnterPvpRoomRequest(
                roomId,
                hasPasswordRaw == 1,
                passwordBytes);
            return true;
        }
    }
}
