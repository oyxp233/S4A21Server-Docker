using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.GameWorld
{
    internal sealed class QuestCompletionDefinition
    {
        private QuestCompletionDefinition(
            int questId,
            string grade,
            string type,
            bool isRepeatable,
            IReadOnlyList<QuestRewardItem> seekingItems,
            QuestRewardDefinition rewardDefinition)
        {
            QuestId = questId;
            Grade = grade ?? string.Empty;
            Type = type ?? string.Empty;
            IsRepeatable = isRepeatable;
            SeekingItems = seekingItems
                ?? Array.Empty<QuestRewardItem>();
            RewardDefinition = rewardDefinition
                ?? throw new ArgumentNullException(nameof(rewardDefinition));
        }

        internal int QuestId { get; }

        internal string Grade { get; }

        internal string Type { get; }

        internal bool IsRepeatable { get; }

        internal IReadOnlyList<QuestRewardItem> SeekingItems { get; }

        internal QuestRewardDefinition RewardDefinition { get; }

        internal bool SupportsBatchCompletion =>
            IsRepeatable
            && string.Equals(Type, "seeking", StringComparison.Ordinal)
            && SeekingItems.Count > 0;

        internal static bool TryCreate(
            int questId,
            string grade,
            string type,
            string intData,
            bool isRepeatable,
            QuestRewardDefinition rewardDefinition,
            out QuestCompletionDefinition definition,
            out string error)
        {
            definition = null;
            error = string.Empty;
            if (questId <= 0)
            {
                error = "invalid quest id";
                return false;
            }
            if (rewardDefinition == null
                || rewardDefinition.QuestId != questId)
            {
                error = "invalid quest reward definition";
                return false;
            }

            var seekingItems = Array.Empty<QuestRewardItem>();
            if (string.Equals(type, "seeking", StringComparison.Ordinal))
            {
                if (!TryParseSeekingItems(intData, out seekingItems, out error))
                    return false;
            }

            definition = new QuestCompletionDefinition(
                questId,
                grade,
                type,
                isRepeatable,
                new ReadOnlyCollection<QuestRewardItem>(seekingItems),
                rewardDefinition);
            return true;
        }

        private static bool TryParseSeekingItems(
            string intData,
            out QuestRewardItem[] items,
            out string error)
        {
            items = Array.Empty<QuestRewardItem>();
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(intData))
            {
                error = "seeking quest has no item requirements";
                return false;
            }

            var tokens = intData.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || (tokens.Length & 1) != 0)
            {
                error = "seeking item data is not a complete item/count list";
                return false;
            }

            var order = new List<int>();
            var requiredByItem = new Dictionary<int, int>();
            for (var index = 0; index < tokens.Length; index += 2)
            {
                if (!int.TryParse(tokens[index], out var itemId)
                    || !int.TryParse(tokens[index + 1], out var count)
                    || itemId < 0
                    || count <= 0)
                {
                    error = "seeking item data contains a negative item or non-positive count";
                    return false;
                }

                if (!requiredByItem.TryGetValue(itemId, out var current))
                {
                    order.Add(itemId);
                    current = 0;
                }

                var combined = (long)current + count;
                if (combined > int.MaxValue)
                {
                    error = "seeking item requirement exceeds int32";
                    return false;
                }
                requiredByItem[itemId] = (int)combined;
            }

            items = new QuestRewardItem[order.Count];
            for (var index = 0; index < order.Count; index++)
            {
                var itemId = order[index];
                items[index] = new QuestRewardItem
                {
                    ItemId = itemId,
                    Count = requiredByItem[itemId],
                };
            }
            return true;
        }
    }
}
