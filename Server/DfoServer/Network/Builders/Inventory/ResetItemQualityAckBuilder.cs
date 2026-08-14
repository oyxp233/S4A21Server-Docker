using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;

namespace DfoServer.Network.Builders.Inventory
{
    internal static class ResetItemQualityAckBuilder
    {
        // 通用 CMD 层先消费 status 字节。0x0051 解析器随后读取目标物品定位:
        // itemId:int32, listType:byte, targetSlot:int32。
        public const int SuccessLength = 10;
        public const int ErrorLength = 2;

        public static byte[] BuildSuccess(ResetItemQualityResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt32(result.TargetItemTemplateId);
            writer.WriteByte((byte)InventoryListType.Main);
            writer.WriteInt32(result.TargetSlotIndex);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
        {
            // 与相邻装备命令一致: 单字节失败标志后跟服务端错误码。
            return new byte[] { 0x00, errorCode };
        }
    }
}
