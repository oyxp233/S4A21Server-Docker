using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class ItemLockListBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x00FB;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var list = snapshot.InitializationSnapshot.ItemLockList;
            var writer = new GamePacketWriter();
            writer.WriteUInt16((ushort)list.Entries.Count);
            foreach (var entry in list.Entries)
            {
                writer.WriteByte(entry.TypeOrList);
                writer.WriteUInt16(entry.ItemKeyOrSlot);
                writer.WriteByte(entry.State);
                if (entry.HasExtraValue)
                    writer.WriteInt32(entry.ExtraValue);
            }
            body = writer.ToArray();
            return true;
        }
    }
}
