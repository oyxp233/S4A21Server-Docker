using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Quests
{
    internal readonly struct QuestNotifySelectionCommand
    {
        internal QuestNotifySelectionCommand(IReadOnlyList<int> questIds)
        {
            QuestIds = questIds ?? Array.Empty<int>();
        }

        internal IReadOnlyList<int> QuestIds { get; }
    }

    internal sealed class QuestNotifySelectionService
    {
        internal const int MaxSlots = 4;

        private readonly QuestNotifySelectionRepository _repository;

        internal QuestNotifySelectionService(string connectionString)
        {
            _repository = new QuestNotifySelectionRepository(connectionString);
        }

        internal bool TryReplace(
            int characterId,
            QuestNotifySelectionCommand command)
        {
            if (characterId <= 0
                || command.QuestIds == null
                || command.QuestIds.Count > MaxSlots
                || command.QuestIds.Distinct().Count() != command.QuestIds.Count)
            {
                return false;
            }

            foreach (var questId in command.QuestIds)
            {
                if (questId <= 0 || GameWorld.QuestData.GetQuestFile(questId) == null)
                    return false;
            }

            _repository.Replace(characterId, command.QuestIds);
            return true;
        }
    }
}
