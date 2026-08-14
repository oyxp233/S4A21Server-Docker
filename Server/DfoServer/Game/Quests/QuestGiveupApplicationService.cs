using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestGiveupApplicationService
    {
        private readonly QuestRepository _repository;

        internal QuestGiveupApplicationService(QuestRepository repository)
        {
            _repository = repository;
        }

        internal QuestGiveupResult Apply(
            QuestCommandOwnerContext owner,
            QuestGiveupCommand command)
        {
            var characterId = owner.CharacterId;
            var questId = command.QuestId;
            var lease = owner.InventoryLease;
            if (!owner.IsCurrentInventoryOwner())
            {
                FileLogger.Log(
                    $"[QuestGiveupApplicationService] GIVEUP rejected: " +
                    $"online inventory missing quest={questId} cid={characterId}");
                return QuestGiveupResult.Fail(0x17);
            }

            lock (lease.SyncRoot)
            {
                if (!owner.IsCurrentInventoryOwner())
                    return QuestGiveupResult.Fail(0x17);

                var inventory = lease.Inventory;
                var changes = new InventoryMutationSet();
                Dictionary<short, ItemCore> snapshots = null;
                var inventoryMutated = false;
                try
                {
                    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                               _repository.ConnectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            var active = QuestRepository.LoadActiveQuests(
                                connection,
                                transaction,
                                characterId);
                            var quest = QuestActiveListRules.FindByQuestId(
                                active,
                                questId);
                            if (quest == null)
                                return QuestGiveupResult.Fail(19);
                            if (!GameWorld.QuestData.CanGiveup(questId))
                                return QuestGiveupResult.Fail(20);

                            var recoveryPlan = QuestGiveupItemRecoveryPolicy.Build(
                                active,
                                questId);
                            snapshots = CaptureTargetSlots(inventory, recoveryPlan);
                            foreach (var entry in recoveryPlan)
                            {
                                var current = inventory.CountMainItem(entry.ItemId);
                                var deleteCount = Math.Max(
                                    0,
                                    current - entry.RetainCount);
                                if (deleteCount <= 0)
                                    continue;

                                inventoryMutated = true;
                                if (!InventoryDeleteService
                                        .TryDeleteMainItemsByTemplateId(
                                        inventory,
                                        entry.ItemId,
                                        deleteCount,
                                        out var deleted))
                                {
                                    throw new InvalidOperationException(
                                        $"item reclaim failed item={entry.ItemId} " +
                                        $"held={current} retain={entry.RetainCount}");
                                }
                                changes.AddRange(deleted);
                            }

                            if (!QuestRepository.TryDeleteActiveQuestCas(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId,
                                    quest.ActivationId,
                                    quest.Version,
                                    quest.TriggerValue))
                            {
                                throw new InvalidOperationException(
                                    "quest activation changed before giveup commit");
                            }

                            if (inventoryMutated
                                && !InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                throw new InvalidOperationException(
                                    "quest giveup inventory persistence returned false");
                            }
                            if (!owner.IsCurrentInventoryOwner())
                            {
                                throw new InvalidOperationException(
                                    "quest giveup inventory lease was replaced");
                            }

                            transaction.Commit();
                        }
                    }

                    if (inventoryMutated)
                        inventory.ClearDirtyState();
                }
                catch (Exception ex)
                {
                    try
                    {
                        InventoryRollbackRecoveryService.ReloadOnlineInventory(
                            _repository.ConnectionString,
                            lease);
                    }
                    catch (Exception recoveryException)
                    {
                        if (inventoryMutated && snapshots != null)
                        {
                            RestoreTargetSlots(inventory, snapshots);
                            inventory.ClearDirtyState();
                        }
                        FileLogger.Log(
                            $"[QuestGiveupApplicationService] GIVEUP rollback reload "
                            + $"failed quest={questId} cid={characterId}: "
                            + recoveryException.Message);
                    }
                    FileLogger.Log(
                        $"[QuestGiveupApplicationService] GIVEUP failed before " +
                        $"atomic commit " +
                        $"quest={questId}: {ex.Message}");
                    return QuestGiveupResult.Fail(0x17);
                }

                var result = new QuestGiveupResult { QuestId = questId };
                result.InventoryChanges.AddRange(changes);
                FileLogger.Log(
                    $"[QuestGiveupApplicationService] GIVEUP quest={questId} " +
                    $"reclaimedSlots={changes.Slots.Count}");
                return result;
            }
        }

        private static Dictionary<short, ItemCore> CaptureTargetSlots(
            InventoryService inventory,
            IReadOnlyCollection<QuestGiveupItemRecoveryEntry> recoveryPlan)
        {
            var itemIds = new HashSet<int>();
            foreach (var entry in recoveryPlan)
                itemIds.Add(entry.ItemId);

            var snapshots = new Dictionary<short, ItemCore>();
            foreach (var pair in inventory.GetItems(InventoryListType.Main))
            {
                if (pair.Value != null && itemIds.Contains(pair.Value.ItemId))
                    snapshots[pair.Key] = pair.Value.Copy();
            }
            return snapshots;
        }

        private static void RestoreTargetSlots(
            InventoryService inventory,
            IReadOnlyDictionary<short, ItemCore> snapshots)
        {
            if (inventory == null || snapshots == null)
                return;

            foreach (var pair in snapshots)
                inventory.SetItem(
                    InventoryListType.Main,
                    pair.Key,
                    pair.Value.Copy());
        }
    }
}
