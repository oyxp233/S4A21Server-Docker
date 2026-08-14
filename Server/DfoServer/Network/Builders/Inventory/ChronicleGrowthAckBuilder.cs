using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Builders
{
    public static class ChronicleGrowthAckBuilder
    {
        public static byte[] BuildSuccess(ChronicleGrowthResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte(result.GrowthSucceeded ? (byte)0x01 : (byte)0x00);
            writer.WriteByte((byte)Math.Min(byte.MaxValue, result.Consumptions.Count));
            foreach (var consumption in result.Consumptions)
            {
                writer.WriteByte((byte)consumption.ListType);
                writer.WriteInt16(consumption.SlotIndex);
                writer.WriteInt32(consumption.ConsumedCount);
            }
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode) => new[] { (byte)0x00, errorCode };
    }
}
