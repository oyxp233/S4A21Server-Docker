using DfoServer.Game.Inventory;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class MoveItemSpaceAckBuilder
    {
        public const byte InvalidOperationErrorCode = 0x02;
        public const int SuccessBodyLength = 11;
        public const int ErrorBodyLength = 4;

        public static byte[] Build(InventoryMoveResult result)
        {
            var writer = new GamePacketWriter();

            // CMD ACK dispatcher 先消费 +0x00 成功标志；86JP 0x00CECCE0 随后按序读取 10B。
            writer.WriteByte(0x01);                              // +0x00 [u8]  成功标志
            writer.WriteByte((byte)result.SourceListType);       // +0x01 [u8]  来源空间
            writer.WriteInt16(result.SourceSlotIndex);           // +0x02 [i16] 来源槽
            writer.WriteInt32(result.MoveValue32);               // +0x04 [i32] 移动后的数量/实例值
            writer.WriteByte((byte)result.DestinationListType);  // +0x08 [u8]  目标空间
            writer.WriteInt16(result.DestinationSlotIndex);      // +0x09 [i16] 目标槽
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode, byte srcListType, byte dstListType)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);       // +0x00 [u8] 失败标志
            writer.WriteByte(errorCode);  // +0x01 [u8] 客户端错误码
            writer.WriteByte(srcListType);// +0x02 [u8] 来源空间
            writer.WriteByte(dstListType);// +0x03 [u8] 目标空间
            return writer.ToArray();
        }
    }
}
