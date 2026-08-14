namespace DfoServer.Network.Builders
{
    public static class CharacterVisibilityBodyBuilder
    {
        public static byte[] Build(ushort userId, byte userStateBits)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(userId);
            writer.WriteByte(userStateBits);
            return writer.ToArray();
        }
    }
}
