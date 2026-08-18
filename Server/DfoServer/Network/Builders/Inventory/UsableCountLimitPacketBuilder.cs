using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;

namespace DfoServer.Network.Builders
{
    internal static class UsableCountLimitPacketBuilder
    {
        internal static byte[] BuildUpdateBody(UsableCountLimitState state)
        {
            if (state == null)
                return Array.Empty<byte>();

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(state.ItemId);
            writer.WriteInt32(state.UsedCount);
            return writer.ToArray();
        }
    }
}
