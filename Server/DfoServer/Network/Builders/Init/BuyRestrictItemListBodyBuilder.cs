using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class BuyRestrictItemListBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x02DA;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var u730 = snapshot.InitializationSnapshot.Unknown730;
            var writer = new GamePacketWriter();
            writer.WriteInt32(u730.Entries.Count);
            foreach (var entry in u730.Entries)
            {
                writer.WriteInt32(entry.EntryId);
                writer.WriteInt32(entry.SentinelOrValue);
                writer.WriteInt32(entry.Flag);
            }
            body = writer.ToArray();
            return true;
        }
    }
}
