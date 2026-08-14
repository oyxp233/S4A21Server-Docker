using DfoServer.Game.CraneMiniGame;
using System;

namespace DfoServer.Network.Builders
{
    internal static class CraneMiniGameStartAckBuilder
    {
        internal static byte[] BuildSuccess(CraneMiniGameStartResult result)
        {
            if (result?.DisplayItems == null || result.DisplayItems.Count != 6)
                throw new ArgumentException("The 86JP crane start response requires six display items.", nameof(result));

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt16(result.MachineId);
            writer.WriteUInt32(unchecked((uint)result.MaterialRemainingCount));
            foreach (var item in result.DisplayItems)
                writer.WriteUInt32(unchecked((uint)item.CatalogIndex));
            return writer.ToArray();
        }

        internal static byte[] BuildFailure(byte errorCode = 0x04)
            => new byte[] { 0, errorCode };
    }
}
