using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class LoginPacketBuilder
    {
        public static byte[] BuildInitialLoginNotice(
            int listenerGamePort = GameNetworkConfig.NormalGamePort)
        {
            var channel =
                GameNetworkConfig.ResolveGameChannel(listenerGamePort);
            var writer = new GamePacketWriter();

            writer.WriteByte(0x01);
            writer.WriteAsciiDstr(channel.LoginName);
            writer.WriteInt32(0x00000000);
            writer.WriteInt32(0x00000000);
            writer.WriteByte((byte)GameNetworkConfig.ChannelServerIndex);
            writer.WriteByte((byte)channel.ChannelId);
            writer.WriteByte(0x00);
            writer.WriteInt32((int)System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.WriteInt32(0x00000001);
            writer.WriteAsciiDstr(GameNetworkConfig.ServerIp);
            writer.WriteInt32(GameNetworkConfig.InitialUdpPort1);
            writer.WriteInt32(GameNetworkConfig.InitialUdpPort2);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteInt32(GameNetworkConfig.CommandPacketCount);
            writer.WriteInt32(GameNetworkConfig.NotificationPacketCount);
            return writer.ToArray();
        }

        public static byte[] BuildLoginSuccess(
            int listenerGamePort = GameNetworkConfig.NormalGamePort)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(0x01);
            writer.WriteByte(18);
            writer.WriteByte(0x00);
            writer.WriteByte(
                GameNetworkConfig.ResolveLoginEnvironment(
                    listenerGamePort));
            writer.WriteByte(0x00);
            writer.WriteInt32(GameNetworkConfig.LoginChannelPort);
            writer.WriteAsciiDstr(GameNetworkConfig.ServerIp);
            writer.WriteInt32(GameNetworkConfig.LoginUnknownPort);
            writer.WriteInt32(GameNetworkConfig.LoginUnknownPort);
            writer.WriteZeroBytes(24);
            return writer.ToArray();
        }
    }
}