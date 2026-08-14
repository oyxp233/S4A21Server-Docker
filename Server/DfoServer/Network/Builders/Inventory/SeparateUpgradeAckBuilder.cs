using DfoServer.Game.ItemUpgrade;

namespace DfoServer.Network.Builders
{
    internal static class SeparateUpgradeAckBuilder
    {
        internal static byte[] BuildSuccess(SeparateUpgradeResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt16(result.Command.MaterialSlotIndex);
            writer.WriteInt32(result.MaterialRemainingCount);
            writer.WriteByte(result.OldLevel);
            writer.WriteByte(result.UpgradeSucceeded ? (byte)0 : (byte)1);
            writer.WriteByte(result.NewLevel);
            writer.WriteByte((byte)result.Command.TargetListType);
            writer.WriteInt16(result.Command.TargetSlotIndex);
            return writer.ToArray();
        }

        internal static byte[] BuildError(byte errorCode)
            => new[] { (byte)0, errorCode };
    }
}
