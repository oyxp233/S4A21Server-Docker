namespace DfoServer.Network.Builders
{
    internal static class ImageCommunicationAckBuilder
    {
        internal static byte[] Build(int npcIndex)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(npcIndex > 0 ? npcIndex : 0);
            return writer.ToArray();
        }
    }
}
