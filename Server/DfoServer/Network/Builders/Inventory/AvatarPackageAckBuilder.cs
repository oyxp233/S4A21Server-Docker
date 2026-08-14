using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class AvatarPackageAckBuilder
    {
        public static byte[] BuildSuccess(short slotIndex)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(slotIndex);
            return writer.ToArray();
        }
    }
}
