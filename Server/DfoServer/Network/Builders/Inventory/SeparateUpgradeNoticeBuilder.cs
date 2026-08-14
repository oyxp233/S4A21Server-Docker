using DfoServer.Game.ItemUpgrade;

namespace DfoServer.Network.Builders
{
    internal static class SeparateUpgradeNoticeBuilder
    {
        internal static byte[] Build(SeparateUpgradeResult result, ushort userUniqueId)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x0E);
            writer.WriteByte(result.UpgradeSucceeded ? (byte)1 : (byte)0);
            writer.WriteUInt16(userUniqueId);
            writer.WriteInt32(result.Command.TargetItemTemplateId);
            writer.WriteByte(result.TargetReinforceLevel);
            writer.WriteByte(result.NewLevel);
            RandomOptionProtocolWriter.WriteDynamic(writer, result.TargetItemSnapshot);
            return writer.ToArray();
        }
    }
}
