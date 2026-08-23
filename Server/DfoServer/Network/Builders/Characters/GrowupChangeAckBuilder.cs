using DfoServer.Game.Characters;
using DfoServer.Network;

namespace DfoServer.Network.Builders.Characters
{
    internal static class GrowupChangeAckBuilder
    {
        internal static byte[] Build(GrowupChangeResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(result?.ResultCode ?? GrowupChangeResult.ResultCodeInvalidState);
            writer.WriteByte(result?.AckChangeCount ?? 0);
            return writer.ToArray();
        }
    }
}
