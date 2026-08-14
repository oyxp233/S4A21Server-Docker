namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryItem
    {
        public long ItemUid { get; set; }

        public string OwnerScope { get; set; } = "character";

        public int OwnerId { get; set; }

        public int? CharacterId { get; set; }

        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public ItemCore Core { get; set; } = new ItemCore();

        public string CreatedAt { get; set; }

        public string UpdatedAt { get; set; }
    }
}
