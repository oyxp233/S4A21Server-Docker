namespace DfoServer.Network.Builders
{
    internal static class DyeItemAckBuilder
    {
        public static byte[] BuildSuccess(
            short avatarSlotIndex,
            ushort color1,
            ushort color2)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(avatarSlotIndex);
            writer.WriteUInt32(4);
            writer.WriteUInt16(color1);
            writer.WriteUInt16(color2);
            return writer.ToArray();
        }

        public static byte[] BuildError()
        {
            return new byte[] { 0x00 };
        }
    }
}
