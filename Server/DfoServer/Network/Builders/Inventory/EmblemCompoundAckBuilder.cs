using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    public static class EmblemCompoundAckBuilder
    {
        public const int SuccessLength = 10;

        public static byte[] BuildSuccess(EmblemCompoundResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);

            // The client reads a one-byte result count, then int32 pairs of
            // item template id and count. Slot changes use UPDATE_ITEM_LIST.
            writer.WriteByte(0x01);
            writer.WriteInt32(result.RewardItemTemplateId);
            writer.WriteInt32(result.RewardGrantedCount);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }

    }
}
