using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class ItemStateListBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType { get; }

        public ItemStateListBodyBuilder(ushort notiType)
        {
            NotiType = notiType;
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var items = NotiType == 0x00AC
                ? snapshot.InitializationSnapshot.CooltimeItemStates
                : snapshot.InitializationSnapshot.EffectItemStates;

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)items.Count);
            foreach (var item in items)
            {
                writer.WriteInt32(item.ItemId);
                writer.WriteInt32(item.ExpireTime);
            }

            body = writer.ToArray();
            return true;
        }
    }
}
