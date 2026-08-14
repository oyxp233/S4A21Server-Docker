namespace DfoServer.Network.Builders
{
    public static class LotteryOverflowConfirmAckBuilder
    {
        public static byte[] Build(byte[] requestBody)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte(requestBody != null && requestBody.Length > 1
                ? requestBody[1]
                : (byte)0);
            writer.WriteByte(requestBody != null && requestBody.Length > 2
                ? requestBody[2]
                : (byte)0);
            return writer.ToArray();
        }
    }
}
