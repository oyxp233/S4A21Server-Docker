using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    internal static class MonsterCardBindAckBuilder
    {
        internal const int SuccessLength = 19;

        internal static byte[] BuildSuccess(short binderSlot, short firstCardSlot, short secondCardSlot, MonsterCardBindResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteUInt16((ushort)binderSlot);
            writer.WriteUInt16((ushort)firstCardSlot);
            writer.WriteUInt16((ushort)secondCardSlot);
            writer.WriteByte(0x01);
            writer.WriteUInt16((ushort)result.Grant.SlotIndex);
            writer.WriteInt32(result.ResultItemId);
            writer.WriteInt32(1);
            writer.WriteByte(0x00);
            return writer.ToArray();
        }

        internal static byte[] BuildError(byte errorCode)
            => CommonPacketBodyBuilder.BuildCmdError(errorCode);
    }
}
