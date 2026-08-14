using System;
using System.Collections.Generic;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class ExpertJobGiveupApplicationService
    {
        private readonly string _connectionString;
        private readonly IExpertJobGiveupStateRepository _states;
        private readonly SqliteCharacterProgressRepository _progress;

        internal ExpertJobGiveupApplicationService(
            string databasePath,
            string schemaFilePath,
            IExpertJobGiveupStateRepository states)
            : this(
                new GameDatabase(databasePath, schemaFilePath),
                states)
        {
        }

        internal ExpertJobGiveupApplicationService(
            IGameDatabase database,
            IExpertJobGiveupStateRepository states)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            _connectionString = database.ConnectionString;
            _states = states ?? throw new ArgumentNullException(nameof(states));
            _progress = new SqliteCharacterProgressRepository(database);
        }

        internal ExpertJobGiveupResult Apply(
            InventoryLease lease,
            Guid sessionId,
            ExpertJobGiveupConfig config)
        {
            if (lease?.Inventory == null
                || sessionId == Guid.Empty
                || config == null
                || !lease.IsOwnedBy(sessionId))
                return ExpertJobGiveupResult.Fail(
                    ExpertJobGiveupResult.ErrorInvalidState);

            var characterId = lease.CharacterId;
            lock (lease.SyncRoot)
            {
                var inventoryMutated = false;
                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            if (!InventoryContext.IsCurrentLease(
                                    lease,
                                    sessionId,
                                    characterId))
                                return ExpertJobGiveupResult.Fail(
                                    ExpertJobGiveupResult.ErrorInvalidState);

                            var persisted = _states.LoadGiveupStateInTransaction(
                                    connection,
                                    transaction,
                                    characterId);
                            if (persisted == null
                                || persisted.ExpertJobType != config.ExpertJobType)
                                return ExpertJobGiveupResult.Fail(
                                    ExpertJobGiveupResult.ErrorInvalidState);
                            if (!config.TryResolveCost(
                                    persisted.GiveupCount,
                                    out var cost,
                                    out var nextGiveupCount))
                                return ExpertJobGiveupResult.Fail(
                                    ExpertJobGiveupResult.ErrorPersistence);
                            if (!QuestRepository.HasAnyClearedQuest(
                                    connection,
                                    transaction,
                                    characterId,
                                    config.ConnectQuestIds))
                                return ExpertJobGiveupResult.Fail(
                                    ExpertJobGiveupResult.ErrorInvalidState);

                            var inventory = lease.Inventory;
                            var currentGold = inventory.CountMainItem(
                                InventoryService.MainVirtualCurrencySlotStart);
                            if (currentGold < cost)
                                return ExpertJobGiveupResult.Fail(
                                    ExpertJobGiveupResult.ErrorInsufficientGold);

                            var result = new ExpertJobGiveupResult
                            {
                                GiveupCount = nextGiveupCount,
                            };
                            if (cost > 0)
                            {
                                inventoryMutated = true;
                                if (!inventory.TryConsumeMainItem(
                                        0,
                                        cost,
                                        out var gold)
                                    || !gold.Success)
                                    throw new InvalidOperationException(
                                        "expert-job giveup gold mutation failed");
                                result.CurrentGold = gold.RemainingCount;
                                result.InventoryChanges.AddRange(gold.Changes);
                            }
                            else
                            {
                                result.CurrentGold = currentGold;
                            }

                            if (config.DeleteItemId > 0)
                            {
                                var deleteCount = inventory.CountMainItem(
                                    config.DeleteItemId);
                                if (deleteCount > 0)
                                {
                                    inventoryMutated = true;
                                    if (!InventoryDeleteService
                                            .TryDeleteMainItemsByTemplateId(
                                                inventory,
                                                config.DeleteItemId,
                                                deleteCount,
                                                out var deleted))
                                        throw new InvalidOperationException(
                                            "expert-job giveup item mutation failed");
                                    result.InventoryChanges.AddRange(deleted);
                                }
                            }

                            var skills = _progress.LoadSkills(
                                connection,
                                transaction,
                                characterId);
                            if (RemoveSkills(skills, config.SkillIds))
                            {
                                _progress.SaveSkillProgress(
                                    connection,
                                    transaction,
                                    characterId,
                                    skills);
                            }

                            QuestRepository.ResetQuestProgress(
                                connection,
                                transaction,
                                characterId,
                                config.ClearQuestIds);
                            if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                                throw new InvalidOperationException(
                                    "expert-job giveup inventory persistence failed");
                            if (!SqliteSubtype0FieldsRepository
                                    .ResetExpertJobInTransaction(
                                        connection,
                                        transaction,
                                        characterId)
                                || !_states.ResetAfterGiveupInTransaction(
                                    connection,
                                    transaction,
                                    characterId,
                                    nextGiveupCount))
                                throw new InvalidOperationException(
                                    "expert-job giveup state reset failed");
                            if (!InventoryContext.IsCurrentLease(
                                    lease,
                                    sessionId,
                                    characterId))
                                throw new InvalidOperationException(
                                    "expert-job giveup inventory lease was replaced");

                            transaction.Commit();
                            inventory.ClearDirtyState();
                            return result;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (inventoryMutated)
                        InventoryRollbackRecoveryService.ReloadOnlineInventory(
                            _connectionString,
                            lease);
                    FileLogger.Log(
                        $"[ExpertJobGiveup] atomic apply failed cid={characterId}: " +
                        ex.Message);
                    return ExpertJobGiveupResult.Fail(
                        ExpertJobGiveupResult.ErrorPersistence);
                }
            }
        }

        private static bool RemoveSkills(
            Game.SelectCharacter.SkillInfoSnapshot skills,
            IReadOnlyCollection<ushort> skillIds)
        {
            if (skills == null || skillIds == null || skillIds.Count == 0)
                return false;

            var targets = new HashSet<ushort>(skillIds);
            var removed = false;
            foreach (var page in skills.Pages)
            {
                if (page == null)
                    continue;
                removed |= page.Entries.RemoveAll(entry =>
                    entry != null && targets.Contains(entry.SkillId)) > 0;
            }
            return removed;
        }

    }
}
