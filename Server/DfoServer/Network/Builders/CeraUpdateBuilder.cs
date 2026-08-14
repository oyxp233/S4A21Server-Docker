namespace DfoServer.Network.Builders
{
    public static class CeraUpdateBuilder
    {
        public static byte[] Build(int cera, int tokenCera, int happyTokenCera)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(cera);
            writer.WriteInt32(tokenCera);
            writer.WriteInt32(happyTokenCera);
            return writer.ToArray();
        }
    }
}
