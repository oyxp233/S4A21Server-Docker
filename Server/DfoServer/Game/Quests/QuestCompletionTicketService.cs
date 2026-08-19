using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Game.TitleBook;
using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.Game.Quests
{
    internal enum QuestCompletionTicketActionKind
    {
        None,
        AnyQuestClear,
        AchievementQuestClear,
        FirstAwakenClear,
        SecondAwakenClear,
    }

    internal enum QuestCompletionTicketUseStatus
    {
        NotApplicable,
        Success,
        InvalidOwner,
        MissingSource,
        NoEligibleQuest,
        ConsumeFailed,
        PersistenceFailed,
    }

    internal sealed class QuestCompletionTicketUseRequest
    {
        public Guid SessionId { get; set; }

        public int CharacterId { get; set; }

        public int AccountId { get; set; }

        public InventoryLease Lease { get; set; }

        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int ExpectedItemTemplateId { get; set; }
    }

    internal sealed class QuestCompletionTicketUseResult
    {
        public QuestCompletionTicketUseStatus Status { get; set; }

        public QuestCompletionTicketActionKind ActionKind { get; set; }

        public int ItemTemplateId { get; set; }

        public InventoryMutationResult ConsumedItem { get; set; }

        public List<ushort> CompletedQuestIds { get; } = new List<ushort>();

        public List<QuestFinishResult> FinishResults { get; } =
            new List<QuestFinishResult>();

        public List<AchievementTriggerResult> AchievementResults { get; } =
            new List<AchievementTriggerResult>();

        public string Detail { get; set; } = string.Empty;

        public bool Handled => Status != QuestCompletionTicketUseStatus.NotApplicable;

        public bool Success => Status == QuestCompletionTicketUseStatus.Success;
    }

    internal sealed class QuestCompletionTicketService
    {
        private static readonly Lazy<TitleBookStaticDataProvider> TitleBookData =
            new Lazy<TitleBookStaticDataProvider>(
                TitleBookStaticDataProvider.LoadDefault);

        private static readonly Lazy<TitleBookMutationService>
            TitleBookMutations =
                new Lazy<TitleBookMutationService>(
                    () => new TitleBookMutationService());

        private readonly string _connectionString;

        internal QuestCompletionTicketService(string connectionString)
        {
            _connectionString = connectionString;
        }

        internal QuestCompletionTicketUseResult UseBySlot(
            QuestCompletionTicketUseRequest request)
        {
            if (!TryResolveApplicableTicket(
                    request,
                    out var sourceItemTemplateId,
                    out var stackable,
                    out var actionKind,
                    out var actionName,
                    out var precheckResult))
            {
                return precheckResult;
            }

            var result = new QuestCompletionTicketUseResult
            {
                Status = QuestCompletionTicketUseStatus.PersistenceFailed,
                ActionKind = actionKind,
                ItemTemplateId = sourceItemTemplateId,
            };

            var lease = request.Lease;
            var database = lease.Inventory?.Database;
            if (database == null || string.IsNullOrWhiteSpace(_connectionString))
            {
                result.Detail = "inventory database unavailable";
                return result;
            }

            var inventoryMutated = false;
            try
            {
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: true))
                {
                    using (InventoryUidAllocationContext.Enter(
                               connection,
                               transaction))
                    {
                        lock (lease.SyncRoot)
                        {
                            if (!InventoryContext.IsCurrentLease(
                                    lease,
                                    request.SessionId,
                                    request.CharacterId)
                                || lease.AccountId != request.AccountId)
                            {
                                result.Status =
                                    QuestCompletionTicketUseStatus.InvalidOwner;
                                result.Detail = "inventory owner changed";
                                return result;
                            }

                            if (!TryLoadCharacterState(
                                    connection,
                                    transaction,
                                    request.CharacterId,
                                    request.AccountId,
                                    out var character))
                            {
                                result.Status =
                                    QuestCompletionTicketUseStatus.InvalidOwner;
                                result.Detail = "character owner mismatch";
                                return result;
                            }

                            if (!InventoryDeleteService
                                    .CanUseStackableForClient(
                                        lease.Inventory,
                                        request.ListType,
                                        request.SlotIndex,
                                        sourceItemTemplateId,
                                        out _))
                            {
                                result.Status =
                                    QuestCompletionTicketUseStatus.MissingSource;
                                result.Detail = "source stackable unavailable";
                                return result;
                            }

                            var completion = new TicketCompletionContext(
                                result,
                                character,
                                lease.Inventory);

                            if (!HasEligibleTarget(
                                    connection,
                                    transaction,
                                    request.CharacterId,
                                    actionKind,
                                    stackable,
                                    completion))
                            {
                                result.Status =
                                    QuestCompletionTicketUseStatus.NoEligibleQuest;
                                result.Detail = "no acceptable or active target quest";
                                return result;
                            }

                            if (!UsableCountLimitService.TryRecordUseIfLimited(
                                    connection,
                                    transaction,
                                    request.CharacterId,
                                    sourceItemTemplateId,
                                    1,
                                    out var usableCountState))
                            {
                                result.Status =
                                    QuestCompletionTicketUseStatus.ConsumeFailed;
                                result.Detail = "usable count limit reached";
                                return result;
                            }

                            if (!InventoryDeleteService.TryUseStackableForClient(
                                    lease.Inventory,
                                    request.ListType,
                                    request.SlotIndex,
                                    sourceItemTemplateId,
                                    out var consumed)
                                || consumed == null)
                            {
                                result.Status =
                                    QuestCompletionTicketUseStatus.ConsumeFailed;
                                result.Detail = "source consume failed";
                                return result;
                            }

                            consumed.UsableCountState = usableCountState;
                            result.ConsumedItem = consumed;
                            inventoryMutated = true;

                            var completedCount = CompleteTargets(
                                connection,
                                transaction,
                                request.CharacterId,
                                lease,
                                actionKind,
                                stackable,
                                completion);
                            if (completedCount <= 0)
                                throw new InvalidOperationException(
                                    "eligible quest disappeared before completion");

                            if (!InventoryPersistenceService
                                    .SaveDirtyInTransaction(
                                        connection,
                                        transaction,
                                        lease))
                            {
                                throw new InvalidOperationException(
                                    "inventory persistence returned false");
                            }

                            transaction.Commit();
                            lease.Inventory.ClearDirtyState();
                            result.Status =
                                QuestCompletionTicketUseStatus.Success;
                            result.Detail = actionName;
                            return result;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestCompletionTicket] failed item=0x{sourceItemTemplateId:X8} " +
                    $"cid={request.CharacterId} slot={request.SlotIndex} " +
                    $"action={actionName}: {ex.Message}");
                if (inventoryMutated)
                    TryReloadInventory(lease);
                result.Status =
                    QuestCompletionTicketUseStatus.PersistenceFailed;
                result.Detail = ex.Message;
                return result;
            }
        }

        private static bool TryResolveApplicableTicket(
            QuestCompletionTicketUseRequest request,
            out int sourceItemTemplateId,
            out StackableItemFile stackable,
            out QuestCompletionTicketActionKind actionKind,
            out string actionName,
            out QuestCompletionTicketUseResult result)
        {
            sourceItemTemplateId = 0;
            stackable = null;
            actionKind = QuestCompletionTicketActionKind.None;
            actionName = string.Empty;
            result = new QuestCompletionTicketUseResult
            {
                Status = QuestCompletionTicketUseStatus.NotApplicable,
            };

            var lease = request?.Lease;
            if (lease?.Inventory == null)
                return false;

            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        request.SessionId,
                        request.CharacterId))
                {
                    return false;
                }

                var source = lease.Inventory.GetItem(
                    request.ListType,
                    request.SlotIndex);
                if (source == null
                    || source.IsEmpty
                    || source.ItemId <= 0
                    || (LooksLikeItemTemplateId(request.ExpectedItemTemplateId)
                        && source.ItemId != request.ExpectedItemTemplateId))
                {
                    return false;
                }

                sourceItemTemplateId = source.ItemId;
            }

            stackable = StackableItemProvider.Load(sourceItemTemplateId);
            actionName = StackableItemProvider.NormalizeType(
                stackable?.ActionTypeName);
            if (!TryResolveActionKind(actionName, out actionKind))
                return false;

            result = new QuestCompletionTicketUseResult
            {
                Status = QuestCompletionTicketUseStatus.PersistenceFailed,
                ActionKind = actionKind,
                ItemTemplateId = sourceItemTemplateId,
                Detail = actionName,
            };
            return true;
        }

        private static bool TryResolveActionKind(
            string actionName,
            out QuestCompletionTicketActionKind kind)
        {
            switch ((actionName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "[any quest clear]":
                    kind = QuestCompletionTicketActionKind.AnyQuestClear;
                    return true;
                case "[achievement quest clear]":
                    kind = QuestCompletionTicketActionKind.AchievementQuestClear;
                    return true;
                case "[first awaken clear]":
                case "[first awakening clear]":
                    kind = QuestCompletionTicketActionKind.FirstAwakenClear;
                    return true;
                case "[second awaken clear]":
                case "[second awakening clear]":
                    kind = QuestCompletionTicketActionKind.SecondAwakenClear;
                    return true;
                default:
                    kind = QuestCompletionTicketActionKind.None;
                    return false;
            }
        }

        private static bool LooksLikeItemTemplateId(int value)
            => value >= 100000;

        private static bool TryLoadCharacterState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            out TicketCharacterState state)
        {
            state = null;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT account_id, level, job, grow_type, exp
FROM characters
WHERE character_id = @cid AND delete_flag = 0;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;
                    var ownerAccountId = reader.GetInt32(0);
                    if (ownerAccountId != accountId)
                        return false;

                    var expValue = reader.GetInt64(4);
                    state = new TicketCharacterState
                    {
                        AccountId = ownerAccountId,
                        Level = Math.Max(1, Math.Min(255, reader.GetInt32(1))),
                        Job = reader.GetInt32(2),
                        GrowType = reader.GetInt32(3),
                        Exp = (uint)Math.Max(0L, Math.Min(uint.MaxValue, expValue)),
                    };
                    return true;
                }
            }
        }

        private static bool HasEligibleTarget(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            QuestCompletionTicketActionKind actionKind,
            StackableItemFile stackable,
            TicketCompletionContext context)
        {
            var visible = BuildVisibleQuestSet(
                connection,
                transaction,
                characterId,
                context);

            if (actionKind == QuestCompletionTicketActionKind.AnyQuestClear)
            {
                foreach (var questId in EnumerateActionQuestIds(stackable))
                {
                    if (visible.Contains(questId))
                        return true;
                }
                return false;
            }

            return TryFindNextDynamicQuest(
                visible,
                actionKind,
                context,
                out _);
        }

        private static int CompleteTargets(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryLease lease,
            QuestCompletionTicketActionKind actionKind,
            StackableItemFile stackable,
            TicketCompletionContext context)
        {
            switch (actionKind)
            {
                case QuestCompletionTicketActionKind.AnyQuestClear:
                    return CompleteListedTargets(
                        connection,
                        transaction,
                        characterId,
                        lease,
                        stackable,
                        context);
                case QuestCompletionTicketActionKind.AchievementQuestClear:
                case QuestCompletionTicketActionKind.FirstAwakenClear:
                case QuestCompletionTicketActionKind.SecondAwakenClear:
                    return CompleteDynamicTargets(
                        connection,
                        transaction,
                        characterId,
                        lease,
                        actionKind,
                        context);
                default:
                    return 0;
            }
        }

        private static int CompleteListedTargets(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryLease lease,
            StackableItemFile stackable,
            TicketCompletionContext context)
        {
            var completed = 0;
            foreach (var questId in EnumerateActionQuestIds(stackable))
            {
                if (context.CompletedThisTicket.Contains(questId))
                    continue;

                var visible = BuildVisibleQuestSet(
                    connection,
                    transaction,
                    characterId,
                    context);
                if (!visible.Contains(questId))
                    continue;

                CompleteQuest(
                    connection,
                    transaction,
                    characterId,
                    lease,
                    questId,
                    context);
                completed++;
            }

            return completed;
        }

        private static int CompleteDynamicTargets(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryLease lease,
            QuestCompletionTicketActionKind actionKind,
            TicketCompletionContext context)
        {
            var completed = 0;
            var guard = Math.Max(1, QuestCatalog.OrderedIds.Count);
            for (var iteration = 0; iteration < guard; iteration++)
            {
                var visible = BuildVisibleQuestSet(
                    connection,
                    transaction,
                    characterId,
                    context);
                if (!TryFindNextDynamicQuest(
                        visible,
                        actionKind,
                        context,
                        out var questId))
                {
                    return completed;
                }

                CompleteQuest(
                    connection,
                    transaction,
                    characterId,
                    lease,
                    questId,
                    context);
                completed++;
            }

            FileLogger.Log(
                $"[QuestCompletionTicket] dynamic clear stopped by guard: " +
                $"cid={characterId} action={actionKind} completed={completed}");
            return completed;
        }

        private static bool TryFindNextDynamicQuest(
            VisibleQuestSet visible,
            QuestCompletionTicketActionKind actionKind,
            TicketCompletionContext context,
            out ushort questId)
        {
            questId = 0;
            foreach (var activeQuestId in visible.ActiveQuestIds)
            {
                if (MatchesDynamicTarget(
                        activeQuestId,
                        actionKind,
                        context))
                {
                    questId = activeQuestId;
                    return true;
                }
            }

            foreach (var acceptableQuestId in visible.AcceptableQuestIds)
            {
                if (visible.ActiveQuestIdSet.Contains(acceptableQuestId)
                    || !MatchesDynamicTarget(
                        acceptableQuestId,
                        actionKind,
                        context))
                {
                    continue;
                }

                questId = acceptableQuestId;
                return true;
            }

            return false;
        }

        private static bool MatchesDynamicTarget(
            ushort questId,
            QuestCompletionTicketActionKind actionKind,
            TicketCompletionContext context)
        {
            if (questId == 0 || context.CompletedThisTicket.Contains(questId))
                return false;

            var quest = QuestData.GetQuestFile(questId);
            if (quest == null)
                return false;

            switch (actionKind)
            {
                case QuestCompletionTicketActionKind.AchievementQuestClear:
                    return string.Equals(
                            QuestData.NormalizeQuestTag(quest.Grade),
                            "achievement",
                            StringComparison.OrdinalIgnoreCase)
                        && TitleBookData.Value.TryFindByQuestId(
                            questId,
                            out var titleBookSlot)
                        && titleBookSlot.Category == 0;
                case QuestCompletionTicketActionKind.FirstAwakenClear:
                    return quest.JobChangeQuestValue == 2;
                case QuestCompletionTicketActionKind.SecondAwakenClear:
                    return quest.JobChangeQuestValue == 3;
                default:
                    return false;
            }
        }

        private static void CompleteQuest(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryLease lease,
            ushort questId,
            TicketCompletionContext context)
        {
            if (context.CompletedThisTicket.Contains(questId))
                return;

            if (!QuestData.TryResolveCompletionDefinition(
                    questId,
                    out var definition,
                    out var definitionError))
            {
                throw new InvalidOperationException(
                    $"invalid quest completion definition quest={questId}: " +
                    definitionError);
            }

            QuestRepository.DeleteActiveQuestsByQuestId(
                connection,
                transaction,
                characterId,
                questId);

            var rewardDefinition = definition.RewardDefinition;
            var chainType = rewardDefinition.ChainType;
            var growNumber = rewardDefinition.RewardParameter;
            var petEvolution = PetCreatureEvolutionResult.Noop;

            ApplySpecialReward(
                connection,
                transaction,
                characterId,
                lease,
                questId,
                rewardDefinition,
                context,
                ref petEvolution);

            if (!definition.IsRepeatable)
            {
                QuestRepository.MarkQuestCleared(
                    connection,
                    transaction,
                    characterId,
                    questId,
                    flagValue: 1);
            }

            QuestClearProgressRules.SynchronizeActiveParents(
                connection,
                transaction,
                characterId);

            context.CompletedThisTicket.Add(questId);
            context.Result.CompletedQuestIds.Add(questId);
            context.Result.FinishResults.Add(new QuestFinishResult
            {
                QuestId = questId,
                FinishType = QuestCompletionApplicationService
                    .ProjectFinishType(definition.Type),
                Exp = 0,
                NewLevel = (byte)Math.Max(
                    1,
                    Math.Min(byte.MaxValue, context.Character.Level)),
                NewExp = context.Character.Exp,
                ChainType = chainType,
                GrowNumber = growNumber,
                PetCreatureEvolution = petEvolution,
            });
        }

        private static void ApplySpecialReward(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryLease lease,
            ushort questId,
            QuestRewardDefinition rewardDefinition,
            TicketCompletionContext context,
            ref PetCreatureEvolutionResult petEvolution)
        {
            switch (rewardDefinition.ChainType)
            {
                case 1:
                case 2:
                    QuestCompletionApplicationService.UpdateGrowType(
                        connection,
                        transaction,
                        characterId,
                        rewardDefinition.ChainType,
                        rewardDefinition.RewardParameter);
                    context.Character.GrowType = ReadCharacterInt(
                        connection,
                        transaction,
                        characterId,
                        "grow_type",
                        context.Character.GrowType);
                    return;
                case 10:
                case 25:
                    petEvolution = PetCreatureEvolutionRuntimeService
                        .TryCompletePetCreatureEvolutionQuest(
                            context.Inventory,
                            rewardDefinition.CreatureKind,
                            rewardDefinition.CreatureLevel,
                            rewardDefinition.RewardParameter);
                    if (!petEvolution.Changed)
                    {
                        throw new InvalidOperationException(
                            $"pet creature evolution reward failed quest={questId}");
                    }
                    return;
                case 20:
                    QuestCompletionApplicationService.UpdateExpertJob(
                        connection,
                        transaction,
                        characterId,
                        rewardDefinition.RewardParameter);
                    return;
                case QuestData.ChainTypeSlotExpansion:
                    QuestCompletionApplicationService.UpdateSlotExpansion(
                        connection,
                        transaction,
                        characterId,
                        rewardDefinition.RewardParameter);
                    return;
                case QuestData.ChainTypeTitle:
                    var achievement = TitleBookMutations.Value
                        .TriggerAchievement(
                            lease,
                            questId,
                            ushort.MaxValue,
                            ushort.MaxValue,
                            ushort.MaxValue);
                    if (achievement == null
                        || !achievement.Success
                        || !achievement.Completed
                        || achievement.TitleItemId <= 0)
                    {
                        throw new InvalidOperationException(
                            $"title reward trigger failed quest={questId}");
                    }
                    context.Result.AchievementResults.Add(achievement);
                    return;
                default:
                    LogUnsupportedSpecialReward(questId, rewardDefinition);
                    return;
            }
        }

        private static void LogUnsupportedSpecialReward(
            ushort questId,
            QuestRewardDefinition rewardDefinition)
        {
            if (rewardDefinition.Kind == QuestRewardKind.Item
                || rewardDefinition.Kind == QuestRewardKind.Title)
            {
                return;
            }

            FileLogger.Log(
                $"[QuestCompletionTicket] special reward has no ticket side effect: " +
                $"quest={questId} kind={rewardDefinition.Kind} " +
                $"chain={rewardDefinition.ChainType} " +
                $"param={rewardDefinition.RewardParameter}");
        }

        private static VisibleQuestSet BuildVisibleQuestSet(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            TicketCompletionContext context)
        {
            var active = QuestRepository.LoadActiveQuests(
                connection,
                transaction,
                characterId);
            var clearedFlags = QuestRepository.LoadClearedFlags(
                connection,
                transaction,
                characterId);
            var clearedQuestIds = new HashSet<int>(clearedFlags.Keys);
            var allowedCreatureKinds =
                PetCreatureEvolutionRuntimeService
                    .LoadEligiblePetCreatureEvolutionQuestKinds(
                        context.Inventory);
            var acceptable = QuestData.ComputeAcceptableQuests(
                context.Character.Level,
                context.Character.Job,
                context.Character.GrowType,
                clearedQuestIds,
                clearedFlags,
                allowedCreatureKinds);

            return new VisibleQuestSet(
                active.Select(quest => quest.QuestId).ToList(),
                acceptable);
        }

        private static IEnumerable<ushort> EnumerateActionQuestIds(
            StackableItemFile stackable)
        {
            if (stackable?.ActionTypeParams == null)
                yield break;

            foreach (var rawQuestId in stackable.ActionTypeParams)
            {
                if (rawQuestId > 0 && rawQuestId <= ushort.MaxValue)
                    yield return (ushort)rawQuestId;
            }
        }

        private static int ReadCharacterInt(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            string column,
            int fallback)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    $"SELECT {column} FROM characters WHERE character_id = @cid";
                command.Parameters.AddWithValue("@cid", characterId);
                var value = command.ExecuteScalar();
                return value != null ? Convert.ToInt32(value) : fallback;
            }
        }

        private static void TryReloadInventory(InventoryLease lease)
        {
            try
            {
                var connectionString = lease?.Inventory?.Database?.ConnectionString;
                if (string.IsNullOrWhiteSpace(connectionString))
                    return;

                InventoryRollbackRecoveryService.ReloadOnlineInventory(
                    connectionString,
                    lease);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestCompletionTicket] inventory reload failed: " +
                    $"{ex.Message}");
            }
        }

        private sealed class TicketCharacterState
        {
            public int AccountId { get; set; }

            public int Level { get; set; }

            public int Job { get; set; }

            public int GrowType { get; set; }

            public uint Exp { get; set; }
        }

        private sealed class TicketCompletionContext
        {
            internal TicketCompletionContext(
                QuestCompletionTicketUseResult result,
                TicketCharacterState character,
                InventoryService inventory)
            {
                Result = result
                    ?? throw new ArgumentNullException(nameof(result));
                Character = character
                    ?? throw new ArgumentNullException(nameof(character));
                Inventory = inventory
                    ?? throw new ArgumentNullException(nameof(inventory));
            }

            internal QuestCompletionTicketUseResult Result { get; }

            internal TicketCharacterState Character { get; }

            internal InventoryService Inventory { get; }

            internal HashSet<ushort> CompletedThisTicket { get; } =
                new HashSet<ushort>();
        }

        private sealed class VisibleQuestSet
        {
            internal VisibleQuestSet(
                IReadOnlyList<ushort> activeQuestIds,
                IReadOnlyList<ushort> acceptableQuestIds)
            {
                ActiveQuestIds = new List<ushort>(
                    activeQuestIds ?? Array.Empty<ushort>());
                AcceptableQuestIds = new List<ushort>(
                    acceptableQuestIds ?? Array.Empty<ushort>());
                ActiveQuestIdSet = new HashSet<ushort>(ActiveQuestIds);
                AcceptableQuestIdSet = new HashSet<ushort>(
                    AcceptableQuestIds);
            }

            internal IReadOnlyList<ushort> ActiveQuestIds { get; }

            internal IReadOnlyList<ushort> AcceptableQuestIds { get; }

            internal HashSet<ushort> ActiveQuestIdSet { get; }

            private HashSet<ushort> AcceptableQuestIdSet { get; }

            internal bool Contains(ushort questId)
                => ActiveQuestIdSet.Contains(questId)
                    || AcceptableQuestIdSet.Contains(questId);
        }
    }
}
