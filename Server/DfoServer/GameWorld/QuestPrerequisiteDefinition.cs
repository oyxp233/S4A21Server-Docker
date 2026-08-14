using System;
using System.Collections.Generic;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal enum QuestPrerequisiteBlockReason
    {
        None = 0,
        InvalidDefinition,
        MissingCompletedQuest,
        RequiredAnswerMismatch,
        CharacterCollision,
        AccountCollisionStateUnavailable,
        AccountCollision,
    }

    internal readonly struct QuestPrerequisiteDecision
    {
        private QuestPrerequisiteDecision(
            bool isAllowed,
            QuestPrerequisiteBlockReason reason)
        {
            IsAllowed = isAllowed;
            Reason = reason;
        }

        internal bool IsAllowed { get; }
        internal QuestPrerequisiteBlockReason Reason { get; }

        internal static QuestPrerequisiteDecision Allow()
            => new QuestPrerequisiteDecision(true, QuestPrerequisiteBlockReason.None);

        internal static QuestPrerequisiteDecision Deny(
            QuestPrerequisiteBlockReason reason)
            => new QuestPrerequisiteDecision(false, reason);
    }

    internal sealed class QuestPrerequisiteEvaluationState
    {
        internal QuestPrerequisiteEvaluationState(
            ISet<int> clearedQuestIds,
            IReadOnlyDictionary<int, int> clearedFlags,
            ISet<int> activeQuestIds = null,
            ISet<int> accountClearedQuestIds = null)
        {
            ClearedQuestIds = clearedQuestIds ?? new HashSet<int>();
            ClearedFlags = clearedFlags ?? new Dictionary<int, int>();
            ActiveQuestIds = activeQuestIds ?? new HashSet<int>();
            AccountClearedQuestIds = accountClearedQuestIds;
        }

        internal ISet<int> ClearedQuestIds { get; }
        internal IReadOnlyDictionary<int, int> ClearedFlags { get; }
        internal ISet<int> ActiveQuestIds { get; }
        internal ISet<int> AccountClearedQuestIds { get; }
    }

    internal readonly struct QuestRequiredAnswer
    {
        internal QuestRequiredAnswer(int questId, int answerIndex)
        {
            QuestId = questId;
            AnswerIndex = answerIndex;
        }

        internal int QuestId { get; }
        internal int AnswerIndex { get; }
    }

    internal sealed class QuestPrerequisiteDefinition
    {
        private QuestPrerequisiteDefinition(
            int questId,
            int[][] completedQuestGroups,
            QuestRequiredAnswer[] requiredAnswers,
            int[] collisionQuestIds,
            int[] accountCollisionQuestIds,
            string[] diagnostics,
            string validationError)
        {
            QuestId = questId;
            CompletedQuestGroups = completedQuestGroups ?? Array.Empty<int[]>();
            RequiredAnswers = requiredAnswers ?? Array.Empty<QuestRequiredAnswer>();
            CollisionQuestIds = collisionQuestIds ?? Array.Empty<int>();
            AccountCollisionQuestIds = accountCollisionQuestIds ?? Array.Empty<int>();
            Diagnostics = diagnostics ?? Array.Empty<string>();
            ValidationError = validationError;
        }

        internal int QuestId { get; }
        internal IReadOnlyList<int[]> CompletedQuestGroups { get; }
        internal IReadOnlyList<QuestRequiredAnswer> RequiredAnswers { get; }
        internal IReadOnlyList<int> CollisionQuestIds { get; }
        internal IReadOnlyList<int> AccountCollisionQuestIds { get; }
        internal IReadOnlyList<string> Diagnostics { get; }
        internal string ValidationError { get; }
        internal bool IsValid => string.IsNullOrEmpty(ValidationError);

        internal QuestPrerequisiteDecision Evaluate(
            QuestPrerequisiteEvaluationState state)
        {
            if (!IsValid || state == null)
            {
                return QuestPrerequisiteDecision.Deny(
                    QuestPrerequisiteBlockReason.InvalidDefinition);
            }

            if (CompletedQuestGroups.Count > 0)
            {
                var anyGroupSatisfied = false;
                foreach (var group in CompletedQuestGroups)
                {
                    var groupSatisfied = true;
                    foreach (var prerequisiteQuestId in group)
                    {
                        if (!state.ClearedQuestIds.Contains(prerequisiteQuestId))
                        {
                            groupSatisfied = false;
                            break;
                        }
                    }

                    if (groupSatisfied)
                    {
                        anyGroupSatisfied = true;
                        break;
                    }
                }

                if (!anyGroupSatisfied)
                {
                    return QuestPrerequisiteDecision.Deny(
                        QuestPrerequisiteBlockReason.MissingCompletedQuest);
                }
            }

            foreach (var requiredAnswer in RequiredAnswers)
            {
                var expectedFlag = QuestRelationIndex
                    .GetRequiredQuestAnswerFlagValue(requiredAnswer.AnswerIndex);
                if (expectedFlag <= 0
                    || !state.ClearedFlags.TryGetValue(
                        requiredAnswer.QuestId,
                        out var actualFlag)
                    || actualFlag != expectedFlag)
                {
                    return QuestPrerequisiteDecision.Deny(
                        QuestPrerequisiteBlockReason.RequiredAnswerMismatch);
                }
            }

            foreach (var collisionQuestId in CollisionQuestIds)
            {
                if (state.ClearedQuestIds.Contains(collisionQuestId)
                    || state.ActiveQuestIds.Contains(collisionQuestId))
                {
                    return QuestPrerequisiteDecision.Deny(
                        QuestPrerequisiteBlockReason.CharacterCollision);
                }
            }

            if (AccountCollisionQuestIds.Count > 0
                && state.AccountClearedQuestIds == null)
            {
                return QuestPrerequisiteDecision.Deny(
                    QuestPrerequisiteBlockReason.AccountCollisionStateUnavailable);
            }

            foreach (var collisionQuestId in AccountCollisionQuestIds)
            {
                if (state.AccountClearedQuestIds.Contains(collisionQuestId))
                {
                    return QuestPrerequisiteDecision.Deny(
                        QuestPrerequisiteBlockReason.AccountCollision);
                }
            }

            return QuestPrerequisiteDecision.Allow();
        }

        internal static QuestPrerequisiteDefinition Parse(
            int questId,
            QuestFile quest,
            Func<int, bool> questExists)
        {
            if (quest == null)
                return Invalid(questId, "quest definition is missing");
            if (questExists == null)
                return Invalid(questId, "quest catalog predicate is missing");

            var diagnostics = new List<string>();
            var completedGroups = new List<int[]>();
            var error = ParseCompletedQuestGroups(
                questId,
                SelectGroups(
                    quest.PreRequiredQuestGroups,
                    quest.PreRequiredQuest),
                questExists,
                completedGroups,
                diagnostics);
            if (error != null)
                return Invalid(questId, error, diagnostics);

            var requiredAnswers = new List<QuestRequiredAnswer>();
            error = ParseRequiredAnswers(
                questId,
                SelectGroups(
                    quest.PreRequiredQuestAnswerGroups,
                    quest.PreRequiredQuestAnswer),
                questExists,
                requiredAnswers);
            if (error != null)
                return Invalid(questId, error, diagnostics);

            var collisionGroups = new List<int[]>();
            error = ParseCollisionQuestGroups(
                questId,
                SelectGroups(
                    quest.CollisionQuestGroups,
                    quest.CollisionQuest),
                questExists,
                "collision quest",
                collisionGroups,
                diagnostics);
            if (error != null)
                return Invalid(questId, error, diagnostics);

            var accountCollisionGroups = new List<int[]>();
            error = ParseCollisionQuestGroups(
                questId,
                SelectGroups(
                    quest.AccountCollisionQuestGroups,
                    quest.AccountCollisionQuest),
                questExists,
                "account collision quest",
                accountCollisionGroups,
                diagnostics);
            if (error != null)
                return Invalid(questId, error, diagnostics);

            return new QuestPrerequisiteDefinition(
                questId,
                completedGroups.ToArray(),
                requiredAnswers.ToArray(),
                FlattenDistinct(collisionGroups),
                FlattenDistinct(accountCollisionGroups),
                diagnostics.ToArray(),
                null);
        }

        private static QuestPrerequisiteDefinition Invalid(
            int questId,
            string error,
            IReadOnlyList<string> diagnostics = null)
            => new QuestPrerequisiteDefinition(
                questId,
                Array.Empty<int[]>(),
                Array.Empty<QuestRequiredAnswer>(),
                Array.Empty<int>(),
                Array.Empty<int>(),
                diagnostics == null
                    ? Array.Empty<string>()
                    : new List<string>(diagnostics).ToArray(),
                error ?? "invalid quest prerequisite definition");

        private static IReadOnlyList<string> SelectGroups(
            IReadOnlyList<string> groups,
            string fallback)
        {
            if (groups != null)
                return groups;
            if (fallback != null)
                return new[] { fallback };
            return Array.Empty<string>();
        }

        private static string ParseCompletedQuestGroups(
            int ownerQuestId,
            IReadOnlyList<string> rawGroups,
            Func<int, bool> questExists,
            ICollection<int[]> output,
            ICollection<string> diagnostics)
        {
            var hasDeclaredGroup = false;
            foreach (var rawGroup in rawGroups)
            {
                if (!TryParseStrictIntList(rawGroup, out var values))
                    return "pre required quest contains a non-integer token";
                if (values.Count == 0)
                    continue;
                hasDeclaredGroup = true;

                var distinct = new List<int>();
                var seen = new HashSet<int>();
                var missing = new List<int>();
                foreach (var referencedQuestId in values)
                {
                    if (referencedQuestId <= 0)
                    {
                        return $"pre required quest contains non-positive quest id " +
                            referencedQuestId;
                    }
                    if (referencedQuestId == ownerQuestId)
                        return "pre required quest contains a self reference";
                    if (!questExists(referencedQuestId))
                    {
                        missing.Add(referencedQuestId);
                        continue;
                    }
                    if (seen.Add(referencedQuestId))
                        distinct.Add(referencedQuestId);
                }

                if (missing.Count > 0)
                {
                    diagnostics.Add(
                        $"pre required quest skipped candidate group '{rawGroup}' " +
                        $"because quest(s) {string.Join(",", missing)} are unknown");
                    continue;
                }
                output.Add(distinct.ToArray());
            }

            if (hasDeclaredGroup && output.Count == 0)
                return "pre required quest has no valid candidate group";

            return null;
        }

        private static string ParseCollisionQuestGroups(
            int ownerQuestId,
            IReadOnlyList<string> rawGroups,
            Func<int, bool> questExists,
            string fieldName,
            ICollection<int[]> output,
            ICollection<string> diagnostics)
        {
            foreach (var rawGroup in rawGroups)
            {
                if (!TryParseStrictIntList(rawGroup, out var values))
                    return $"{fieldName} contains a non-integer token";
                if (values.Count == 0)
                    continue;

                var distinct = new List<int>();
                var seen = new HashSet<int>();
                foreach (var referencedQuestId in values)
                {
                    if (referencedQuestId <= 0)
                    {
                        return $"{fieldName} contains non-positive quest id " +
                            referencedQuestId;
                    }
                    if (referencedQuestId == ownerQuestId)
                        return $"{fieldName} contains a self reference";
                    if (!questExists(referencedQuestId))
                    {
                        diagnostics.Add(
                            $"{fieldName} ignored unknown quest {referencedQuestId}");
                        continue;
                    }
                    if (seen.Add(referencedQuestId))
                        distinct.Add(referencedQuestId);
                }

                if (distinct.Count > 0)
                    output.Add(distinct.ToArray());
            }

            return null;
        }

        private static string ParseRequiredAnswers(
            int ownerQuestId,
            IReadOnlyList<string> rawGroups,
            Func<int, bool> questExists,
            ICollection<QuestRequiredAnswer> output)
        {
            foreach (var rawGroup in rawGroups)
            {
                if (!TryParseStrictIntList(rawGroup, out var values))
                    return "pre required quest answer contains a non-integer token";
                if (values.Count == 0)
                    continue;
                if (values.Count % 2 != 0)
                    return "pre required quest answer must contain quest/answer pairs";

                for (var index = 0; index < values.Count; index += 2)
                {
                    var referencedQuestId = values[index];
                    var answerIndex = values[index + 1];
                    if (referencedQuestId <= 0)
                        return "pre required quest answer contains a non-positive quest id";
                    if (referencedQuestId == ownerQuestId)
                        return "pre required quest answer contains a self reference";
                    if (!questExists(referencedQuestId))
                    {
                        return $"pre required quest answer references unknown quest " +
                            referencedQuestId;
                    }
                    if (answerIndex < 0)
                        return "pre required quest answer contains a negative answer index";
                    output.Add(new QuestRequiredAnswer(
                        referencedQuestId,
                        answerIndex));
                }
            }

            return null;
        }

        private static bool TryParseStrictIntList(
            string data,
            out List<int> values)
        {
            values = new List<int>();
            if (string.IsNullOrWhiteSpace(data))
                return true;

            foreach (var token in data.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, out var value))
                    return false;
                values.Add(value);
            }
            return true;
        }

        private static int[] FlattenDistinct(IReadOnlyList<int[]> groups)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            foreach (var group in groups)
            {
                foreach (var questId in group)
                {
                    if (seen.Add(questId))
                        result.Add(questId);
                }
            }
            return result.ToArray();
        }
    }

    internal static class QuestPrerequisiteCatalog
    {
        private static readonly Lazy<IReadOnlyDictionary<int, QuestPrerequisiteDefinition>>
            Definitions = new Lazy<IReadOnlyDictionary<int, QuestPrerequisiteDefinition>>(
                BuildDefinitions);

        internal static QuestPrerequisiteDefinition Get(int questId)
            => Definitions.Value.TryGetValue(questId, out var definition)
                ? definition
                : null;

        internal static IReadOnlyDictionary<int, QuestPrerequisiteDefinition> GetAll()
            => Definitions.Value;

        private static IReadOnlyDictionary<int, QuestPrerequisiteDefinition>
            BuildDefinitions()
        {
            var result = new Dictionary<int, QuestPrerequisiteDefinition>();
            foreach (var questId in QuestCatalog.OrderedIds)
            {
                var definition = QuestPrerequisiteDefinition.Parse(
                    questId,
                    QuestCatalog.Get(questId),
                    referencedQuestId => QuestCatalog.Get(referencedQuestId) != null);
                result[questId] = definition;
                if (!definition.IsValid)
                {
                    FileLogger.Log(
                        $"[QuestPrerequisiteCatalog] invalid quest={questId}: " +
                        definition.ValidationError);
                }
                foreach (var diagnostic in definition.Diagnostics)
                {
                    FileLogger.Log(
                        $"[QuestPrerequisiteCatalog] quest={questId}: {diagnostic}");
                }
            }
            return result;
        }
    }
}
