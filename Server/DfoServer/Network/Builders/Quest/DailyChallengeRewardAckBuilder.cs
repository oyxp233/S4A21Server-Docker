using DfoServer.Game.Quests;

namespace DfoServer.Network.Builders
{
    internal static class DailyChallengeRewardAckBuilder
    {
        internal static byte[] Build(DailyChallengeRewardClaimResult result)
        {
            if (result == null || !result.ClientSuccess)
                return new byte[] { 0, 0 };

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(result.GroupIndex);
            writer.WriteInt32(0); // Only special challenge index 5 consumes this count.
            return writer.ToArray();
        }
    }
}
