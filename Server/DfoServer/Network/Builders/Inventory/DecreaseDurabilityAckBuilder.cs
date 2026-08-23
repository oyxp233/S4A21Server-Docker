using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    internal static class DecreaseDurabilityAckBuilder
    {
        internal const byte ErrorInvalidTarget = 17;

        internal static byte[] BuildSuccess(short slotIndex, ushort durability)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte((byte)slotIndex);
            writer.WriteUInt16(durability);
            return writer.ToArray();
        }

        internal static byte[] BuildError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }
    }
}
