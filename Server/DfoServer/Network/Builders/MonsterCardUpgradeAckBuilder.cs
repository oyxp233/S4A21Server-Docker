using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    internal static class MonsterCardUpgradeAckBuilder
    {
        internal const int SuccessLength = 7;

        internal static byte[] BuildSuccess(MonsterCardUpgradeResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte(result.Success ? (byte)0x01 : (byte)0x00);
            writer.WriteInt32(result.TargetItemId);
            writer.WriteByte(result.UpgradeCount);
            return writer.ToArray();
        }

        internal static byte[] BuildError(byte errorCode)
            => CommonPacketBodyBuilder.BuildCmdError(errorCode);
    }
}
