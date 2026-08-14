using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class EnchanterStoreUseResult
    {
        internal byte ErrorCode { get; set; }
        internal bool EnchantSucceeded { get; set; }
        internal InventoryListType TargetListType { get; set; }
        internal short TargetSlotIndex { get; set; }
        internal InventoryListType CardListType { get; set; }
        internal short CardSlotIndex { get; set; }
        internal int RequesterGold { get; set; }
        internal int OwnerGold { get; set; }
        internal int Endurance { get; set; }
        internal int ExperienceGain { get; set; }
        internal uint FinalExperience { get; set; }
    }
}
