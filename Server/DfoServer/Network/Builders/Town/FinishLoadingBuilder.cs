using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class FinishLoadingBuilder
    {
        public static byte[] BuildNotification()
        {
            var writer = new GamePacketWriter();

            writer.WriteInt32(0x00000000);
            writer.WriteByte(0x00);
            return writer.ToArray();
        }
    }
}