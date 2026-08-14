using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public sealed class EmblemCompoundRequest
    {
        public List<EmblemCompoundInput> Inputs { get; } = new List<EmblemCompoundInput>();
    }

    public sealed class EmblemCompoundInput
    {
        public int ItemTemplateId { get; set; }
        public short SlotIndex { get; set; }
    }

    public sealed class EmblemCompoundResult
    {
        public const byte ErrorInvalidRequest = 0x16;
        public const byte ErrorInventoryFull = 0x17;

        public byte ErrorCode { get; set; }
        public int RewardItemTemplateId { get; set; }
        public short RewardSlotIndex { get; set; }
        public int RewardGrantedCount { get; set; }
        public int RewardStackCount { get; set; }
        public int PvfBoosterItemTemplateId { get; set; }
        public List<short> ChangedSlots { get; } = new List<short>();
    }
}
