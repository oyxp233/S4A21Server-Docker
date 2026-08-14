using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestGiveupItemRecoveryEntry
    {
        internal int ItemId { get; set; }
        internal int RetainCount { get; set; }
    }

    internal static class QuestGiveupItemRecoveryPolicy
    {
        internal static IReadOnlyList<QuestGiveupItemRecoveryEntry> Build(
            IReadOnlyCollection<ActiveQuest> active,
            ushort abandonedQuestId)
            => Build(
                active,
                abandonedQuestId,
                questId => QuestData.GetEventItems(questId),
                questId => QuestData.GetSeekingConsumeItems(questId),
                IsQuestItem);

        internal static IReadOnlyList<QuestGiveupItemRecoveryEntry> Build(
            IReadOnlyCollection<ActiveQuest> active,
            ushort abandonedQuestId,
            Func<ushort, IReadOnlyCollection<QuestRewardItem>> getEventItems,
            Func<ushort, IReadOnlyCollection<QuestRewardItem>> getSeekingItems,
            Func<int, bool> isQuestItem)
        {
            if (getEventItems == null)
                throw new ArgumentNullException(nameof(getEventItems));
            if (getSeekingItems == null)
                throw new ArgumentNullException(nameof(getSeekingItems));
            if (isQuestItem == null)
                throw new ArgumentNullException(nameof(isQuestItem));

            var activationItemIds = new HashSet<int>();
            AddItemIds(
                activationItemIds,
                getEventItems(abandonedQuestId));
            var candidateItemIds = new HashSet<int>(activationItemIds);
            AddItemIds(
                candidateItemIds,
                getSeekingItems(abandonedQuestId));

            var result = new List<QuestGiveupItemRecoveryEntry>();
            foreach (var itemId in candidateItemIds)
            {
                if (!activationItemIds.Contains(itemId)
                    && !isQuestItem(itemId))
                    continue;

                var retainCount = 0;
                if (active != null)
                {
                    foreach (var quest in active)
                    {
                        if (quest == null || quest.QuestId == abandonedQuestId)
                            continue;

                        retainCount = Math.Max(
                            retainCount,
                            GetQuestRequirement(
                                quest.QuestId,
                                itemId,
                                getEventItems,
                                getSeekingItems));
                    }
                }

                result.Add(new QuestGiveupItemRecoveryEntry
                {
                    ItemId = itemId,
                    RetainCount = retainCount,
                });
            }
            return result;
        }

        private static int GetQuestRequirement(
            ushort questId,
            int itemId,
            Func<ushort, IReadOnlyCollection<QuestRewardItem>> getEventItems,
            Func<ushort, IReadOnlyCollection<QuestRewardItem>> getSeekingItems)
        {
            var eventCount = SumItemCount(
                getEventItems(questId),
                itemId);
            var seekingCount = SumItemCount(
                getSeekingItems(questId),
                itemId);
            return Math.Max(eventCount, seekingCount);
        }

        private static int SumItemCount(
            IReadOnlyCollection<QuestRewardItem> items,
            int itemId)
        {
            long total = 0;
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item.ItemId == itemId && item.Count > 0)
                        total += item.Count;
                }
            }
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        private static void AddItemIds(
            ICollection<int> itemIds,
            IReadOnlyCollection<QuestRewardItem> items)
        {
            if (itemIds == null || items == null)
                return;
            foreach (var item in items)
            {
                if (item.ItemId > 0 && item.Count > 0)
                    itemIds.Add(item.ItemId);
            }
        }

        private static bool IsQuestItem(int itemId)
        {
            try
            {
                var metadata = ItemMetadataResolver.Resolve(itemId);
                return metadata != null
                    && metadata.IsStackable
                    && metadata.IsPrimaryStackableFamily("quest");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestGiveupItemRecoveryPolicy] item metadata failed " +
                    $"item={itemId}: {ex.Message}");
                return false;
            }
        }
    }
}
