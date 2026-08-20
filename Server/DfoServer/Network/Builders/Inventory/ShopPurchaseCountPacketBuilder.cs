using DfoServer.Game.SelectCharacter;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    internal static class ShopPurchaseCountPacketBuilder
    {
        internal static byte[] Build(IReadOnlyList<ItemValueEntrySnapshot> entries)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt32(entries?.Count ?? 0);
            if (entries != null)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    writer.WriteInt32(entry.ItemId);
                    writer.WriteInt32(entry.Value);
                }
            }

            writer.WriteInt32(0);
            return writer.ToArray();
        }
    }
}
