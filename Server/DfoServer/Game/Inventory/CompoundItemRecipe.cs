using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public sealed class CompoundItemRecipeRequest
    {
        public int SourceValue { get; set; }

        public bool SourceIsItemId { get; set; }

        public ushort RequestedCount { get; set; }

        public ushort? OutputCount { get; set; }
    }

    public sealed class CompoundItemRecipeResult
    {
        public bool Success => ErrorCode == 0;

        public byte ErrorCode { get; set; }

        public short SourceSlotIndex { get; set; } = -1;

        public int SourceItemTemplateId { get; set; }

        public ushort RequestedCount { get; set; }

        public bool SourceConsumed { get; set; }

        public List<CompoundItemDeletedEntry> DeletedEntries { get; } = new List<CompoundItemDeletedEntry>();

        public List<BoosterRewardResult> Rewards { get; } = new List<BoosterRewardResult>();

        public string RecipeType { get; set; } = string.Empty;

        public string PvfPath { get; set; } = string.Empty;

        public int GoldSpent { get; set; }

        public int UpdatedGold { get; set; }

        internal IReadOnlyList<short> GetMainRefreshSlots()
        {
            var slots = new List<short>();
            foreach (var entry in DeletedEntries)
            {
                if (entry.ListType == InventoryListType.Main && !slots.Contains(entry.SlotIndex))
                    slots.Add(entry.SlotIndex);
            }

            foreach (var reward in Rewards)
            {
                if (reward.ListType == InventoryListType.Main && !slots.Contains(reward.SlotIndex))
                    slots.Add(reward.SlotIndex);
            }

            return slots;
        }
    }

    public sealed class CompoundItemDeletedEntry
    {
        public InventoryListType ListType { get; set; } = InventoryListType.Main;

        public short SlotIndex { get; set; }

        public int Count { get; set; }

        public int ItemTemplateId { get; set; }
    }

    internal sealed class CompoundItemRecipeDefinition
    {
        public string PvfPath { get; set; } = string.Empty;

        public string RecipeType { get; set; } = string.Empty;

        public IReadOnlyList<CompoundItemRecipeEntry> Materials { get; set; } = Array.Empty<CompoundItemRecipeEntry>();

        public IReadOnlyList<CompoundItemRecipeEntry> Outputs { get; set; } = Array.Empty<CompoundItemRecipeEntry>();

        /// <summary>金币费用（来自 [input item] 中的 goldId=0 / goldAmount），设计图 IntData 路径为 0。</summary>
        public int GoldCost { get; set; }
    }

    internal sealed class CompoundItemRecipeEntry
    {
        public CompoundItemRecipeEntry(int itemTemplateId, int count)
        {
            ItemTemplateId = itemTemplateId;
            Count = count;
        }

        public int ItemTemplateId { get; }

        public int Count { get; }
    }
}
