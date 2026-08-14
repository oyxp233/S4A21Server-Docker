using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    public struct QuestDropCandidate
    {
        public int QuestId;
        public int ItemId;
        public int Count;
        public int DropRate;
        public int MaxStack;
        public int SeekingRequiredCount;
        public bool PreferQuestInventory;
    }

    public static class QuestDropProvider
    {
        public const int EnemyTypeMonster = 1;
        public const int EnemyTypeAiCharacter = 2;
        public const int EnemyTypePassiveObject = 3;

        

        /// <summary>
        /// 检查杀怪是否触发任务物品掉落。
        /// 匹配逻辑 (IDA 实证 Quest::CheckKillMonster @ 0x83535d6):
        ///   monsterCode 精确匹配 (无通配), dungeonId -1=任意, difficulty -1=任意, enemyType 默认1
        /// </summary>
        public static List<QuestDropCandidate> CheckMonsterDrop(
            ICollection<int> activeQuestIds, int dungeonIndex, int difficulty, int monsterCode)
        {
            if (activeQuestIds == null || activeQuestIds.Count == 0)
                return null;

            var results = new List<QuestDropCandidate>();

            foreach (var questId in activeQuestIds)
            {
                try
                {
                    var qst = QuestData.GetQuestFile(questId);
                    if (qst == null)
                        continue;

                    foreach (var entry in qst.MonsterRewardItems)
                    {
                        if (entry.MonsterCode != monsterCode)
                            continue;
                        if (!MatchesScope(entry.DungeonId, entry.Difficulty, dungeonIndex, difficulty))
                            continue;

                        results.Add(new QuestDropCandidate
                        {
                            QuestId = questId,
                            ItemId = entry.ItemId,
                            Count = entry.Count,
                            DropRate = entry.DropRate,
                            MaxStack = entry.MaxStack,
                            SeekingRequiredCount =
                                GetSeekingRequiredCount(
                                    questId,
                                    entry.ItemId),
                            PreferQuestInventory =
                                IsSeekingTargetItem(
                                    questId,
                                    entry.ItemId),
                        });
                    }

                    foreach (var entry in qst.EnemyRewardItems)
                    {
                        if (entry.EnemyType != EnemyTypeMonster
                            || entry.EnemyCode != monsterCode
                            || !MatchesScope(
                                entry.DungeonId,
                                entry.Difficulty,
                                dungeonIndex,
                                difficulty))
                        {
                            continue;
                        }

                        results.Add(new QuestDropCandidate
                        {
                            QuestId = questId,
                            ItemId = entry.ItemId,
                            Count = entry.Count,
                            DropRate = entry.DropRate,
                            MaxStack = entry.MaxStack,
                            SeekingRequiredCount =
                                GetSeekingRequiredCount(
                                    questId,
                                    entry.ItemId),
                            PreferQuestInventory =
                                IsSeekingTargetItem(
                                    questId,
                                    entry.ItemId),
                        });
                    }

                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[QuestDropProvider] ERROR: monster drop match failed, quest {questId} skipped: {ex.Message}");
                }
            }

            return results.Count > 0 ? results : null;
        }

        public static List<QuestDropCandidate> CheckMonsterCaptureDrop(
            ICollection<int> activeQuestIds,
            IReadOnlyList<MonsterCaptureItemDefinition> captureItems)
        {
            if (activeQuestIds == null
                || activeQuestIds.Count == 0
                || captureItems == null
                || captureItems.Count == 0)
            {
                return null;
            }

            var captureItemIds = new HashSet<int>();
            foreach (var item in captureItems)
            {
                if (item.ItemId > 0)
                    captureItemIds.Add(item.ItemId);
            }

            var questByItem = new Dictionary<int, int>();
            var requiredByItem = new Dictionary<int, int>();
            foreach (var questId in activeQuestIds)
            {
                try
                {
                    var seekingItems = QuestData.GetSeekingConsumeItems(questId);
                    if (seekingItems == null)
                        continue;

                    foreach (var seeking in seekingItems)
                    {
                        if (seeking.ItemId <= 0
                            || seeking.Count <= 0
                            || !captureItemIds.Contains(seeking.ItemId))
                        {
                            continue;
                        }

                        var required = GetSeekingRequiredCount(
                            questId,
                            seeking.ItemId);
                        if (required <= 0)
                            continue;
                        if (!requiredByItem.TryGetValue(
                                seeking.ItemId,
                                out var currentRequired)
                            || required > currentRequired)
                        {
                            requiredByItem[seeking.ItemId] = required;
                        }
                        if (!questByItem.TryGetValue(
                                seeking.ItemId,
                                out var currentQuestId)
                            || questId < currentQuestId)
                        {
                            questByItem[seeking.ItemId] = questId;
                        }
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[QuestDropProvider] ERROR: capture seeking match failed, "
                        + $"quest {questId} skipped: {ex.Message}");
                }
            }

            var result = new List<QuestDropCandidate>();
            foreach (var item in captureItems)
            {
                if (!requiredByItem.TryGetValue(item.ItemId, out var required)
                    || !questByItem.TryGetValue(item.ItemId, out var questId))
                {
                    continue;
                }

                result.Add(new QuestDropCandidate
                {
                    QuestId = questId,
                    ItemId = item.ItemId,
                    Count = item.Count,
                    DropRate = item.DropRate,
                    MaxStack = required,
                    SeekingRequiredCount = required,
                    PreferQuestInventory = true,
                });
            }

            return result.Count > 0 ? result : null;
        }

        public static List<QuestDropCandidate> CheckClearReward(
            ICollection<int> activeQuestIds,
            int dungeonIndex,
            int difficulty)
        {
            if (activeQuestIds == null || activeQuestIds.Count == 0)
                return null;

            var results = new List<QuestDropCandidate>();
            foreach (var questId in activeQuestIds)
            {
                try
                {
                    var quest = QuestData.GetQuestFile(questId);
                    if (quest?.ClearRewardItems == null)
                        continue;

                    foreach (var entry in quest.ClearRewardItems)
                    {
                        if (entry.ItemId <= 0
                            || entry.Count <= 0
                            || !MatchesScope(
                                entry.DungeonId,
                                entry.Difficulty,
                                dungeonIndex,
                                difficulty))
                        {
                            continue;
                        }

                        results.Add(new QuestDropCandidate
                        {
                            QuestId = questId,
                            ItemId = entry.ItemId,
                            Count = entry.Count,
                            DropRate = entry.DropRate,
                            MaxStack = entry.MaxStack,
                            SeekingRequiredCount =
                                GetSeekingRequiredCount(
                                    questId,
                                    entry.ItemId),
                            PreferQuestInventory =
                                IsSeekingTargetItem(
                                    questId,
                                    entry.ItemId),
                        });
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[QuestDropProvider] ERROR: clear reward match " +
                        $"failed, quest {questId} skipped: {ex.Message}");
                }
            }

            return results.Count > 0 ? results : null;
        }

        public static List<QuestDropCandidate> CheckEnemyDrop(
            ICollection<int> activeQuestIds, int dungeonIndex, int difficulty, int enemyCode, int enemyType)
        {
            if (activeQuestIds == null || activeQuestIds.Count == 0)
                return null;

            var results = new List<QuestDropCandidate>();

            foreach (var questId in activeQuestIds)
            {
                try
                {
                    var qst = QuestData.GetQuestFile(questId);
                    if (qst == null || qst.EnemyRewardItems == null || qst.EnemyRewardItems.Count == 0)
                        continue;

                    foreach (var entry in qst.EnemyRewardItems)
                    {
                        if (entry.EnemyCode != enemyCode)
                            continue;
                        if (entry.EnemyType != enemyType)
                            continue;
                        if (!MatchesScope(entry.DungeonId, entry.Difficulty, dungeonIndex, difficulty))
                            continue;

                        results.Add(new QuestDropCandidate
                        {
                            QuestId = questId,
                            ItemId = entry.ItemId,
                            Count = entry.Count,
                            DropRate = entry.DropRate,
                            MaxStack = entry.MaxStack,
                            SeekingRequiredCount =
                                GetSeekingRequiredCount(
                                    questId,
                                    entry.ItemId),
                            PreferQuestInventory =
                                IsSeekingTargetItem(
                                    questId,
                                    entry.ItemId),
                        });
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[QuestDropProvider] ERROR: enemy drop match failed, quest {questId} skipped: {ex.Message}");
                }
            }

            return results.Count > 0 ? results : null;
        }

        private static bool IsSeekingTargetItem(int questId, int itemId)
        {
            if (questId <= 0 || itemId <= 0)
                return false;

            return QuestData.GetSeekingConsumeItems(questId).Exists(
                item => item.ItemId == itemId && item.Count > 0);
        }

        private static int GetSeekingRequiredCount(int questId, int itemId)
        {
            if (questId <= 0 || itemId <= 0)
                return -1;

            long required = 0;
            foreach (var item in QuestData.GetSeekingConsumeItems(questId))
            {
                if (item.ItemId == itemId && item.Count > 0)
                    required += item.Count;
            }
            if (required <= 0)
                return -1;
            return required > int.MaxValue ? int.MaxValue : (int)required;
        }

        private static bool MatchesScope(int dungeonId, int rewardDifficulty, int dungeonIndex, int difficulty)
        {
            if (dungeonId != -1 && dungeonId != dungeonIndex)
                return false;
            if (rewardDifficulty >= 0 && rewardDifficulty != difficulty)
                return false;
            return true;
        }

        /// <summary>
        /// 从候选列表 roll 掉落数量。
        /// 逻辑 (IDA 实证 CheckQuestMonster):
        ///   随机选一个候选 → for(k=0; k &lt; count; k++) if(rand(100) &lt; dropRate) actualCount++
        ///   如果 maxStack 已满则换下一个候选
        /// 简化: 单机不做装备/VIP 加成, 只用基础 dropRate
        /// </summary>
        public static int RollDrop(QuestDropCandidate candidate, int currentHeld)
        {
            var effectiveLimit = GetEffectiveHeldLimit(candidate);
            if (effectiveLimit >= 0 && currentHeld >= effectiveLimit)
                return 0;

            int actual = 0;
            int maxAttempts = Math.Max(1, candidate.Count);
            for (int k = 0; k < maxAttempts; k++)
            {
                if (Infrastructure.ServerRandom.Next(100) < candidate.DropRate)
                    actual++;
            }

            if (actual <= 0) return 0;
            if (actual > 999) actual = 999;

            if (effectiveLimit >= 0 && currentHeld + actual > effectiveLimit)
                actual = effectiveLimit - currentHeld;

            return Math.Max(0, actual);
        }

        public static int GetEffectiveHeldLimit(QuestDropCandidate candidate)
        {
            var limit = candidate.MaxStack >= 0
                ? candidate.MaxStack
                : -1;
            if (candidate.SeekingRequiredCount > 0)
            {
                limit = limit < 0
                    ? candidate.SeekingRequiredCount
                    : Math.Min(limit, candidate.SeekingRequiredCount);
            }
            return limit;
        }
    }
}
