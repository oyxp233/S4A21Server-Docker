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

        internal QuestScenarioModeClearResult Apply(
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
                return QuestScenarioModeClearResult.Fail(questId);
            }

            var quest = GameWorld.QuestData.GetQuestFile(questId);
            if (!IsClearableMainlineQuest(quest, characterLevel))
            {
                FileLogger.Log(
                    $"[QuestScenarioModeClear] rejected quest={questId} " +
                    $"cid={characterId} reason=not-mainline-or-level");
                return QuestScenarioModeClearResult.Fail(questId);
            }

            lock (lease.SyncRoot)
            {
                if (!owner.IsCurrentInventoryOwner()
                    || lease.AccountId != owner.AccountId)
                {
                    return QuestScenarioModeClearResult.Fail(questId);
                }

                var result = QuestScenarioModeClearResult.Fail(questId);
                var inventoryMutated = false;
                using (var connection = new SqliteConnection(
                           _repository.ConnectionString))
                {
                    connection.Open();
                    try
                    {
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            if (!owner.IsCurrentInventoryOwner()
                                || lease.AccountId != owner.AccountId)
                            {
                                return QuestScenarioModeClearResult.Fail(questId);
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
                                    return QuestScenarioModeClearResult.Fail(questId);
                                }

                                if (alreadyCleared)
                                {
                                    FileLogger.Log(
                                        $"[QuestScenarioModeClear] rejected quest={questId} " +
                                        $"cid={characterId} reason=already-cleared");
                                    return QuestScenarioModeClearResult.Fail(questId);
                                }
                            }

                            if (activeQuest != null)
                            {
                                var recoveryPlan = QuestGiveupItemRecoveryPolicy.Build(
                                    active,
                                    questId);
                                foreach (var entry in recoveryPlan)
                                {
                                    var current = lease.Inventory.CountMainItem(entry.ItemId);
                                    var deleteCount = Math.Max(
                                        0,
                                        current - entry.RetainCount);
                                    if (deleteCount <= 0)
                                        continue;

                                    if (!InventoryDeleteService.TryDeleteMainItemsByTemplateId(
                                            lease.Inventory,
                                            entry.ItemId,
                                            deleteCount,
                                            out var deleted))
                                    {
                                        throw new InvalidOperationException(
                                            $"scenario event-item cleanup failed " +
                                            $"item={entry.ItemId} count={deleteCount}");
                                    }

                                    inventoryMutated = true;
                                    result.InventoryChanges.AddRange(deleted);
                                }

                                if (!QuestRepository.TryDeleteActiveQuestCas(
                                        connection,
                                        transaction,
                                        characterId,
                                        questId,
                                        activeQuest.ActivationId,
                                        activeQuest.Version,
                                        activeQuest.TriggerValue))
                                {
                                    throw new InvalidOperationException(
                                        "quest activation changed before scenario clear commit");
                                }
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

                            if (inventoryMutated
                                && !InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                throw new InvalidOperationException(
                                    "scenario event-item cleanup persistence returned false");
                            }

                            transaction.Commit();
                            result.Success = true;
                            FileLogger.Log(
                                $"[QuestScenarioModeClear] cleared quest={questId} " +
                                $"cid={characterId} active={activeQuest != null} " +
                                $"alreadyCleared={alreadyCleared} " +
                                $"eventItemSlots={result.InventoryChanges.Slots.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (inventoryMutated)
                        {
                            try
                            {
                                InventoryRollbackRecoveryService.ReloadOnlineInventory(
                                    _repository.ConnectionString,
                                    lease);
                            }
                            catch (Exception recoveryException)
                            {
                                FileLogger.Log(
                                    $"[QuestScenarioModeClear] inventory rollback reload " +
                                    $"failed quest={questId} cid={characterId}: " +
                                    recoveryException.Message);
                            }
                        }

                        FileLogger.Log(
                            $"[QuestScenarioModeClear] failed before atomic commit " +
                            $"quest={questId} cid={characterId}: {ex.Message}");
                        return QuestScenarioModeClearResult.Fail(questId);
                    }
                }

                if (inventoryMutated)
                    lease.Inventory.ClearDirtyState();
                return result;
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
