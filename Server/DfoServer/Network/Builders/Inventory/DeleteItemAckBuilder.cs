using DfoServer.Game.Inventory;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class DeleteItemAckBuilder
    {
        public static byte[] Build(InventoryMutationResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte((byte)result.ListType);
            writer.WriteByte(0x01);
            writer.WriteInt16(result.SlotIndex);
            writer.WriteInt32(result.AppliedCount);
            writer.WriteInt16(0);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte listType)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(0x17);
            writer.WriteByte(listType);
            return writer.ToArray();
        }
    }
}
