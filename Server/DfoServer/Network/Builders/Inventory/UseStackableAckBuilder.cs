using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class UseStackableAckBuilder
    {
        private const byte InvalidItemErrorCode = 23;

        // Practice rooms accept the use action without reporting a persistent inventory mutation.
        public static byte[] BuildPracticeSuccess(byte listType, int instanceValue, int itemCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(listType);
            writer.WriteInt32(instanceValue);
            writer.WriteInt32(itemCode);
            return writer.ToArray();
        }

        public static byte[] BuildSuccess(short slotIndex, byte listType, int instanceValue, int itemCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(slotIndex);
            writer.WriteByte(listType);
            writer.WriteInt32(instanceValue);
            writer.WriteInt32(itemCode);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte listType, int instanceValue, int itemCode)
        {
            return BuildError(
                InvalidItemErrorCode,
                listType,
                instanceValue,
                itemCode);
        }

        public static byte[] BuildError(
            byte errorCode,
            byte listType,
            int instanceValue,
            int itemCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(errorCode);
            writer.WriteByte(listType);
            writer.WriteInt32(instanceValue);
            writer.WriteInt32(itemCode);
            return writer.ToArray();
        }
    }
}
