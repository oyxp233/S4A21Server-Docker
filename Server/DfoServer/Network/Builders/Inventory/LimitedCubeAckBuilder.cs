using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    internal static class LimitedCubeAckBuilder
    {
        internal const byte GenericErrorCode = 0x11;

        public static byte[] BuildSuccess(InventoryTitleChangeResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt32(result != null ? result.ResultItemId : 0);
            writer.WriteInt16(result != null ? result.ResultValue : (short)0);

            // 当前 86CN150925 客户端的 sub_CD50E0 会将该字节与 1 比较，
            // sub_1E4CE70 再把比较结果写入新建结果物品的 +0x5C 字段。
            writer.WriteByte(result != null && result.ResultItemKind == ItemCore.KindEquipment
                ? (byte)0x01
                : (byte)0x00);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode = GenericErrorCode)
            => CommonPacketBodyBuilder.BuildCmdError(errorCode);
    }
}
