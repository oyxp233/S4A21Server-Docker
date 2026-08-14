using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    public static class ChronicleRefineAckBuilder
    {
        public static byte[] BuildSuccess(ChronicleRefineResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(result.Command.MaterialSlotIndex);
            writer.WriteInt16((short)System.Math.Max(0, System.Math.Min(short.MaxValue, result.MaterialRemainingStackCount)));
            writer.WriteByte(result.RefineSucceeded ? (byte)0x01 : (byte)0x00);

            if (!result.RefineSucceeded)
            {
                // 86 client handler 0x00CE9C70 reads a reserved byte before the
                // destroyed target slot, then standard slot/item/count reward rows.
                writer.WriteByte(0x00);
                writer.WriteInt16(result.Command.TargetSlotIndex);
                writer.WriteByte((byte)System.Math.Min(byte.MaxValue, result.FailureRewards.Count));
                for (var i = 0; i < result.FailureRewards.Count && i < byte.MaxValue; i++)
                {
                    var reward = result.FailureRewards[i];
                    writer.WriteInt16(reward.SlotIndex);
                    writer.WriteInt32(reward.ItemTemplateId);
                    writer.WriteInt32(reward.Count);
                }
            }

            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
        {
            return new byte[] { 0x00, errorCode };
        }
    }
}
