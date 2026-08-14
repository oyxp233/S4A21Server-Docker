using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    public static class PurifyItemAckBuilder
    {
        public static byte[] BuildSuccess(PurifyItemResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(result.MaterialSlotIndex);
            writer.WriteInt32(result.MaterialRemainingCount);
            writer.WriteInt16(result.TargetSlotIndex);
            writer.WriteByte(result.AmplifyType);
            writer.WriteUInt16(result.AmplifyValue);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
        {
            // 客户端用首个 dword 是否为 0 判断失败，0x00CC 不消费具体错误码。
            return new byte[] { 0x00, 0x00, 0x00, 0x00 };
        }
    }
}
