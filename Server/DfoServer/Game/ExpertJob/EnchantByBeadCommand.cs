using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    public sealed class EnchantByBeadCommand
    {
        public InventoryListType BeadListType { get; set; }

        public short BeadSlotIndex { get; set; }

        public InventoryListType TargetListType { get; set; }

        public short TargetSlotIndex { get; set; }
    }
}
