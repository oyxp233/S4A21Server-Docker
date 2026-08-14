using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestAcceptanceApplicationService
    {
        private readonly string _connectionString;
        private readonly QuestPrerequisiteEvaluator _prerequisites;

        internal QuestAcceptanceApplicationService(string connectionString)
        {
            _connectionString = connectionString;
            _prerequisites = new QuestPrerequisiteEvaluator(connectionString);
        }

        internal QuestAcceptResult Apply(
            QuestCommandOwnerContext owner,
            QuestAcceptCommand command)
        {
            var characterId = owner.CharacterId;
            var questId = command.QuestId;
            var lease = owner.InventoryLease;
            if (!owner.IsCurrentInventoryOwner()
                || lease.AccountId != owner.AccountId)
            {
                return QuestAcceptResult.Fail(0x17);
            }

            var repeatable = GameWorld.QuestData.IsRepeatableQuest(questId);
            var eventItems = GameWorld.QuestData.GetEventItems(questId);
            var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(questId);
            var eventSlots = new List<ushort>(eventItems.Count);
            var grantRequests = new List<InventoryRewardGrantRequest>();
            var grantRequestIndexes = new List<int>();
            for (var index = 0; index < eventItems.Count; index++)
            {
                var item = eventItems[index];
                eventSlots.Add(0);
                if (item.ItemId <= 0 || item.Count <= 0)
                    continue;

                grantRequests.Add(InventoryRewardGrantRequest.Create(
                    item.ItemId,
                    item.Count,
                    ItemCreateReason.QuestReward));
                grantRequestIndexes.Add(index);
            }

            var clientInitialTrigger = GameWorld.QuestData.GetInitTrigger(questId);
            var committedTrigger = clientInitialTrigger;
            var slot = -1;
            lock (lease.SyncRoot)
            {
                if (!owner.IsCurrentInventoryOwner())
                    return QuestAcceptResult.Fail(0x17);

                var inventory = lease.Inventory;
                InventoryRewardGrantBatchPlan grantPlan = null;
                if (grantRequests.Count > 0
                    && (!InventoryRewardGrantService.TryPlanBatch(
                            inventory,
                            grantRequests,
                            out grantPlan)
                        || grantPlan == null
                        || !grantPlan.Success))
                {
                    return QuestAcceptResult.Fail(0x11);
                }

                QuestAcceptanceInventoryRollback rollback = null;
                var inventoryMutated = false;
                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            if (!owner.IsCurrentInventoryOwner())
                                return QuestAcceptResult.Fail(0x17);

                            var active = QuestRepository.LoadActiveQuests(
                                connection,
                                transaction,
                                characterId);
                            if (QuestActiveListRules.FindByQuestId(active, questId) != null)
                                return QuestAcceptResult.Fail(18);
                            if (QuestRepository.IsQuestCleared(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId)
                                && !repeatable)
                            {
                                return QuestAcceptResult.Fail(18);
                            }
                            if (!_prerequisites.IsSatisfied(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId,
                                    active))
                            {
                                return QuestAcceptResult.Fail(21);
                            }
                            if (!QuestDungeonActivationPolicy
                                    .IsAcceptanceAllowed(questId, active))
                            {
                                FileLogger.Log(
                                    $"[QuestAcceptanceApplicationService] ACCEPT " +
                                    $"blocked by task-dungeon presentation priority: " +
                                    $"quest={questId} cid={characterId}");
                                return QuestAcceptResult.Fail(21);
                            }

                            slot = QuestActiveListRules.FindFreeSlot(active);
                            if (slot < 0)
                            {
                                return QuestAcceptResult.Fail(
                                    QuestSlotLayout.ActiveListFullFallbackError);
                            }

                            if (GameWorld.QuestData.IsQuestClearQuest(questId))
                            {
                                committedTrigger = QuestClearProgressRules.Compute(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId);
                            }
                            if (seekItems.Count > 0)
                            {
                                committedTrigger = QuestProgressReducer.ApplySeekingItems(
                                    new QuestTrigger(committedTrigger),
                                    seekItems,
                                    itemId => CountMainItemWithPendingRewards(
                                        inventory,
                                        itemId,
                                        eventItems)).PackedValue;
                            }

                            if (grantPlan != null && grantPlan.Entries.Count > 0)
                            {
                                rollback = QuestAcceptanceInventoryRollback.Capture(
                                    inventory,
                                    grantPlan);
                                if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                                        inventory,
                                        grantPlan,
                                        out var grantResult)
                                    || grantResult == null
                                    || !grantResult.Success)
                                {
                                    throw new InvalidOperationException(
                                        $"quest event item apply failed: {grantResult?.Error}");
                                }
                                inventoryMutated = true;

                                for (var index = 0;
                                     index < grantResult.Results.Count
                                     && index < grantRequestIndexes.Count;
                                     index++)
                                {
                                    var slotIndex = grantResult.Results[index].SlotIndex;
                                    if (slotIndex >= 0)
                                    {
                                        eventSlots[grantRequestIndexes[index]] =
                                            (ushort)slotIndex;
                                    }
                                }
                            }

                            QuestRepository.InsertActiveQuest(
                                connection,
                                transaction,
                                characterId,
                                slot,
                                questId,
                                committedTrigger);
                            if (repeatable)
                            {
                                QuestRepository.DeleteClearedFlag(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId);
                            }

                            if (inventoryMutated
                                && !InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                throw new InvalidOperationException(
                                    "quest event item persistence returned false");
                            }
                            if (!owner.IsCurrentInventoryOwner())
                            {
                                throw new InvalidOperationException(
                                    "quest accept inventory lease was replaced");
                            }

                            transaction.Commit();
                        }
                    }

                    if (inventoryMutated)
                        inventory.ClearDirtyState();
                }
                catch (Exception ex)
                {
                    if (inventoryMutated && rollback != null)
                        rollback.Restore(inventory, grantPlan);

                    FileLogger.Log(
                        $"[QuestAcceptanceApplicationService] ACCEPT failed before " +
                        $"atomic commit: quest={questId} cid={characterId} " +
                        $"error={ex.Message}");
                    return QuestAcceptResult.Fail(0x17);
                }
            }

            var result = new QuestAcceptResult
            {
                QuestId = questId,
                InitTrigger = clientInitialTrigger,
                CommittedTrigger = committedTrigger,
            };
            if (clientInitialTrigger != committedTrigger)
            {
                result.PostAcceptTriggerProjection = new QuestSetTriggerResult
                {
                    QuestId = questId,
                    PreviousTriggerValue = clientInitialTrigger,
                    TriggerValue = committedTrigger,
                };
            }
            for (var index = 0; index < eventItems.Count; index++)
            {
                result.EventItems.Add(new QuestEventItemGrant
                {
                    SlotIndex = eventSlots[index],
                    ItemId = eventItems[index].ItemId,
                    Count = eventItems[index].Count,
                });
            }
            FileLogger.Log(
                $"[QuestAcceptanceApplicationService] ACCEPT quest={questId} " +
                $"slot={slot} clientInitTrigger={clientInitialTrigger} " +
                $"committedTrigger={committedTrigger} " +
                $"eventItems={eventItems.Count}");
            return result;
        }

        private static int CountMainItemWithPendingRewards(
            InventoryService inventory,
            int itemId,
            IReadOnlyCollection<GameWorld.QuestRewardItem> pendingRewards)
        {
            var count = inventory != null ? inventory.CountMainItem(itemId) : 0;
            if (pendingRewards == null)
                return count;

            foreach (var reward in pendingRewards)
            {
                if (reward.ItemId <= 0 || reward.Count <= 0)
                    continue;
                if (GetMainItemIdentityKey(itemId)
                    != GetMainItemIdentityKey(reward.ItemId))
                {
                    continue;
                }

                var value = (long)Math.Max(0, count) + reward.Count;
                count = value > int.MaxValue ? int.MaxValue : (int)value;
            }
            return count;
        }

        private static int GetMainItemIdentityKey(int itemId)
        {
            return InventoryService.TryResolveMainVirtualSlotByItemId(
                itemId,
                out var slotIndex,
                out _)
                ? -100000 - slotIndex
                : itemId;
        }

        private sealed class QuestAcceptanceInventoryRollback
        {
            private readonly Dictionary<(InventoryListType, short), ItemCore> _items =
                new Dictionary<(InventoryListType, short), ItemCore>();
            private readonly Dictionary<short, int> _virtualCounts =
                new Dictionary<short, int>();

            internal static QuestAcceptanceInventoryRollback Capture(
                InventoryService inventory,
                InventoryRewardGrantBatchPlan plan)
            {
                var snapshot = new QuestAcceptanceInventoryRollback();
                foreach (var entry in plan.Entries)
                {
                    if (entry.Kind == InventoryRewardGrantKind.MainVirtualCount)
                    {
                        if (!snapshot._virtualCounts.ContainsKey(entry.SlotIndex))
                        {
                            snapshot._virtualCounts[entry.SlotIndex] =
                                inventory.GetMainVirtualCount(entry.SlotIndex)?.Count ?? 0;
                        }
                        continue;
                    }
                    if (entry.Kind != InventoryRewardGrantKind.InventoryItem)
                        continue;

                    var key = (entry.ListType, entry.SlotIndex);
                    if (!snapshot._items.ContainsKey(key))
                    {
                        snapshot._items[key] = inventory.TryGetItem(
                            entry.ListType,
                            entry.SlotIndex,
                            out var item)
                            ? item.Copy()
                            : null;
                    }
                }
                return snapshot;
            }

            internal void Restore(
                InventoryService inventory,
                InventoryRewardGrantBatchPlan plan)
            {
                foreach (var entry in plan.Entries)
                {
                    if (entry.Kind == InventoryRewardGrantKind.InventoryItem
                        && entry.CreateResult != null)
                    {
                        InventoryCreateService.DetachCreatedDetails(
                            inventory,
                            entry.CreateResult);
                    }
                }

                foreach (var pair in _items)
                {
                    if (pair.Value == null)
                        inventory.RemoveItem(pair.Key.Item1, pair.Key.Item2);
                    else
                    {
                        inventory.SetItem(
                            pair.Key.Item1,
                            pair.Key.Item2,
                            pair.Value.Copy());
                    }
                }
                foreach (var pair in _virtualCounts)
                    inventory.SetMainVirtualCount(pair.Key, pair.Value);
            }
        }
    }
}
