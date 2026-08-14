using DfoServer.Game.Inventory;
using System.Collections.Generic;

namespace DfoServer.Game.Lottery
{
    public sealed class LotteryItemDefinition
    {
        public int ItemTemplateId { get; set; }

        public string StackableType { get; set; }

        public int GoldCost { get; set; }

        public int RequiredItemTemplateId { get; set; }

        public int RequiredItemCount { get; set; }

        public IReadOnlyList<PvfLib.BoosterRewardEntry> RewardPool { get; set; }

        public bool UsesIncreaseChanceProgress { get; set; }

        public int ProgressResetCount { get; set; }

        public int ProgressResetGoldCost { get; set; }
    }

    public sealed class LotterySourceContext
    {
        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int StackCount { get; set; }
    }

    public sealed class LotteryOpenResult
    {
        public short SourceSlotIndex { get; set; }

        public int SourceItemTemplateId { get; set; }

        public int SourceRemainingStackCount { get; set; }

        public int ConsumedGold { get; set; }

        public int UpdatedGold { get; set; }

        public int ConsumedRequiredItemTemplateId { get; set; }

        public int ConsumedRequiredItemCount { get; set; }

        public List<short> RequiredItemChangedSlots { get; } = new List<short>();

        public bool UsedDoubleReward { get; set; }

        public bool DeliveredToMailbox { get; set; }

        public LotteryProgressSnapshot Progress { get; set; }

        public List<LotteryRewardGrant> Rewards { get; } = new List<LotteryRewardGrant>();
    }

    public sealed class LotteryProgressSnapshot
    {
        public int ItemTemplateId { get; set; }

        public int NewRewardIndex { get; set; } = -1;

        public bool AutoReset { get; set; }

        public HashSet<int> ClaimedRewardIndexes { get; } = new HashSet<int>();
    }

    public sealed class LotteryRewardGrant
    {
        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int StackCount { get; set; }

        public int GrantedCount { get; set; }

        internal ItemCore DisplayCore { get; set; }
    }

}
