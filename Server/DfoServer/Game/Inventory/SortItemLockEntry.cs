namespace DfoServer.Game.Inventory
{
    public sealed class SortItemLockEntry
    {
        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public byte State { get; set; } = 1;
    }
}
