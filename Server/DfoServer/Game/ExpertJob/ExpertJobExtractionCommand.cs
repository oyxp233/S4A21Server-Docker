using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class ExpertJobExtractionCommand
    {
        internal byte ExtractorType { get; set; }
        internal short ExtractorSlotIndex { get; set; }
        internal InventoryListType TargetListType { get; set; }
        internal short TargetSlotIndex { get; set; }
    }
}
