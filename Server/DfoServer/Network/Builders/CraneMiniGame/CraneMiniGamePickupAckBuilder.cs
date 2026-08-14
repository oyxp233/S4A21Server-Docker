using DfoServer.Game.CraneMiniGame;

namespace DfoServer.Network.Builders
{
    internal static class CraneMiniGamePickupAckBuilder
    {
        internal static byte[] BuildSuccess(CraneMiniGameItem item)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt32(unchecked((uint)(item?.ItemId ?? 0)));
            writer.WriteInt16(checked((short)item.Count));
            return writer.ToArray();
        }

        internal static byte[] BuildFailure(byte errorCode = 0x04)
            => new byte[] { 0, errorCode };
    }
}
