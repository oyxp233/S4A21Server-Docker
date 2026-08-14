using System.Collections.Generic;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class ExpertJobCompoundOutput
    {
        internal int ItemId { get; set; }
        internal int Count { get; set; }
    }

    internal sealed class ExpertJobCompoundResult
    {
        internal byte ErrorCode { get; set; }
        internal int SuccessCount { get; set; }
        internal int FailureCount { get; set; }
        internal int ExperienceGain { get; set; }
        internal uint FinalExperience { get; set; }
        internal int GoldSpent { get; set; }
        internal bool RequiresExpertJobInfoRefresh { get; set; }
        internal bool ExtractorInventoryChanged { get; set; }
        internal List<ExpertJobCompoundOutput> AttemptedOutputs { get; } =
            new List<ExpertJobCompoundOutput>();
        internal List<ExpertJobCompoundOutput> Outputs { get; } =
            new List<ExpertJobCompoundOutput>();
        internal List<short> ChangedMainSlots { get; } = new List<short>();
        internal List<int> LearnedRecipeIds { get; } = new List<int>();

        internal void AddChangedMainSlot(short slotIndex)
        {
            if (slotIndex >= 0 && !ChangedMainSlots.Contains(slotIndex))
                ChangedMainSlots.Add(slotIndex);
        }
    }
}
