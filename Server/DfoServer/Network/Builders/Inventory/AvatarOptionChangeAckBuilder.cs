using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    internal static class AvatarOptionChangeAckBuilder
    {
        public static byte[] BuildSuccess(short targetSlotIndex, ushort abilityNo)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(targetSlotIndex);
            writer.WriteByte((byte)abilityNo);
            return writer.ToArray();
        }

        public static byte[] BuildError()
        {
            return new byte[] { 0x00 };
        }
    }
}
