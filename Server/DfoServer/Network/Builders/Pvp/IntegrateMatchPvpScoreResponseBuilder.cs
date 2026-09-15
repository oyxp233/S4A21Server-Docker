using System;

namespace DfoServer.Network.Builders.Pvp
{
    internal static class IntegrateMatchPvpScoreResponseBuilder
    {
        internal static byte[] BuildEmptyBody(ushort targetUserId)
        {
            if (targetUserId == 0 || targetUserId == ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(targetUserId));

            // A21 27415D0 consumes 68 bytes after the dispatcher success byte.
            // FAAB30 resolves name/job/server from the cached target CUser.
            // There is no verified persisted first-season history owner yet.
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt16(targetUserId);
            writer.WriteZeroBytes(3 * sizeof(uint)); // 1v1: wins/streak/perfect
            writer.WriteZeroBytes(3 * sizeof(uint)); // individual: wins/kills/multiple kills
            writer.WriteZeroBytes(3 * sizeof(uint)); // relay: wins/kills/double kills
            writer.WriteZeroBytes(3 * sizeof(uint)); // team: wins/kills/double kills
            writer.WriteUInt32(0); // cumulative wins
            writer.WriteUInt32(0); // hell-mode statistic 1
            writer.WriteUInt32(0); // hell-mode statistic 2
            writer.WriteUInt16(0); // hell-mode statistic 3
            writer.WriteUInt32(0); // final statistic, meaning unconfirmed
            return writer.ToArray();
        }
    }
}
