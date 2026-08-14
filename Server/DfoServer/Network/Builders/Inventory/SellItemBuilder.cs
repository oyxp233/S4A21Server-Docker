using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class SellItemBuilder
    {
        public static byte[] Build(byte listType, short slotIndex, short sellCount, int updatedGold)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt32(updatedGold);
            writer.WriteByte(listType);
            writer.WriteInt16(slotIndex);
            writer.WriteInt16(sellCount);
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