namespace DfoServer.Network.Builders
{
    public static class ServerBroadcastTimeMessageBuilder
    {
        public static byte[] Build(string message, int durationMilliseconds = 5000)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(durationMilliseconds);
            writer.WriteClientDstr(message ?? string.Empty);
            return writer.ToArray();
        }
    }
}
