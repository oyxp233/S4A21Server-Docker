using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;

namespace DfoServer.Network.Builders
{
    public static class AvatarDisjointAckBuilder
    {
        public static byte[] BuildSuccess(AvatarDisjointResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt16(result.Request.SlotIndex);
            writer.WriteUInt16((ushort)Math.Min(ushort.MaxValue, result.Materials.Count));
            for (var i = 0; i < result.Materials.Count && i < ushort.MaxValue; i++)
            {
                var reward = result.Materials[i];
                writer.WriteInt16(reward.SlotIndex);
                writer.WriteInt32(reward.ItemTemplateId);
                writer.WriteInt32(reward.Count);
            }
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }
    }
}
