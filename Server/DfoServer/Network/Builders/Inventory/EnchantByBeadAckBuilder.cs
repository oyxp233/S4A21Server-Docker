using DfoServer.Game.ExpertJob;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class EnchantByBeadAckBuilder
    {
        public static byte[] BuildSuccess(EnchantByBeadResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte((byte)result.Command.TargetListType);
            writer.WriteInt16(result.Command.TargetSlotIndex);
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
