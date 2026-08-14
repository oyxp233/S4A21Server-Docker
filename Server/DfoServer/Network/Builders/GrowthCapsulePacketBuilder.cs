namespace DfoServer.Network.Builders
{
    public static class GrowthCapsulePacketBuilder
    {
        public static byte[] BuildClaimAck(bool success, int itemId = 0, int itemCount = 0)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(success ? (byte)0 : (byte)1);
            if (success)
            {
                writer.WriteUInt32(0);
                writer.WriteUInt32((uint)System.Math.Max(0, itemId));
                writer.WriteUInt32((uint)System.Math.Max(0, itemCount));
            }
            return writer.ToArray();
        }
    }
}
