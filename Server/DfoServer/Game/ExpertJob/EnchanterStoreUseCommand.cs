using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class EnchanterStoreUseCommand
    {
        internal ushort OwnerUserId { get; set; }
        internal int RecipeItemId { get; set; }
        internal byte Mode { get; set; }
        internal InventoryListType TargetListType { get; set; }
        internal short TargetSlotIndex { get; set; }
        internal InventoryListType CardListType { get; set; }
        internal short CardSlotIndex { get; set; }
    }
}
