using DfoServer.Game.SelectCharacter;
using DfoServer.Game.TitleBook;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class TitleBookListBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0166;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var categories = snapshot.InitializationSnapshot.TitleBookCategories;
            if (occurrenceIndex < 0 || occurrenceIndex >= categories.Count)
            {
                var w = new Network.GamePacketWriter();
                w.WriteByte(0);
                w.WriteUInt16(0);
                w.WriteInt32(occurrenceIndex);
                w.WriteInt32(0);
                body = w.ToArray();
                return true;
            }

            body = BuildCategoryBody(categories[occurrenceIndex]);
            return true;
        }

        public static byte[] BuildCategoryBody(TitleBookCategorySnapshot category)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(category.InfoType);
            writer.WriteUInt16(category.OwnerId16);
            writer.WriteInt32(category.Category);
            writer.WriteInt32(category.Entries.Count);
            foreach (var entry in category.Entries)
            {
                writer.WriteUInt16(entry.SlotIndex);
                writer.WriteInt32(entry.ItemId);
                writer.WriteInt32(entry.Value);
                writer.WriteByte(entry.Attr);
                writer.WriteUInt16(entry.Durability);
                writer.WriteByte(entry.SealFlag);
                writer.WriteInt32(entry.EnchantIndex);
                writer.WriteByte(entry.EnchantUpgradeCount);
                writer.WriteByte(entry.AmplifyType);
                writer.WriteUInt16(entry.AmplifyValue);
            }
            return writer.ToArray();
        }
    }
}
