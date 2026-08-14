using DfoServer.Game.Inventory;

namespace DfoServer.Game.SelectCharacter
{
    public enum SelectCharacterPacketTemplateKind
    {
        Raw = 0,
        ItemList = 1,
    }

    public sealed class SelectCharacterPacketTemplate
    {
        public SelectCharacterPacketTemplateKind Kind { get; set; }

        public byte Command { get; set; }

        public ushort Type { get; set; }

        public InventoryListType ItemListType { get; set; }

        public int OccurrenceIndex { get; set; }
    }
}
