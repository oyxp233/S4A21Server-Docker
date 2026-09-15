using System;

namespace DfoServer.Network.Parsers.Pvp
{
    internal readonly struct IntegrateMatchPvpScoreRequest
    {
        private IntegrateMatchPvpScoreRequest(ushort targetUserId)
        {
            TargetUserId = targetUserId;
        }

        internal ushort TargetUserId { get; }

        internal static bool TryParse(byte[] body, out IntegrateMatchPvpScoreRequest request)
        {
            request = default;
            // A21 FA5560 requests the first season with only a u16 target UID.
            if (body == null || body.Length != 2)
                return false;
            var uid = BitConverter.ToUInt16(body, 0);
            if (uid == 0 || uid == ushort.MaxValue)
                return false;
            request = new IntegrateMatchPvpScoreRequest(uid);
            return true;
        }
    }
}
