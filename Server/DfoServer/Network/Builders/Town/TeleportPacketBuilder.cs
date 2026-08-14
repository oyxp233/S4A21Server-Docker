using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class TeleportPacketBuilder
    {
        public static byte[] BuildTeleportResponse(short type, int itemCode)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(0x01);
            writer.WriteInt16(type);
            writer.WriteInt32(itemCode);
            return writer.ToArray();
        }
    }
}
