using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using PvfLib;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestScenarioModeClearApplicationService
    {
        private readonly QuestRepository _repository;

        internal QuestScenarioModeClearApplicationService(
            QuestRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        internal bool Apply(
            QuestCommandOwnerContext owner,
            QuestScenarioModeClearCommand command,
            int characterLevel,
            int characterJob,
            int growType)
        {
            var questId = command.QuestId;
            var characterId = owner.CharacterId;
            var lease = owner.InventoryLease;
            if (!owner.IsCurrentInventoryOwner()
                || lease.AccountId != owner.AccountId)
            {
                return false;
            }

            var quest = GameWorld.QuestData.GetQuestFile(questId);
            if (!IsClearableMainlineQuest(quest, characterLevel))
            {
                FileLogger.Log(
                    $"[QuestScenarioModeClear] rejected quest={questId} " +
                    $"cid={characterId} reason=not-mainline-or-level");
                return false;
            }

            lock (lease.SyncRoot)
            {
                if (!owner.IsCurrentInventoryOwner()
                    || lease.AccountId != owner.AccountId)
                {
                    return false;
                }

                using (var connection = new SqliteConnection(
                           _repository.ConnectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        if (!owner.IsCurrentInventoryOwner()
                            || lease.AccountId != owner.AccountId)
                        {
                            return false;
                        }

                        var active = QuestRepository.LoadActiveQuests(
                            connection,
                            transaction,
                            characterId);
                        var activeQuest = QuestActiveListRules.FindByQuestId(
                            active,
                            questId);

                        var clearedFlags = QuestRepository.LoadClearedFlags(
                            connection,
                            transaction,
                            characterId);
                        var alreadyCleared = clearedFlags.TryGetValue(
                            questId,
                            out var clearValue)
                            && clearValue != 0;

                        if (activeQuest == null)
                        {
                            var allowedCreatureKinds =
                                PetCreatureEvolutionRuntimeService
                                    .LoadEligiblePetCreatureEvolutionQuestKinds(
                                        lease.Inventory);
                            var acceptable = GameWorld.QuestData.ComputeAcceptableQuests(
                                characterLevel,
                                characterJob,
                                growType,
                                new HashSet<int>(clearedFlags.Keys),
                                clearedFlags,
                                allowedCreatureKinds);
                            if (!acceptable.Contains(questId))
                            {
                                FileLogger.Log(
                                    $"[QuestScenarioModeClear] rejected quest={questId} " +
                                    $"cid={characterId} reason=not-active-or-acceptable");
                                return false;
                            }

                            if (alreadyCleared)
                            {
                                FileLogger.Log(
                                    $"[QuestScenarioModeClear] rejected quest={questId} " +
                                    $"cid={characterId} reason=already-cleared");
                                return false;
                            }
                        }

                        if (activeQuest != null
                            && !QuestRepository.TryDeleteActiveQuestCas(
                                connection,
                                transaction,
                                characterId,
                                questId,
                                activeQuest.ActivationId,
                                activeQuest.Version,
                                activeQuest.TriggerValue))
                        {
                            FileLogger.Log(
                                $"[QuestScenarioModeClear] rejected quest={questId} " +
                                $"cid={characterId} reason=active-quest-changed");
                            return false;
                        }

                        if (!alreadyCleared)
                        {
                            QuestRepository.MarkQuestCleared(
                                connection,
                                transaction,
                                characterId,
                                questId,
                                flagValue: 1);
                        }

                        transaction.Commit();
                        FileLogger.Log(
                            $"[QuestScenarioModeClear] cleared quest={questId} " +
                            $"cid={characterId} active={activeQuest != null} " +
                            $"alreadyCleared={alreadyCleared}");
                        return true;
                    }
                }
            }
        }

        private static bool IsClearableMainlineQuest(
            QuestFile quest,
            int characterLevel)
        {
            if (quest == null || quest.IsEvent || characterLevel <= 0)
                return false;

            if (!string.Equals(
                    GameWorld.QuestData.NormalizeQuestTag(quest.Grade),
                    "epic",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var minimumLevel = quest.Level != null && quest.Level.Length > 0
                ? quest.Level[0]
                : 1;
            return minimumLevel < characterLevel;
        }
    }
}
