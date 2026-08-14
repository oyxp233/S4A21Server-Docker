using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    internal readonly struct ImageCommunicationUseCommand
    {
    }

    internal enum ImageCommunicationUseStatus
    {
        Success = 0,
        InvalidCharacter = 1,
        ConfigurationUnavailable = 2,
        NoMatchingActiveQuest = 3,
    }

    internal sealed class ImageCommunicationUseResult
    {
        internal ImageCommunicationUseStatus Status { get; set; }
        internal ushort QuestId { get; set; }
        internal int NpcIndex { get; set; }
        internal bool Success => Status == ImageCommunicationUseStatus.Success;
    }

    internal sealed class ImageCommunicationApplicationService
    {
        private readonly QuestRepository _repository;

        internal ImageCommunicationApplicationService(string connectionString)
        {
            _repository = new QuestRepository(
                connectionString
                ?? throw new ArgumentNullException(nameof(connectionString)));
        }

        internal ImageCommunicationUseResult Apply(
            int characterId,
            ImageCommunicationUseCommand command)
        {
            _ = command;
            if (characterId <= 0)
                return Rejected(ImageCommunicationUseStatus.InvalidCharacter);

            var definition = GameWorld.ImageCommunicationDefinitionCatalog.Current;
            if (definition.Npcs.Count == 0)
            {
                return Rejected(
                    ImageCommunicationUseStatus.ConfigurationUnavailable);
            }

            return ImageCommunicationRules.Resolve(
                definition,
                _repository.LoadActiveQuests(characterId),
                GameWorld.QuestData.GetQuestFile);
        }

        private static ImageCommunicationUseResult Rejected(
            ImageCommunicationUseStatus status)
            => new ImageCommunicationUseResult { Status = status };
    }

    internal static class ImageCommunicationRules
    {
        internal static ImageCommunicationUseResult Resolve(
            GameWorld.ImageCommunicationDefinition definition,
            IReadOnlyCollection<ActiveQuest> activeQuests,
            Func<int, QuestFile> questResolver)
        {
            if (definition == null || definition.Npcs.Count == 0)
            {
                return new ImageCommunicationUseResult
                {
                    Status = ImageCommunicationUseStatus.ConfigurationUnavailable,
                };
            }

            if (activeQuests == null || questResolver == null)
            {
                return new ImageCommunicationUseResult
                {
                    Status = ImageCommunicationUseStatus.NoMatchingActiveQuest,
                };
            }

            foreach (var entry in definition.Npcs)
            {
                var active = QuestActiveListRules.FindByQuestId(
                    activeQuests,
                    entry.RequiredQuestId);
                if (active == null || active.TriggerValue == 0)
                    continue;

                var quest = questResolver(entry.RequiredQuestId);
                if (!MatchesConfiguredNpc(
                        entry.RequiredQuestId,
                        quest,
                        entry.NpcIndex))
                    continue;

                return new ImageCommunicationUseResult
                {
                    Status = ImageCommunicationUseStatus.Success,
                    QuestId = entry.RequiredQuestId,
                    NpcIndex = entry.NpcIndex,
                };
            }

            return new ImageCommunicationUseResult
            {
                Status = ImageCommunicationUseStatus.NoMatchingActiveQuest,
            };
        }

        private static bool MatchesConfiguredNpc(
            int questId,
            QuestFile quest,
            int npcIndex)
        {
            if (quest == null
                || questId <= 0
                || npcIndex <= 0
                || GameWorld.QuestData.NormalizeQuestTag(quest.Type)
                    != "meet npc")
            {
                return false;
            }

            var hasNpcTarget = false;
            if (quest.CompleteNpcIndex > 0)
            {
                hasNpcTarget = true;
                if (quest.CompleteNpcIndex != npcIndex)
                    return false;
            }

            var targets = GameWorld.QuestData.ParseIntList(quest.IntData);
            if (targets.Count > 0)
            {
                hasNpcTarget = true;
                if (!targets.Contains(npcIndex))
                    return false;
            }

            return hasNpcTarget;
        }
    }
}
