namespace DfoServer.Game.Inventory
{
    internal sealed class VirtualCountItem
    {
        public short SlotIndex { get; set; }

        public int ItemId { get; set; }

        public int Count { get; set; }

        public VirtualCountItem Copy()
        {
            return new VirtualCountItem
            {
                SlotIndex = SlotIndex,
                ItemId = ItemId,
                Count = Count,
            };
        }
    }
}
