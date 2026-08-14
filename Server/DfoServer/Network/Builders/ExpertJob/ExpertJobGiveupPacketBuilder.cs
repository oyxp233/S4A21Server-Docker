using System;
using DfoServer.Game.ExpertJob;

namespace DfoServer.Network.Builders.ExpertJob
{
    internal static class ExpertJobGiveupPacketBuilder
    {
        internal static byte[] BuildSuccess(ExpertJobGiveupResult result)
        {
            if (result == null || !result.Success)
                throw new ArgumentException("a successful giveup result is required", nameof(result));

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(result.CurrentGold);
            writer.WriteByte(result.GiveupCount);
            return writer.ToArray();
        }

        internal static byte[] BuildError(byte errorCode)
            => new[] { (byte)0, errorCode };
    }
}
