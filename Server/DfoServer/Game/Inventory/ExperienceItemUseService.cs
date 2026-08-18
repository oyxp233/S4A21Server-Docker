using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.ReviveCoin;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    internal sealed class ExperienceItemUseService
    {
        private readonly string _connectionString;
        private readonly IRentalTimeProvider _timeProvider;
        private readonly ExperienceItemCooldownTracker _cooldowns;
        private readonly SqliteCharacterProgressRepository _progressRepository;

        internal ExperienceItemUseService(
            string databasePath,
            string schemaFilePath,
            IRentalTimeProvider timeProvider,
            ExperienceItemCooldownTracker cooldowns)
            : this(
                new GameDatabase(databasePath, schemaFilePath),
                timeProvider,
                cooldowns)
        {
        }

        internal ExperienceItemUseService(
            IGameDatabase database,
            IRentalTimeProvider timeProvider,
            ExperienceItemCooldownTracker cooldowns)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
            _timeProvider = timeProvider
                ?? throw new ArgumentNullException(nameof(timeProvider));
            _cooldowns = cooldowns
                ?? throw new ArgumentNullException(nameof(cooldowns));
            _progressRepository = new SqliteCharacterProgressRepository(database);
        }

        internal ExperienceItemUseResult UseBySlot(
            int characterId,
            int accountId,
            InventoryListType listType,
            short slotIndex,
            ExperienceItemUseLocation location)
        {
            if (listType != InventoryListType.Main || characterId <= 0 || slotIndex < 0)
                return Reject(ExperienceItemUseStatus.NotApplicable, 0, "invalid source slot");

            if (!InventoryContext.TryGetLease(characterId, out var lease)
                || lease.Inventory == null)
                return Reject(ExperienceItemUseStatus.NotApplicable, 0, "online inventory is unavailable");

            if (accountId <= 0 || lease.AccountId != accountId)
                return Reject(ExperienceItemUseStatus.InvalidOwner, 0, "inventory lease/account ownership mismatch");

            var resolvedItemId = 0;
            var sourceConsumed = false;
            ItemCore sourceSnapshot = null;
            InventoryService inventory = null;
            ExperienceItemCooldownReservation cooldownReservation = null;
            try
            {
                lock (lease.SyncRoot)
                {
                    inventory = lease.Inventory;
                    var source = inventory.GetItem(listType, slotIndex);
                    if (source == null || source.IsEmpty)
                        return Reject(ExperienceItemUseStatus.NotApplicable, 0, "source slot is empty");

                    sourceSnapshot = source.Copy();
                    resolvedItemId = sourceSnapshot.ItemId;

                    // 道具42(复活币礼盒): 消耗1个礼盒 → 复活币+1
                    if (resolvedItemId == ReviveCoinService.ConsumableItemId)
                    {
                        InventoryDeleteResult deleteResult = null;
                        var consumeFailed = false;
                        var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                            lease,
                            "revive-coin-consumable",
                            (connection, transaction) =>
                            {
                                var currentInventory = lease.Inventory;
                                if (!InventoryDeleteService.TryConsumeFromSlot(
                                        currentInventory,
                                        listType,
                                        slotIndex,
                                        resolvedItemId,
                                        1,
                                        out deleteResult)
                                    || !deleteResult.Success
                                    || deleteResult.DeletedCount != 1)
                                {
                                    consumeFailed = true;
                                    return false;
                                }

                                var current = currentInventory.CountMainItem(
                                    ReviveCoinService.ItemId);
                                return currentInventory.SetMainVirtualCount(
                                    ReviveCoinService.WalletSlot,
                                    ReviveCoinService.ItemId,
                                    current + 1);
                            });
                        if (!committed)
                        {
                            return Reject(
                                consumeFailed
                                    ? ExperienceItemUseStatus.ConsumeFailed
                                    : ExperienceItemUseStatus.PersistenceFailed,
                                resolvedItemId,
                                consumeFailed
                                    ? "inventory deduction failed"
                                    : "revive coin transaction failed");
                        }

                        return new ExperienceItemUseResult
                        {
                            Status = ExperienceItemUseStatus.Success,
                            AccountId = accountId,
                            ItemTemplateId = resolvedItemId,
                            ConsumedItem = BuildConsumedMutation(
                                listType, slotIndex, sourceSnapshot, deleteResult),
                        };
                    }

                    var definition = ExperienceItemDataProvider.Resolve(resolvedItemId);
                    if (!definition.IsExperienceLike)
                    {
                        return Reject(
                            ExperienceItemUseStatus.UnsupportedDefinition,
                            resolvedItemId,
                            "source item is not ordinary character experience");
                    }

                    if (!UsableCountLimitService.CanUse(
                            _connectionString,
                            characterId,
                            resolvedItemId))
                    {
                        return Reject(
                            ExperienceItemUseStatus.ConsumeFailed,
                            resolvedItemId,
                            "usable count limit reached");
                    }

                    UsableCountLimitState usableCountState = null;
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            var currentSource = inventory.GetItem(listType, slotIndex);
                            if (currentSource == null || currentSource.ItemId != resolvedItemId)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.NotApplicable,
                                    resolvedItemId,
                                    "source slot changed during use");
                            }

                            if (currentSource.Count <= 0)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.ConsumeFailed,
                                    resolvedItemId,
                                    "source stack is empty");
                            }

                            var character = _progressRepository.LoadProgressSnapshot(
                                connection,
                                transaction,
                                characterId);
                            if (character == null
                                || accountId <= 0
                                || character.AccountId != accountId)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.InvalidOwner,
                                    resolvedItemId,
                                    "character/account ownership mismatch");
                            }

                            var usePlan = ExperienceItemUsePolicy.Evaluate(
                                new ExperienceItemUseContext
                                {
                                    Definition = definition,
                                    SourceExpireTime = currentSource.ExpireTime,
                                    NowUnixTime = _timeProvider.UtcNowUnixSeconds(),
                                    Job = character.Job,
                                    Level = character.Level,
                                    Exp = character.Exp,
                                    IsHardcore = character.IsHardcore,
                                    Location = location,
                                });
                            if (!usePlan.Success)
                            {
                                return Reject(
                                    usePlan.Status,
                                    resolvedItemId,
                                    usePlan.Detail);
                            }

                            if (!UsableCountLimitService.TryRecordUseIfLimited(
                                    connection,
                                    transaction,
                                    characterId,
                                    resolvedItemId,
                                    1,
                                    out usableCountState))
                            {
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "usable count transaction failed");
                            }

                            if (!_cooldowns.TryReserve(
                                    characterId,
                                    definition,
                                    out cooldownReservation,
                                    out var remainingCooldown))
                            {
                                return Reject(
                                    ExperienceItemUseStatus.CooldownActive,
                                    resolvedItemId,
                                    $"cooldown remaining={remainingCooldown}ms");
                            }

                            if (!InventoryDeleteService.TryConsumeFromSlot(
                                    inventory,
                                    listType,
                                    slotIndex,
                                    resolvedItemId,
                                    1,
                                    out var deleteResult)
                                || !deleteResult.Success
                                || deleteResult.DeletedCount != 1)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.ConsumeFailed,
                                    resolvedItemId,
                                    "inventory deduction failed");
                            }

                            sourceConsumed = true;
                            var consumedItem = BuildConsumedMutation(
                                listType,
                                slotIndex,
                                sourceSnapshot,
                                deleteResult);

                            var grant = Progression.CharacterExperienceService.GrantInTransaction(
                                connection,
                                transaction,
                                characterId,
                                accountId,
                                character.Level,
                                character.Exp,
                                usePlan.GrantedExp);
                            if (!grant.Persisted)
                            {
                                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "level/experience persistence failed");
                            }

                            Characters.CharacterStatComputer.DecodeGrowType(character.GrowType, out var expFirstGrow, out var expSecondGrow);
                            var syncedSkills = SkillStateService.LoadAndSync(
                                _progressRepository,
                                connection,
                                transaction,
                                characterId,
                                character.Job,
                                grant.NewLevel,
                                character.BonusSp,
                                character.BonusTp,
                                persist: grant.LeveledUp,
                                growType: expFirstGrow,
                                secondGrowType: expSecondGrow);
                            if (syncedSkills.Points == null)
                            {
                                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "skill-point synchronization failed");
                            }

                            if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "inventory persistence failed");
                            }

                            var totalGrowthCapsuleExp = grant.TotalGrowthCapsuleExp;
                            if (grant.HonorExpGain == 0 && grant.NewLevel >= ExpTableProvider.MaxLevel)
                            {
                                totalGrowthCapsuleExp = GrowthCapsuleProgressRepository.LoadTotalExp(
                                    connection,
                                    transaction,
                                    accountId);
                            }

                            var result = new ExperienceItemUseResult
                            {
                                Status = ExperienceItemUseStatus.Success,
                                AccountId = accountId,
                                ItemTemplateId = resolvedItemId,
                                ConsumedItem = consumedItem,
                                PreviousLevel = character.Level,
                                NewLevel = grant.NewLevel,
                                PreviousExp = character.Exp,
                                NewExp = grant.NewExp,
                                GrantedExp = usePlan.GrantedExp,
                                HonorExpGain = grant.HonorExpGain,
                                TotalHonorExp = grant.TotalHonorExp,
                                TotalGrowthCapsuleExp = totalGrowthCapsuleExp,
                                SyncedSkills = syncedSkills.Skills,
                                SkillPoints = SkillStateService.GetProtocolState(
                                    syncedSkills.Skills,
                                    syncedSkills.Points),
                                UsableCountState = usableCountState,
                            };

                            transaction.Commit();
                            inventory.ClearDirtyState();
                            sourceConsumed = false;

                            try
                            {
                                cooldownReservation?.Commit();
                            }
                            catch (Exception ex)
                            {
                                FileLogger.Log(
                                    $"[ExperienceItem] cooldown commit failed after database commit: item={resolvedItemId} cid={characterId} error={ex.Message}");
                            }

                            return result;
                        }
                    }
                }
            }
            catch (SqliteException ex)
            {
                if (sourceConsumed)
                    RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);

                FileLogger.Log(
                    $"[ExperienceItem] SQLite failure item={resolvedItemId} cid={characterId} slot={slotIndex}: {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode} {ex.Message}");
                return Reject(
                    ExperienceItemUseStatus.PersistenceFailed,
                    resolvedItemId,
                    "database transaction failed");
            }
            catch (Exception ex) when (sourceConsumed)
            {
                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                FileLogger.Log(
                    $"[ExperienceItem] inventory mutation rollback item={resolvedItemId} cid={characterId} slot={slotIndex}: {ex.Message}");
                return Reject(
                    ExperienceItemUseStatus.PersistenceFailed,
                    resolvedItemId,
                    "inventory transaction failed");
            }
            finally
            {
                cooldownReservation?.Dispose();
            }
        }

        private static InventoryMutationResult BuildConsumedMutation(
            InventoryListType listType,
            short slotIndex,
            ItemCore source,
            InventoryDeleteResult deleteResult)
        {
            return new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = source != null ? source.ItemId : 0,
                RemainingStackCount = deleteResult != null ? deleteResult.RemainingCount : 0,
                InstanceValue = source != null && InventoryStackRuleService.IsStackable(source)
                    ? (deleteResult != null ? deleteResult.RemainingCount : 0)
                    : (source != null ? source.InstanceValue : 0),
                Durability = source != null ? source.Durability : (ushort)0,
                ExpireTime = source != null ? source.ExpireTime : 0,
                RequestedCount = 1,
                AppliedCount = (short)(deleteResult != null ? deleteResult.DeletedCount : 0),
            };
        }

        private static void RestoreConsumedSource(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            ItemCore sourceSnapshot)
        {
            if (inventory == null || sourceSnapshot == null)
                return;

            inventory.SetItem(listType, slotIndex, sourceSnapshot.Copy());
        }

        private static ExperienceItemUseResult Reject(
            ExperienceItemUseStatus status,
            int itemTemplateId,
            string detail)
            => new ExperienceItemUseResult
            {
                Status = status,
                ItemTemplateId = itemTemplateId,
                Detail = detail,
            };
    }
}
