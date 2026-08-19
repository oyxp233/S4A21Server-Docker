using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class TeleportPacketBuilder
    {
        public static byte[] BuildTeleportNotification(int itemCode)
        {
            var writer = new GamePacketWriter();

            // A21 成功样本: [01][itemId][09 00 00 00]。
            writer.WriteByte(0x01);
            writer.WriteInt32(itemCode);
            writer.WriteInt32(9);
            return writer.ToArray();
        }
    }
}
