using System;

namespace DfoServer.Network.Parsers.Pvp
{
    internal static class PvpTotalMatchTeamRequest
    {
        // A21 545D80/545800: Dstr, followed by exactly three roster indices for SET.
        internal static bool TryParse(byte[] body, bool hasMembers, out byte[] name, out byte[] slots)
        {
            name = null;
            slots = null;
            if (body == null || body.Length < 4)
                return false;
            var length = BitConverter.ToInt32(body, 0);
            if (length < 1 || length > 24 || body.Length != 4 + length + (hasMembers ? 3 : 0))
                return false;
            name = new byte[length];
            Buffer.BlockCopy(body, 4, name, 0, length);
            slots = hasMembers ? body.AsSpan(4 + length, 3).ToArray() : Array.Empty<byte>();
            return true;
        }
    }
}
