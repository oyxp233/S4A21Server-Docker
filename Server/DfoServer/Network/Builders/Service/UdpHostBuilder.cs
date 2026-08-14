using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class UdpHostBuilder
    {
        public static byte[] BuildUnavailable()
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            return writer.ToArray();
        }
    }
}