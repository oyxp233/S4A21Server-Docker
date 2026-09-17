using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Network.Builders
{
    internal readonly struct AntonAwakeningRewardEntry
    {
        internal AntonAwakeningRewardEntry(
            ushort userId,
            byte cardType,
            uint flags,
            uint itemId,
            uint quantity)
        {
            UserId = userId;
            CardType = cardType;
            Flags = flags;
            ItemId = itemId;
            Quantity = quantity;
        }

        internal ushort UserId { get; }
        internal byte CardType { get; }
        internal uint Flags { get; }
        internal uint ItemId { get; }
        internal uint Quantity { get; }
    }

    internal static class AntonAwakeningRewardPacketBuilder
    {
        internal const int HeaderSize = sizeof(uint);
        internal const int EntrySize = sizeof(ushort) + sizeof(byte)
            + sizeof(uint) + sizeof(uint) + sizeof(uint);

        internal static byte[] Build(
            IReadOnlyList<AntonAwakeningRewardEntry> entries)
        {
            if (entries == null)
                return null;

            var seen = new HashSet<ushort>();
            foreach (var entry in entries)
            {
                if (entry.UserId == 0
                    || entry.ItemId == 0
                    || entry.Quantity == 0
                    || !seen.Add(entry.UserId))
                {
                    return null;
                }
            }

            using (var stream = new MemoryStream(HeaderSize + entries.Count * EntrySize))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((uint)entries.Count);
                foreach (var entry in entries)
                {
                    writer.Write(entry.UserId);
                    writer.Write(entry.CardType);
                    writer.Write(entry.Flags);
                    writer.Write(entry.ItemId);
                    writer.Write(entry.Quantity);
                }
                return stream.ToArray();
            }
        }
    }
}
