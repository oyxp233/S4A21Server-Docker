using System;
using System.Buffers.Binary;
using System.Linq;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Parsers.Friends
{
    internal static class UnitedFriendRequest
    {
        // ADD: target UID, server byte, then GBK DSTR.
        internal static bool TryAdd(byte[] body, out string name)
        {
            name = null;
            return body != null && body.Length >= 7 && body[2] == 1
                && TryName(body.AsSpan(3), out name);
        }

        // DELETE: server byte, then GBK DSTR.
        internal static bool TryDelete(byte[] body, out string name)
        {
            name = null;
            return body != null && body.Length >= 5 && body[0] == 1
                && TryName(body.AsSpan(1), out name);
        }

        // REQUEST_USER_CHANNEL shares the server/DSTR layout; its ACK DSTR reader requires < 50 GBK bytes.
        internal static bool TryUserChannel(byte[] body, out string name)
            => TryDelete(body, out name) && body.Length < 55;

        private static bool TryName(ReadOnlySpan<byte> body, out string name)
        {
            name = null;
            if (body.Length < 4) return false;
            int length = BinaryPrimitives.ReadInt32LittleEndian(body);
            // The ordinary friend list accepts 1..255 GBK bytes per name.
            return length > 0 && length < 256 && length == body.Length - 4
                && ClientTextEncoding.TryGetStringStrict(body[4..].ToArray(), out name)
                && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);
        }
    }
}
