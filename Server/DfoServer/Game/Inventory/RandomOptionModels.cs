using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class RandomOptionEntry
    {
        public byte Type { get; set; }
        public byte Value1 { get; set; }
        public byte Value2 { get; set; }
    }

    public sealed class RandomOptionUnsealResult
    {
        public InventoryListType TargetListType { get; set; } = InventoryListType.Main;

        public bool TargetEquipped { get; set; }
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public int GoldCost { get; set; }
        public int UpdatedGold { get; set; }
        public int ReplacedOptionIndex { get; set; } = -1;
        internal List<RandomOptionEntry> RandomOptions { get; set; }
        internal List<int> ChangeOptionCandidates { get; set; }
    }
}
