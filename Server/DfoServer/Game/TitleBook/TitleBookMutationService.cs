using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;

namespace DfoServer.Game.TitleBook
{
    public sealed class TitleBookMutationService
    {
        private readonly TitleBookStaticDataProvider _staticData;

        internal TitleBookMutationService()
        {
            _staticData = TitleBookStaticDataProvider.LoadDefault();
        }

        internal TitleBookMutationResult PutTitle(
            int characterId,
            int accountId,
            InventoryListType sourceList,
            short sourceSlot,
            int itemId,
            int category,
            int bookIndex)
        {
            var result = CreateMutationResult(sourceList, sourceSlot, itemId, category, bookIndex);
            if (!IsCategoryIndexValid(category, bookIndex))
                return result;

            if (!InventoryContext.TryGetLease(characterId, out var lease))
                return result;

            return PutTitle(
                lease,
                accountId,
                sourceList,
                sourceSlot,
                itemId,
                category,
                bookIndex,
                result);
        }

        internal TitleBookMutationResult PutTitle(
            InventoryLease lease,
            int accountId,
            InventoryListType sourceList,
            short sourceSlot,
            int itemId,
            int category,
            int bookIndex)
        {
            var result = CreateMutationResult(
                sourceList,
                sourceSlot,
                itemId,
                category,
                bookIndex);
            if (!IsCategoryIndexValid(category, bookIndex))
                return result;

            return PutTitle(
                lease,
                accountId,
                sourceList,
                sourceSlot,
                itemId,
                category,
                bookIndex,
                result);
        }

        private TitleBookMutationResult PutTitle(
            InventoryLease lease,
            int accountId,
            InventoryListType sourceList,
            short sourceSlot,
            int itemId,
            int category,
            int bookIndex,
            TitleBookMutationResult result)
        {
            if (!MatchesOwner(lease, accountId))
                return result;

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                if (sourceList == InventoryListType.Equipment)
                    return PutEquippedTitle(inventory, sourceSlot, itemId, category, bookIndex, result);

                var source = inventory.GetItem(sourceList, sourceSlot);
                if (source == null || source.ItemId != itemId)
                    return result;
                source = source.Copy();

                var definition = _staticData.GetSlot(category, bookIndex);
                if (!definition.AllowsItem(itemId))
                    return result;

                var existing = inventory.TitleBook.GetItem(category, bookIndex);

                if (!inventory.RemoveItem(sourceList, sourceSlot))
                    return result;

                if (existing != null)
                {
                    if (!inventory.SetItem(sourceList, sourceSlot, existing))
                    {
                        inventory.SetItem(sourceList, sourceSlot, source);
                        return result;
                    }
                }

                if (!inventory.TitleBook.SetItem(category, bookIndex, source))
                {
                    if (existing != null)
                        inventory.TitleBook.SetItem(category, bookIndex, existing);
                    inventory.SetItem(sourceList, sourceSlot, source);
                    return result;
                }

                result.Success = true;
                result.InventoryChanged = true;
                result.ItemLockChanged |= source.EquipmentLockId != 0
                    || (existing != null && existing.EquipmentLockId != 0);
                return result;
            }
        }

        private TitleBookMutationResult PutEquippedTitle(
            InventoryService inventory,
            short sourceSlot,
            int itemId,
            int category,
            int bookIndex,
            TitleBookMutationResult result)
        {
            var equipped = inventory.GetItem(InventoryListType.Equipment, sourceSlot);
            if (equipped == null || equipped.ItemId != itemId)
            {
                result.ErrorCode = 0x02;
                return result;
            }

            if (!TryResolveTitleBookSlotForItem(itemId, category, bookIndex, out var resolvedCategory, out var resolvedBookIndex))
            {
                result.ErrorCode = 0x02;
                return result;
            }

            var existing = inventory.TitleBook.GetItem(resolvedCategory, resolvedBookIndex);
            if (existing != null && existing.ItemId != itemId)
            {
                result.ErrorCode = 0x02;
                return result;
            }

            if (!inventory.TitleBook.SetItem(resolvedCategory, resolvedBookIndex, equipped))
                return result;

            if (!inventory.RemoveItem(InventoryListType.Equipment, sourceSlot))
            {
                if (existing != null)
                    inventory.TitleBook.SetItem(resolvedCategory, resolvedBookIndex, existing);
                else
                    inventory.TitleBook.ClearItem(resolvedCategory, resolvedBookIndex);
                return result;
            }

            if (existing != null
                && existing.EquipmentLockId != 0
                && existing.EquipmentLockId != equipped.EquipmentLockId)
            {
                inventory.EquipmentLocks.Remove(existing.EquipmentLockId);
                result.ItemLockChanged = true;
            }

            result.Success = true;
            result.Category = resolvedCategory;
            result.BookIndex = resolvedBookIndex;
            result.EquipmentChanged = true;
            result.ItemLockChanged |= equipped.EquipmentLockId != 0;
            return result;
        }

        internal TitleBookMutationResult GetTitle(
            int characterId,
            int accountId,
            InventoryListType targetList,
            short targetSlot,
            int itemId,
            int category,
            int bookIndex)
        {
            var result = CreateMutationResult(targetList, targetSlot, itemId, category, bookIndex);
            if (!IsCategoryIndexValid(category, bookIndex))
                return result;

            if (!InventoryContext.TryGetLease(characterId, out var lease))
                return result;

            return GetTitle(
                lease,
                accountId,
                targetList,
                targetSlot,
                itemId,
                category,
                bookIndex,
                result);
        }

        internal TitleBookMutationResult GetTitle(
            InventoryLease lease,
            int accountId,
            InventoryListType targetList,
            short targetSlot,
            int itemId,
            int category,
            int bookIndex)
        {
            var result = CreateMutationResult(
                targetList,
                targetSlot,
                itemId,
                category,
                bookIndex);
            if (!IsCategoryIndexValid(category, bookIndex))
                return result;

            return GetTitle(
                lease,
                accountId,
                targetList,
                targetSlot,
                itemId,
                category,
                bookIndex,
                result);
        }

        private TitleBookMutationResult GetTitle(
            InventoryLease lease,
            int accountId,
            InventoryListType targetList,
            short targetSlot,
            int itemId,
            int category,
            int bookIndex,
            TitleBookMutationResult result)
        {
            if (!MatchesOwner(lease, accountId))
                return result;

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                var title = inventory.TitleBook.GetItem(category, bookIndex);
                if (title == null || title.ItemId != itemId)
                    return result;

                var definition = _staticData.GetSlot(category, bookIndex);
                if (targetList != InventoryListType.Equipment && definition.QuestId != -1)
                    return result;

                if (targetList == InventoryListType.Equipment
                    && targetSlot != (short)EquipmentType.TitleName)
                {
                    result.ErrorCode = 0x02;
                    return result;
                }

                var target = inventory.GetItem(targetList, targetSlot);
                if (target != null)
                    return result;

                if (!inventory.SetItem(targetList, targetSlot, title))
                    return result;

                if (!inventory.TitleBook.ClearItem(category, bookIndex))
                {
                    inventory.RemoveItem(targetList, targetSlot);
                    return result;
                }

                result.Success = true;
                result.InventoryChanged = targetList != InventoryListType.Equipment;
                result.EquipmentChanged = targetList == InventoryListType.Equipment;
                result.ItemLockChanged = title.EquipmentLockId != 0;
                return result;
            }
        }

        internal AchievementTriggerResult TriggerAchievement(int characterId, int questId, ushort delta1, ushort delta2, ushort delta3)
        {
            var result = new AchievementTriggerResult { QuestId = questId };
            if (!InventoryContext.TryGetLease(characterId, out var lease))
                return result;

            return TriggerAchievement(lease, questId, delta1, delta2, delta3);
        }

        internal AchievementTriggerResult TriggerAchievement(
            InventoryLease lease,
            int questId,
            ushort delta1,
            ushort delta2,
            ushort delta3)
        {
            var result = new AchievementTriggerResult { QuestId = questId };
            if (lease?.Inventory == null)
                return result;

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                TitleBookSlotDefinition definition = null;
                TitleQuestDefinition quest = null;
                if (_staticData.TryFindByQuestId(questId, out definition))
                    quest = _staticData.GetQuest(definition.QuestId);

                var entry = inventory.Achievements.GetOrCreateEntry(
                    questId,
                    quest != null ? quest.CheckCount : (ushort)1);
                var wasCompleted = entry.P1 == 0 && entry.P2 == 0 && entry.P3 == 0;
                entry.P1 = SaturatingSubtract(entry.P1, delta1);
                entry.P2 = SaturatingSubtract(entry.P2, delta2);
                entry.P3 = SaturatingSubtract(entry.P3, delta3);
                inventory.Achievements.MarkDirty(questId);

                result.Remain1 = entry.P1;
                result.Remain2 = entry.P2;
                result.Remain3 = entry.P3;
                result.TailOrState = entry.P4;
                result.Success = true;
                result.Completed = !wasCompleted
                    && entry.P1 == 0
                    && entry.P2 == 0
                    && entry.P3 == 0;

                if (result.Completed && definition != null)
                {
                    var titleItemId = quest != null && quest.RewardTitleItemId > 0
                        ? quest.RewardTitleItemId
                        : definition.AllowedTitleItemIds.FirstOrDefault(id => id > 0);
                    if (titleItemId > 0)
                    {
                        var title = InventoryCreateService.CreateCore(
                            ItemCore.KindEquipment,
                            titleItemId,
                            ItemCreateReason.QuestReward,
                            1);
                        inventory.TitleBook.SetItem(definition.Category, definition.Index, title);
                        result.Category = definition.Category;
                        result.BookIndex = definition.Index;
                        result.TitleItemId = titleItemId;
                    }
                }

                return result;
            }
        }

        internal IReadOnlyList<AchievementTriggerResult> TriggerUseItemAchievements(
            InventoryLease lease,
            int itemId,
            int consumedCount)
        {
            return TriggerUseItemAchievements(
                lease,
                new[]
                {
                    new KeyValuePair<int, int>(itemId, consumedCount),
                });
        }

        internal IReadOnlyList<AchievementTriggerResult> TriggerUseItemAchievements(
            InventoryLease lease,
            IEnumerable<KeyValuePair<int, int>> consumedItems)
        {
            var results = new List<AchievementTriggerResult>();
            if (lease?.Inventory == null || consumedItems == null)
                return results;

            foreach (var pair in _staticData
                .BuildUseItemProgressDeltas(consumedItems)
                .OrderBy(pair => pair.Key))
            {
                var result = TriggerAchievement(
                    lease,
                    pair.Key,
                    pair.Value,
                    0,
                    0);
                if (result.Success)
                    results.Add(result);
            }

            return results;
        }

        private static bool MatchesOwner(
            InventoryLease lease,
            int accountId)
        {
            return lease?.Inventory != null
                && lease.CharacterId == lease.Inventory.CharacterId
                && (accountId <= 0 || lease.AccountId == accountId);
        }

        private bool TryResolveTitleBookSlotForItem(
            int itemId,
            int requestedCategory,
            int requestedBookIndex,
            out int category,
            out int bookIndex)
        {
            if (IsCategoryIndexValid(requestedCategory, requestedBookIndex)
                && _staticData.GetSlot(requestedCategory, requestedBookIndex).AllowsItem(itemId))
            {
                category = requestedCategory;
                bookIndex = requestedBookIndex;
                return true;
            }

            for (category = 0; category < TitleBookStaticDataProvider.CategoryCapacities.Count; category++)
            {
                var capacity = TitleBookStaticDataProvider.CategoryCapacities[category];
                for (bookIndex = 0; bookIndex < capacity; bookIndex++)
                {
                    if (_staticData.GetSlot(category, bookIndex).AllowsItem(itemId))
                        return true;
                }
            }

            category = -1;
            bookIndex = -1;
            return false;
        }

        private static TitleBookMutationResult CreateMutationResult(InventoryListType itemSpace, short slotIndex, int itemId, int category, int bookIndex)
        {
            return new TitleBookMutationResult
            {
                ItemSpace = itemSpace,
                SlotIndex = slotIndex,
                ItemId = itemId,
                Category = category,
                BookIndex = bookIndex,
            };
        }

        private static bool IsCategoryIndexValid(int category, int bookIndex)
        {
            return category >= 0
                && category < TitleBookStaticDataProvider.CategoryCapacities.Count
                && bookIndex >= 0
                && bookIndex < TitleBookStaticDataProvider.CategoryCapacities[category];
        }

        private static ushort SaturatingSubtract(ushort value, ushort delta)
        {
            return delta >= value ? (ushort)0 : (ushort)(value - delta);
        }
    }
}
