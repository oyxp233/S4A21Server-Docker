using System;
using System.Collections.Generic;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal static class QuestRelationIndex
    {
        private static readonly Lazy<Dictionary<int, int>> QuestionAnswerCounts =
            new Lazy<Dictionary<int, int>>(BuildQuestionAnswerCounts);
        private static readonly Lazy<Dictionary<int, int[]>> SuccessorQuestIds =
            new Lazy<Dictionary<int, int[]>>(BuildSuccessorQuestIds);

        internal static List<int> GetPreRequiredQuests(int questId)
        {
            var result = new List<int>();
            var definition = QuestPrerequisiteCatalog.Get(questId);
            if (definition == null || !definition.IsValid)
                return result;

            var seen = new HashSet<int>();
            foreach (var group in definition.CompletedQuestGroups)
            {
                foreach (var prerequisiteQuestId in group)
                {
                    if (seen.Add(prerequisiteQuestId))
                        result.Add(prerequisiteQuestId);
                }
            }
            return result;
        }

        internal static List<int> GetCollisionQuests(int questId)
        {
            var definition = QuestPrerequisiteCatalog.Get(questId);
            return definition != null && definition.IsValid
                ? new List<int>(definition.CollisionQuestIds)
                : new List<int>();
        }

        internal static bool IsQuestionQuest(int questId)
            => QuestData.NormalizeQuestTag(QuestCatalog.Get(questId)?.Type)
                == "question";

        internal static int GetQuestionAnswerCount(int questId)
            => QuestionAnswerCounts.Value.TryGetValue(questId, out var count)
                ? count
                : 0;

        internal static int GetRequiredQuestAnswerFlagValue(int answerIndex)
            => answerIndex >= 0 ? answerIndex + 1 : 0;

        internal static bool DoesClearedFlagMatchRequiredQuestAnswer(
            IReadOnlyDictionary<int, int> clearedFlags,
            int requiredQuestId,
            int requiredAnswerIndex)
        {
            if (requiredQuestId <= 0)
                return true;

            var requiredFlag = GetRequiredQuestAnswerFlagValue(
                requiredAnswerIndex);
            return requiredFlag > 0
                && clearedFlags != null
                && clearedFlags.TryGetValue(requiredQuestId, out var flagValue)
                && flagValue == requiredFlag;
        }

        internal static List<ushort> ComputeAcceptableQuests(
            int characterLevel,
            int characterJob,
            int growType,
            HashSet<int> clearedQuestIds,
            Dictionary<int, int> clearedFlags,
            ISet<int> allowedCreatureKinds)
        {
            var result = new List<ushort>();
            var prerequisiteState = new QuestPrerequisiteEvaluationState(
                clearedQuestIds,
                clearedFlags);
            foreach (var questId in QuestCatalog.OrderedIds)
            {
                if (questId <= 0 || questId > 29999)
                    continue;

                var quest = QuestCatalog.Get(questId);
                if (quest == null
                    || ParseExposedValue(quest.ExposedByNpc) == 0
                    || quest.IsEvent)
                {
                    continue;
                }

                if (quest.CreatureKind >= 0
                    && (allowedCreatureKinds == null
                        || !allowedCreatureKinds.Contains(quest.CreatureKind)))
                {
                    continue;
                }

                if (quest.ExpertJobType >= 0 && quest.ExpertJobLevel >= 0)
                    continue;

                var grade = (quest.Grade ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                if (grade == "[training]"
                    && !QuestData.IsThereDailyTrainingQuestList(
                        characterLevel,
                        quest.NpcIndex))
                {
                    continue;
                }

                if (!IsSelectableGrade(grade))
                    continue;

                var targetCharacter = (quest.TargetCharacter ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                if (targetCharacter.Length > 0
                    && !MatchesTargetCharacter(targetCharacter, characterJob))
                {
                    continue;
                }

                var minimumLevel = quest.Level != null && quest.Level.Length > 0
                    ? quest.Level[0]
                    : 1;
                var maximumLevel = quest.Level != null && quest.Level.Length > 1
                    ? quest.Level[1]
                    : 99;
                if (characterLevel < minimumLevel || characterLevel > maximumLevel)
                    continue;

                var job = (quest.Job ?? string.Empty).Trim().ToLowerInvariant();
                if (job.Length > 0
                    && job != "[all]"
                    && !MatchesJob(job, characterJob))
                {
                    continue;
                }

                var jobChangeQuest = quest.JobChangeQuestValue;
                if (jobChangeQuest == 2 || jobChangeQuest == 3)
                {
                    var firstGrow = growType & 0xF;
                    if (quest.GrowType != -1 && quest.GrowType != firstGrow)
                        continue;
                }
                else if (quest.GrowType != -1
                    && jobChangeQuest != 1
                    && jobChangeQuest != 10
                    && jobChangeQuest != 20
                    && growType >= 0
                    && quest.GrowType != growType)
                {
                    continue;
                }

                var repeatable = grade == "[daily]"
                    || grade == "[normaly repeat]"
                    || grade == "[special daily]";
                if (!repeatable && clearedQuestIds.Contains(questId))
                    continue;
                var prerequisiteDefinition = QuestPrerequisiteCatalog.Get(questId);
                if (prerequisiteDefinition == null
                    || !prerequisiteDefinition.Evaluate(
                        prerequisiteState).IsAllowed)
                {
                    continue;
                }

                result.Add((ushort)questId);
            }

            FileLogger.Log(
                $"[QuestRelationIndex] acceptable={result.Count} " +
                $"job={characterJob} lv={characterLevel} grow={growType}");
            return result;
        }

        internal static bool IsQuestClearQuest(int questId)
            => IsQuestClearQuest(QuestCatalog.Get(questId));

        internal static bool IsQuestClearQuest(QuestFile quest)
        {
            var tag = QuestData.NormalizeQuestTag(
                quest?.Type);
            return tag == "quest clear" || tag == "clear quest";
        }

        internal static List<int> GetQuestClearRequiredQuestIds(int questId)
        {
            var quest = QuestCatalog.Get(questId);
            if (!IsQuestClearQuest(questId) || quest == null)
                return new List<int>();

            var values = QuestData.ParseIntList(quest.IntData);
            values.RemoveAll(id => id <= 0);
            return values;
        }

        internal static List<QuestRewardItem> GetCarryForwardEventItems(
            int questId)
        {
            var eventItems = QuestData.GetEventItems(questId);
            if (eventItems.Count == 0)
                return new List<QuestRewardItem>();

            var eventItemIds = new HashSet<int>();
            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId > 0 && eventItem.Count > 0)
                    eventItemIds.Add(eventItem.ItemId);
            }
            if (eventItemIds.Count == 0)
                return new List<QuestRewardItem>();

            var carryForward = new Dictionary<int, int>();
            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId <= 0 || eventItem.Count <= 0)
                    continue;
                if (!HasDownstreamSeekingConsumer(
                        questId,
                        eventItem.ItemId))
                    continue;

                if (!carryForward.TryGetValue(
                        eventItem.ItemId,
                        out var currentCount)
                    || currentCount < eventItem.Count)
                {
                    carryForward[eventItem.ItemId] = eventItem.Count;
                }
            }

            var result = new List<QuestRewardItem>();
            foreach (var pair in carryForward)
            {
                result.Add(new QuestRewardItem
                {
                    ItemId = pair.Key,
                    Count = pair.Value,
                });
            }
            return result;
        }

        private static bool HasDownstreamSeekingConsumer(
            int questId,
            int itemId)
        {
            if (questId <= 0 || itemId <= 0)
                return false;

            var pending = new Queue<int>();
            var visited = new HashSet<int> { questId };
            EnqueueSuccessors(questId, pending);
            while (pending.Count > 0)
            {
                var nextQuestId = pending.Dequeue();
                if (!visited.Add(nextQuestId))
                    continue;

                if (ContainsItem(
                        QuestData.GetEventItems(nextQuestId),
                        itemId))
                {
                    continue;
                }
                if (ContainsItem(
                        QuestTargetIndex.GetSeekingConsumeItems(nextQuestId),
                        itemId))
                {
                    return true;
                }

                EnqueueSuccessors(nextQuestId, pending);
            }
            return false;
        }

        private static void EnqueueSuccessors(
            int questId,
            Queue<int> pending)
        {
            if (!SuccessorQuestIds.Value.TryGetValue(
                    questId,
                    out var successors))
            {
                return;
            }

            foreach (var successor in successors)
                pending.Enqueue(successor);
        }

        private static Dictionary<int, int[]> BuildSuccessorQuestIds()
        {
            var mutable = new Dictionary<int, List<int>>();
            foreach (var nextQuestId in QuestCatalog.OrderedIds)
            {
                var nextQuest = QuestCatalog.Get(nextQuestId);
                if (nextQuest == null)
                    continue;

                var definition = QuestPrerequisiteCatalog.Get(nextQuestId);
                if (definition == null || !definition.IsValid)
                    continue;

                foreach (var prerequisiteId in GetPreRequiredQuests(nextQuestId))
                {
                    if (prerequisiteId <= 0
                        || prerequisiteId == nextQuestId)
                    {
                        continue;
                    }
                    if (!mutable.TryGetValue(
                            prerequisiteId,
                            out var successors))
                    {
                        successors = new List<int>();
                        mutable[prerequisiteId] = successors;
                    }
                    if (!successors.Contains(nextQuestId))
                        successors.Add(nextQuestId);
                }
            }

            var result = new Dictionary<int, int[]>(mutable.Count);
            foreach (var pair in mutable)
                result[pair.Key] = pair.Value.ToArray();
            return result;
        }

        private static Dictionary<int, int> BuildQuestionAnswerCounts()
        {
            var result = new Dictionary<int, int>();
            foreach (var questId in QuestCatalog.OrderedIds)
            {
                var quest = QuestCatalog.Get(questId);
                if (quest == null)
                    continue;

                var definition = QuestPrerequisiteCatalog.Get(questId);
                if (definition == null || !definition.IsValid)
                    continue;

                foreach (var requiredAnswer in definition.RequiredAnswers)
                {
                    var questionQuestId = requiredAnswer.QuestId;
                    var answerIndex = requiredAnswer.AnswerIndex;
                    if (questionQuestId <= 0 || answerIndex < 0)
                        continue;

                    var nextCount = answerIndex + 1;
                    if (!result.TryGetValue(
                            questionQuestId,
                            out var currentCount)
                        || nextCount > currentCount)
                    {
                        result[questionQuestId] = nextCount;
                    }
                }
            }

            FileLogger.Log(
                $"[QuestRelationIndex] question quests={result.Count}");
            return result;
        }

        private static bool IsSelectableGrade(string grade)
            => grade == string.Empty
                || grade == "[normal]"
                || grade == "[side]"
                || grade == "[sub]"
                || grade == "[epic]"
                || grade == "[training]"
                || grade == "[achievement]"
                || grade == "[daily]"
                || grade == "[daily random]"
                || grade == "[normaly repeat]"
                || grade == "[special daily]"
                || grade == "[common unique]"
                || grade == "[system]";

        private static int ParseExposedValue(string value)
            => int.TryParse((value ?? string.Empty).Trim(), out var parsed)
                ? parsed
                : -1;

        private static bool MatchesTargetCharacter(
            string targetCharacter,
            int characterJob)
        {
            var baseIndex = GetBaseJobIndex(characterJob);
            if (baseIndex < 0)
                return false;
            return targetCharacter.Contains(
                IsAtVariant(characterJob)
                    ? AtJobNames[baseIndex]
                    : JobNames[baseIndex]);
        }

        private static bool MatchesJob(string job, int characterJob)
        {
            var baseIndex = GetBaseJobIndex(characterJob);
            if (baseIndex < 0)
                return false;
            return job.Contains(
                IsAtVariant(characterJob)
                    ? AtJobNames[baseIndex]
                    : JobNames[baseIndex]);
        }

        private static int GetBaseJobIndex(int characterJob)
        {
            switch (characterJob)
            {
                case 0:
                case 9:
                case 11:
                    return 0;
                case 1:
                case 7:
                    return 1;
                case 2:
                case 5:
                    return 2;
                case 3:
                case 8:
                case 10:
                    return 3;
                case 4:
                    return 4;
                case 6:
                    return 5;
                case 12:
                    return 6;
                default:
                    return -1;
            }
        }

        private static bool IsAtVariant(int characterJob)
            => characterJob == 5
                || characterJob == 7
                || characterJob == 8
                || characterJob == 9
                || characterJob == 10
                || characterJob == 11;

        private static bool ContainsItem(
            IReadOnlyCollection<QuestRewardItem> items,
            int itemId)
        {
            foreach (var item in items)
            {
                if (item.ItemId == itemId && item.Count > 0)
                    return true;
            }
            return false;
        }

        private static readonly string[] JobNames =
        {
            "[swordman]",
            "[fighter]",
            "[gunner]",
            "[mage]",
            "[priest]",
            "[thief]",
            "[knight]",
        };

        private static readonly string[] AtJobNames =
        {
            "[at swordman]",
            "[at fighter]",
            "[at gunner]",
            "[at mage]",
            "[at priest]",
            "[at thief]",
            "[at knight]",
        };
    }
}
