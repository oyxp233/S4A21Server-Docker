using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestPrerequisiteEvaluator
    {
        private readonly string _connectionString;

        internal QuestPrerequisiteEvaluator(string connectionString)
        {
            _connectionString = connectionString;
        }

        internal bool IsSatisfied(int characterId, int questId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                var active = QuestRepository.LoadActiveQuests(
                    connection,
                    null,
                    characterId);
                return IsSatisfied(
                    connection,
                    null,
                    characterId,
                    questId,
                    active);
            }
        }

        internal bool IsSatisfied(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int questId,
            IReadOnlyCollection<ActiveQuest> activeQuests)
        {
            var definition = GameWorld.QuestPrerequisiteCatalog.Get(questId);
            if (definition == null || !definition.IsValid)
                return false;

            var clearedFlags = QuestRepository.LoadClearedFlags(
                connection,
                transaction,
                characterId);
            var activeQuestIds = new HashSet<int>();
            if (activeQuests != null)
            {
                foreach (var activeQuest in activeQuests)
                {
                    if (activeQuest != null && activeQuest.QuestId > 0)
                        activeQuestIds.Add(activeQuest.QuestId);
                }
            }

            var state = new GameWorld.QuestPrerequisiteEvaluationState(
                new HashSet<int>(clearedFlags.Keys),
                clearedFlags,
                activeQuestIds);
            return definition.Evaluate(state).IsAllowed;
        }
    }
}
