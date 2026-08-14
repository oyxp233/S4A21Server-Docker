using System;
using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    internal static class QuestProgressReducer
    {
        internal static QuestTrigger ApplyClientMutation(
            QuestTrigger current,
            byte triggerType,
            bool increment)
            => current.ApplyClientMutation(triggerType, increment);

        internal static QuestTrigger Complete(QuestTrigger current)
            => current.IsComplete ? current : new QuestTrigger(0);

        internal static QuestTrigger DecrementChannel(
            QuestTrigger current,
            int channelIndex)
        {
            var remaining = current.GetChannel(channelIndex);
            return remaining > 0
                ? current.ReplaceChannel(channelIndex, remaining - 1)
                : current;
        }

        internal static QuestTrigger ApplySeekingItems(
            QuestTrigger current,
            IReadOnlyList<GameWorld.QuestRewardItem> seekItems,
            Func<int, int> getHeldCount)
        {
            if (seekItems == null || seekItems.Count == 0 || getHeldCount == null)
                return current;

            long missingHeld = 0;
            foreach (var item in seekItems)
            {
                if (item.ItemId < 0 || item.Count <= 0)
                    continue;

                var required = Math.Max(1, item.Count);
                var held = Math.Max(0, getHeldCount(item.ItemId));
                missingHeld += Math.Max(0, required - held);
            }

            return new QuestTrigger(
                GameWorld.QuestData.ReplaceTriggerChannel(
                    current.PackedValue,
                    0,
                    missingHeld));
        }
    }
}
