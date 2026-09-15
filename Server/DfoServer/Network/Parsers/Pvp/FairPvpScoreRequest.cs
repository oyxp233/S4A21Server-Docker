using System;

namespace DfoServer.Network.Parsers.Pvp
{
    internal readonly struct FairPvpScoreRequest
    {
        private FairPvpScoreRequest(ushort targetUserId, bool isSelf, byte viewMode, int actorIndex)
        {
            TargetUserId = targetUserId;
            IsSelf = isSelf;
            ViewMode = viewMode;
            ActorIndex = actorIndex;
        }

        internal ushort TargetUserId { get; }
        internal bool IsSelf { get; }
        internal byte ViewMode { get; }
        internal int ActorIndex { get; }

        internal static bool TryParse(byte[] body, out FairPvpScoreRequest request)
        {
            request = default;
            // A21 2741810 writes uid, self flag, view mode + 1 and actor index.
            if (body == null || body.Length != 8 || body[2] > 1
                || body[3] < 1 || body[3] > 3)
                return false;
            var uid = BitConverter.ToUInt16(body, 0);
            var actorIndex = BitConverter.ToInt32(body, 4);
            if (uid == 0 || uid == ushort.MaxValue || actorIndex < -1 || actorIndex > 2)
                return false;
            request = new FairPvpScoreRequest(uid, body[2] != 0, body[3], actorIndex);
            return true;
        }
    }
}
