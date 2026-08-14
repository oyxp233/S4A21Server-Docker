using DfoServer.Game.Inventory;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class DisjointItemAckBuilder
    {
        public static byte[] BuildSuccess(DisjointItemResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(result.Request.TargetSlotIndex);
            writer.WriteByte((byte)result.Request.ItemSpace);
            writer.WriteByte((byte)(result.Materials.Count > byte.MaxValue ? byte.MaxValue : result.Materials.Count));

            for (var i = 0; i < result.Materials.Count && i < byte.MaxValue; i++)
            {
                var material = result.Materials[i];
                writer.WriteInt16(material.SlotIndex);
                writer.WriteInt32(material.ItemTemplateId);
                writer.WriteInt32(material.Count);
            }

            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }
    }
}
