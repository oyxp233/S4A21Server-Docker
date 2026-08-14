using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    internal static class QuestDungeonActivationPolicy
    {
        internal static bool IsAcceptanceAllowed(
            int questId,
            IReadOnlyCollection<ActiveQuest> activeQuests)
        {
            if (questId <= 0 || activeQuests == null)
                return true;

            foreach (var activeQuest in activeQuests)
            {
                if (activeQuest != null
                    && GameWorld.QuestDungeonPresentationPlanner
                        .SharesPhysicalSlot(questId, activeQuest.QuestId))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
