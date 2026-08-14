namespace DfoServer.Network.Builders
{
    public static class ServerNoticeMessageBuilder
    {
        public static byte[] Build(string message, byte mode = 0)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(mode);
            writer.WriteUtf8Dstr(message ?? string.Empty);
            return writer.ToArray();
        }
    }
}
