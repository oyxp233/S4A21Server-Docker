using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal readonly struct ChangeTutorialFlagRequest
    {
        internal byte Mode { get; }
        internal uint FlagIndex { get; }
        internal byte RewardFlag { get; }

        private ChangeTutorialFlagRequest(
            byte mode,
            uint flagIndex,
            byte rewardFlag)
        {
            Mode = mode;
            FlagIndex = flagIndex;
            RewardFlag = rewardFlag;
        }

        internal static bool TryParse(
            byte[] body,
            out ChangeTutorialFlagRequest request)
        {
            request = default;
            // A21 clients may omit the nine reserved bytes and send only the
            // mode, flag index and reward flag (6B). The full capture layout
            // remains 15B; both layouts carry the same fields at offsets 0..5.
            if (body == null || body.Length < 6)
                return false;

            request = new ChangeTutorialFlagRequest(
                body[0],
                BitConverter.ToUInt32(body, 1),
                body[5]);
            return true;
        }
    }
}
