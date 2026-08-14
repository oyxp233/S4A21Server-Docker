namespace DfoServer.Network.Builders
{
    public static class LotteryItemNoticeBuilder
    {
        public static byte[] Build(ushort userUniqueId, int itemTemplateId, byte upgradeLevel)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x02);
            writer.WriteByte(0x01);
            writer.WriteUInt16(userUniqueId);
            writer.WriteInt32(itemTemplateId);
            writer.WriteByte(upgradeLevel);
            return writer.ToArray();
        }
    }
}
