using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    internal static class TitleChangeAckBuilder
    {
        internal const byte GenericErrorCode = 0x11;

        public static byte[] BuildSuccess(InventoryTitleChangeResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);

            // 当前 86CN150925 客户端的 sub_CDB120 依次读取
            // PVF 分支标记、结果物品 ID 和实际来源道具 ID。
            writer.WriteByte(result != null && result.IsSuccessBranch
                ? (byte)0x01
                : (byte)0x00);
            writer.WriteInt32(result != null ? result.ResultItemId : 0);
            writer.WriteInt32(result != null ? result.SourceItemId : 0);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode = GenericErrorCode)
        {
            return CommonPacketBodyBuilder.BuildCmdError(errorCode);
        }
    }
}
