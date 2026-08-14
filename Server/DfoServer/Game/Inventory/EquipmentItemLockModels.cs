namespace DfoServer.Game.Inventory
{
    public sealed class EquipmentItemLockEntry
    {
        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public byte State { get; set; } = 1;

        public int RemainingSeconds { get; set; }
    }

    public sealed class EquipmentItemLockResult
    {
        public bool Success { get; set; }

        public byte ErrorCode { get; set; }

        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public byte EquipmentLockId { get; set; }

        public int RemainingSeconds { get; set; }
    }
}
