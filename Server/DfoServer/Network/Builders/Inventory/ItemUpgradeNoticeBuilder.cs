using DfoServer.Game.ItemUpgrade;

namespace DfoServer.Network.Builders
{
    public static class ItemUpgradeNoticeBuilder
    {
        public static byte[] Build(ItemUpgradeResult result, ushort userUniqueId)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(0x01);
            writer.WriteByte(result.UpgradeSucceeded ? (byte)1 : (byte)0);
            writer.WriteUInt16(userUniqueId);
            writer.WriteInt32(result.TargetItemTemplateId);
            writer.WriteByte(result.UpgradeSucceeded ? result.NewLevel : result.OldLevel);
            if (result.TargetItemSnapshot == null)
                writer.WriteByte(0x00);
            else
                RandomOptionProtocolWriter.WriteDynamic(writer, result.TargetItemSnapshot);

            return writer.ToArray();
        }
    }
}
