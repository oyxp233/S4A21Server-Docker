using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class DailyChallengeService
    {
        private static readonly ConcurrentDictionary<long, byte> MissingEntryWarnings =
            new ConcurrentDictionary<long, byte>();

        private readonly DailyChallengeRepository _repository;
        private readonly string _connectionString;

        internal DailyChallengeService(string connectionString)
        {
            _connectionString = connectionString;
            _repository = new DailyChallengeRepository(connectionString);
        }

        internal bool TryHandleSetTrigger(
            int characterId,
            byte[] body,
            out DailyChallengeSetTriggerResult result)
        {
            result = null;
            if (body == null || body.Length < 3)
                return false;

            var questId = BitConverter.ToUInt16(body, 0);
            if (!QuestData.IsDailyChallengeQuest(questId))
                return false;

            var triggerType = body[2];
            var isIncrement = body.Length >= 4 && body[3] != 0;
            var stored = _repository.ApplyMutation(
                characterId,
                questId,
                (target, current) => ApplyMutation(target, current, triggerType, isIncrement));

            if (!stored.Found)
            {
                var warningKey = ((long)characterId << 32) | questId;
                if (MissingEntryWarnings.TryAdd(warningKey, 0))
                {
                    FileLogger.Log(
                        $"[DailyChallenge] configured quest missing from character ledger: "
                        + $"cid={characterId} quest={questId}; returning unavailable state");
                }
            }
            else if (stored.Changed)
            {
                FileLogger.Log(
                    $"[DailyChallenge] SET_TRIGGER cid={characterId} quest={questId} "
                    + $"group={stored.GroupIndex} entry={stored.EntryIndex} "
                    + $"type=0x{triggerType:X2} inc={isIncrement} "
                    + $"remaining={stored.PreviousValue}->{stored.CurrentValue} "
                    + $"target={stored.TargetValue}");
            }

            result = new DailyChallengeSetTriggerResult(
                new QuestSetTriggerResult
                {
                    QuestId = questId,
                    PreviousTriggerValue = stored.PreviousValue,
                    TriggerValue = stored.CurrentValue,
                },
                stored.Snapshot,
                stored.Found,
                stored.Changed);
            return true;
        }

        internal DailyChallengeResetResult ResetCharacter(int characterId)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));

            var result = _repository.ResetCharacter(characterId);
            if (result.ChangedEntries > 0 || result.ClearedClaims > 0)
            {
                FileLogger.Log(
                    $"[DailyChallenge] reset cid={characterId} "
                    + $"entries={result.ChangedEntries} claims={result.ClearedClaims}");
            }

            return result;
        }

        internal DailyChallengeRewardClaimResult ClaimReward(
            int characterId,
            int characterLevel,
            int groupIndex,
            InventoryLease lease)
        {
            if (characterId <= 0
                || groupIndex < 0
                || groupIndex >= 6
                || lease == null
                || lease.CharacterId != characterId
                || lease.Inventory == null)
            {
                return DailyChallengeRewardClaimResult.Rejected(
                    DailyChallengeRewardClaimStatus.InvalidRequest,
                    groupIndex,
                    null);
            }

            lock (lease.SyncRoot)
            {
                SelectCharacterInitializationSnapshot snapshot = null;
                RewardInventoryRollback rollback = null;
                InventoryRewardGrantBatchResult grant = null;
                var inventoryMutated = false;

                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            var state = _repository.LoadRewardState(
                                connection,
                                transaction,
                                characterId,
                                groupIndex);
                            snapshot = DailyChallengeRepository.LoadSnapshot(
                                connection,
                                transaction,
                                characterId);

                            if (!state.Found)
                            {
                                return DailyChallengeRewardClaimResult.Rejected(
                                    DailyChallengeRewardClaimStatus.GroupUnavailable,
                                    groupIndex,
                                    snapshot);
                            }

                            if (state.Claimed)
                            {
                                transaction.Commit();
                                return DailyChallengeRewardClaimResult.AlreadyClaimed(
                                    groupIndex,
                                    snapshot);
                            }

                            if (!DailyChallengeData.TryResolveReward(
                                    groupIndex,
                                    characterLevel,
                                    state.EntryCount,
                                    out var reward))
                            {
                                return DailyChallengeRewardClaimResult.Rejected(
                                    DailyChallengeRewardClaimStatus.RewardUnavailable,
                                    groupIndex,
                                    snapshot);
                            }

                            if (state.CompletedEntryCount < reward.RequiredCompletionCount)
                            {
                                return DailyChallengeRewardClaimResult.Rejected(
                                    DailyChallengeRewardClaimStatus.Incomplete,
                                    groupIndex,
                                    snapshot,
                                    reward,
                                    state.CompletedEntryCount);
                            }

                            var requests = new List<InventoryRewardGrantRequest>
                            {
                                InventoryRewardGrantRequest.Create(
                                    reward.ItemId,
                                    reward.ItemCount,
                                    ItemCreateReason.QuestReward),
                            };
                            if (!InventoryRewardGrantService.TryPlanBatch(
                                    lease.Inventory,
                                    requests,
                                    out var plan))
                            {
                                return DailyChallengeRewardClaimResult.Rejected(
                                    DailyChallengeRewardClaimStatus.InventoryFull,
                                    groupIndex,
                                    snapshot,
                                    reward,
                                    state.CompletedEntryCount);
                            }

                            rollback = RewardInventoryRollback.Capture(
                                lease.Inventory,
                                plan.Entries[0]);
                            if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                                    lease.Inventory,
                                    plan,
                                    out grant))
                            {
                                RewardInventoryRollback.Restore(lease.Inventory, rollback, grant);
                                return DailyChallengeRewardClaimResult.Rejected(
                                    DailyChallengeRewardClaimStatus.InventoryFull,
                                    groupIndex,
                                    snapshot,
                                    reward,
                                    state.CompletedEntryCount);
                            }

                            inventoryMutated = true;
                            if (!_repository.TryMarkRewardClaimed(
                                    connection,
                                    transaction,
                                    characterId,
                                    groupIndex))
                            {
                                RewardInventoryRollback.Restore(lease.Inventory, rollback, grant);
                                inventoryMutated = false;
                                snapshot = DailyChallengeRepository.LoadSnapshot(
                                    connection,
                                    transaction,
                                    characterId);
                                transaction.Commit();
                                return DailyChallengeRewardClaimResult.AlreadyClaimed(
                                    groupIndex,
                                    snapshot);
                            }

                            if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                throw new InvalidOperationException(
                                    "daily challenge inventory persistence returned false");
                            }

                            snapshot = DailyChallengeRepository.LoadSnapshot(
                                connection,
                                transaction,
                                characterId);
                            transaction.Commit();
                            lease.Inventory.ClearDirtyState();
                            inventoryMutated = false;

                            FileLogger.Log(
                                $"[DailyChallenge] REWARD claimed cid={characterId} "
                                + $"group={groupIndex} completed={state.CompletedEntryCount}/"
                                + $"{reward.RequiredCompletionCount} item={reward.ItemId} "
                                + $"count={reward.ItemCount}");
                            return DailyChallengeRewardClaimResult.Succeeded(
                                groupIndex,
                                snapshot,
                                reward,
                                state.CompletedEntryCount,
                                grant?.Changes);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (inventoryMutated)
                        RewardInventoryRollback.Restore(lease.Inventory, rollback, grant);

                    FileLogger.Log(
                        $"[DailyChallenge] REWARD failed cid={characterId} "
                        + $"group={groupIndex}: {ex.Message}");
                    return DailyChallengeRewardClaimResult.Rejected(
                        DailyChallengeRewardClaimStatus.PersistenceFailed,
                        groupIndex,
                        snapshot);
                }
            }
        }

        private static uint ApplyMutation(
            uint target,
            uint storedCurrent,
            byte triggerType,
            bool isIncrement)
        {
            var current = Math.Min(target, storedCurrent);
            var next = new QuestTrigger(current)
                .ApplyClientMutation(triggerType, isIncrement)
                .PackedValue;
            return Math.Min(target, next);
        }
    }

    internal enum DailyChallengeRewardClaimStatus
    {
        Success,
        AlreadyClaimed,
        InvalidRequest,
        GroupUnavailable,
        RewardUnavailable,
        Incomplete,
        InventoryFull,
        PersistenceFailed,
    }

    internal sealed class DailyChallengeRewardClaimResult
    {
        internal DailyChallengeRewardClaimStatus Status { get; private set; }
        internal int GroupIndex { get; private set; }
        internal int ItemId { get; private set; }
        internal int ItemCount { get; private set; }
        internal int RequiredCompletionCount { get; private set; }
        internal int CompletedEntryCount { get; private set; }
        internal SelectCharacterInitializationSnapshot Snapshot { get; private set; }
        internal InventoryMutationSet Changes { get; private set; } = new InventoryMutationSet();
        internal bool ClientSuccess => Status == DailyChallengeRewardClaimStatus.Success
            || Status == DailyChallengeRewardClaimStatus.AlreadyClaimed;
        internal bool GrantedReward => Status == DailyChallengeRewardClaimStatus.Success;

        internal static DailyChallengeRewardClaimResult Succeeded(
            int groupIndex,
            SelectCharacterInitializationSnapshot snapshot,
            DailyChallengeRewardDefinition reward,
            int completed,
            InventoryMutationSet changes)
        {
            var result = Create(
                DailyChallengeRewardClaimStatus.Success,
                groupIndex,
                snapshot,
                reward,
                completed);
            result.Changes.AddRange(changes);
            return result;
        }

        internal static DailyChallengeRewardClaimResult AlreadyClaimed(
            int groupIndex,
            SelectCharacterInitializationSnapshot snapshot) =>
            Create(
                DailyChallengeRewardClaimStatus.AlreadyClaimed,
                groupIndex,
                snapshot,
                null,
                0);

        internal static DailyChallengeRewardClaimResult Rejected(
            DailyChallengeRewardClaimStatus status,
            int groupIndex,
            SelectCharacterInitializationSnapshot snapshot,
            DailyChallengeRewardDefinition reward = null,
            int completed = 0) =>
            Create(status, groupIndex, snapshot, reward, completed);

        private static DailyChallengeRewardClaimResult Create(
            DailyChallengeRewardClaimStatus status,
            int groupIndex,
            SelectCharacterInitializationSnapshot snapshot,
            DailyChallengeRewardDefinition reward,
            int completed) =>
            new DailyChallengeRewardClaimResult
            {
                Status = status,
                GroupIndex = groupIndex,
                ItemId = reward?.ItemId ?? 0,
                ItemCount = reward?.ItemCount ?? 0,
                RequiredCompletionCount = reward?.RequiredCompletionCount ?? 0,
                CompletedEntryCount = completed,
                Snapshot = snapshot,
            };
    }

    internal sealed class RewardInventoryRollback
    {
        internal InventoryRewardGrantKind Kind { get; private set; }
        internal InventoryListType ListType { get; private set; }
        internal short SlotIndex { get; private set; }
        internal ItemCore PreviousItem { get; private set; }
        internal VirtualCountItem PreviousVirtualCount { get; private set; }

        internal static RewardInventoryRollback Capture(
            InventoryService inventory,
            InventoryRewardGrantPlanEntry entry)
        {
            var snapshot = new RewardInventoryRollback
            {
                Kind = entry.Kind,
                ListType = entry.ListType,
                SlotIndex = entry.SlotIndex,
            };
            if (entry.Kind == InventoryRewardGrantKind.InventoryItem)
                snapshot.PreviousItem = inventory.GetItem(entry.ListType, entry.SlotIndex)?.Copy();
            else if (entry.Kind == InventoryRewardGrantKind.MainVirtualCount)
                snapshot.PreviousVirtualCount = inventory.GetMainVirtualCount(entry.SlotIndex);
            return snapshot;
        }

        internal static void Restore(
            InventoryService inventory,
            RewardInventoryRollback snapshot,
            InventoryRewardGrantBatchResult grant)
        {
            if (inventory == null || snapshot == null)
                return;

            if (grant != null)
            {
                foreach (var result in grant.Results)
                    InventoryCreateService.DetachCreatedDetails(inventory, result.CreateResult);
            }

            if (snapshot.Kind == InventoryRewardGrantKind.InventoryItem)
            {
                if (snapshot.PreviousItem == null)
                    inventory.RemoveItem(snapshot.ListType, snapshot.SlotIndex);
                else
                    inventory.SetItem(
                        snapshot.ListType,
                        snapshot.SlotIndex,
                        snapshot.PreviousItem.Copy());
            }
            else if (snapshot.Kind == InventoryRewardGrantKind.MainVirtualCount
                && snapshot.PreviousVirtualCount != null)
            {
                inventory.SetMainVirtualCount(
                    snapshot.SlotIndex,
                    snapshot.PreviousVirtualCount.ItemId,
                    snapshot.PreviousVirtualCount.Count);
            }
        }
    }

    internal sealed class DailyChallengeSetTriggerResult
    {
        internal DailyChallengeSetTriggerResult(
            QuestSetTriggerResult ack,
            SelectCharacterInitializationSnapshot snapshot,
            bool found,
            bool changed)
        {
            Ack = ack;
            Snapshot = snapshot;
            Found = found;
            Changed = changed;
        }

        internal QuestSetTriggerResult Ack { get; }
        internal SelectCharacterInitializationSnapshot Snapshot { get; }
        internal bool Found { get; }
        internal bool Changed { get; }
    }
}
