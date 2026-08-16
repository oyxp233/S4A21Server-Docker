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
            writer.WriteAsciiDstr(GameNetworkConfig.AdvertisedGameIp);
            writer.WriteInt32(GameNetworkConfig.InitialUdpPort1);
            writer.WriteInt32(GameNetworkConfig.InitialUdpPort2);
            writer.WriteInt32(0x00000000);
            writer.WriteByte((byte)'0');
            writer.WriteByte((byte)'0');
            writer.WriteInt32(GameNetworkConfig.CommandPacketCount);
            writer.WriteInt32(GameNetworkConfig.NotificationPacketCount);
            writer.WriteInt32(0);
            return writer.ToArray();
        }

        public static byte[] BuildLoginSuccess(
            int listenerGamePort = GameNetworkConfig.NormalGamePort)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(0x01);
            writer.WriteByte(20);
            writer.WriteByte(0x00);
            writer.WriteByte(
                GameNetworkConfig.ResolveLoginEnvironment(
                    listenerGamePort));
            writer.WriteByte(0x00);
            writer.WriteInt32(GameNetworkConfig.LoginChannelPort);
            writer.WriteAsciiDstr(GameNetworkConfig.AdvertisedGameIp);
            writer.WriteInt32(GameNetworkConfig.LoginUnknownPort);
            writer.WriteInt32(GameNetworkConfig.LoginUnknownPort);
            writer.WriteZeroBytes(24);
            return writer.ToArray();
        }
    }
}
