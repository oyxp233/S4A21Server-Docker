using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DfoServer.GameWorld
{
    internal enum SequentialDungeonCapabilityResolution
    {
        Absent = 0,
        Resolved = 1,
        Ambiguous = 2,
    }

    internal sealed class SequentialDungeonRewardGroup
    {
        internal SequentialDungeonRewardGroup(
            int weight,
            int rewardGroupItemId,
            int cardState)
        {
            if (weight <= 0)
                throw new ArgumentOutOfRangeException(nameof(weight));
            if (rewardGroupItemId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rewardGroupItemId));
            }
            if (cardState < 0)
                throw new ArgumentOutOfRangeException(nameof(cardState));

            Weight = weight;
            RewardGroupItemId = rewardGroupItemId;
            CardState = cardState;
        }

        internal int Weight { get; }
        internal int RewardGroupItemId { get; }
        internal int CardState { get; }
    }

    internal sealed class SequentialDungeonDefinition
    {
        private readonly ReadOnlyCollection<int> _dungeonIds;
        private readonly ReadOnlyCollection<int> _prerequisiteDungeonIds;
        private readonly ReadOnlyCollection<int> _monsterIds;
        private readonly ReadOnlyCollection<int> _entranceExceptDungeonIds;
        private readonly ReadOnlyCollection<int> _rewardableDungeonIds;
        private readonly ReadOnlyCollection<int> _alwaysVisibleDungeonIds;
        private readonly ReadOnlyCollection<SequentialDungeonRewardGroup>
            _clearRewardGroups;
        private readonly HashSet<int> _monsterIdSet;

        internal SequentialDungeonDefinition(
            int groupKey,
            byte difficulty,
            bool isAntonDungeonSequence,
            IEnumerable<int> dungeonIds,
            IEnumerable<int> monsterIds,
            bool showIndividualProcess,
            IEnumerable<int> entranceExceptDungeonIds,
            IEnumerable<int> rewardableDungeonIds,
            IEnumerable<int> alwaysVisibleDungeonIds,
            IEnumerable<SequentialDungeonRewardGroup> clearRewardGroups)
        {
            if (groupKey <= 0)
                throw new ArgumentOutOfRangeException(nameof(groupKey));

            var copiedDungeonIds = CopyPositiveUnique(
                dungeonIds,
                nameof(dungeonIds),
                allowEmpty: false);
            var copiedMonsterIds = CopyPositiveUnique(
                monsterIds,
                nameof(monsterIds),
                allowEmpty: true);
            var copiedEntranceIds = CopyPositiveUnique(
                entranceExceptDungeonIds,
                nameof(entranceExceptDungeonIds),
                allowEmpty: true);
            var copiedRewardableIds = CopyPositiveUnique(
                rewardableDungeonIds,
                nameof(rewardableDungeonIds),
                allowEmpty: true);
            var copiedAlwaysVisibleIds = CopyPositiveUnique(
                alwaysVisibleDungeonIds,
                nameof(alwaysVisibleDungeonIds),
                allowEmpty: true);
            var dungeonIdSet = new HashSet<int>(copiedDungeonIds);

            EnsureSubset(
                copiedEntranceIds,
                dungeonIdSet,
                nameof(entranceExceptDungeonIds));
            EnsureSubset(
                copiedRewardableIds,
                dungeonIdSet,
                nameof(rewardableDungeonIds));
            EnsureSubset(
                copiedAlwaysVisibleIds,
                dungeonIdSet,
                nameof(alwaysVisibleDungeonIds));

            var entranceSet = new HashSet<int>(copiedEntranceIds);
            var prerequisites = copiedDungeonIds
                .Where(dungeonId => !entranceSet.Contains(dungeonId))
                .ToList();
            if (prerequisites.Count > 31)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(entranceExceptDungeonIds),
                    "Sequential route masks can contain at most 31 prerequisites.");
            }

            var copiedRewards = (clearRewardGroups
                    ?? Enumerable.Empty<SequentialDungeonRewardGroup>())
                .ToList();
            var totalRewardWeight = 0L;
            foreach (var reward in copiedRewards)
            {
                if (reward == null
                    || reward.Weight <= 0
                    || reward.RewardGroupItemId <= 0
                    || reward.CardState < 0)
                {
                    throw new ArgumentException(
                        "Sequential clear reward groups must be valid.",
                        nameof(clearRewardGroups));
                }

                totalRewardWeight += reward.Weight;
                if (totalRewardWeight > int.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(clearRewardGroups),
                        "Sequential clear reward weight exceeds Int32 capacity.");
                }
            }

            GroupKey = groupKey;
            Difficulty = difficulty;
            IsAntonDungeonSequence = isAntonDungeonSequence;
            ShowIndividualProcess = showIndividualProcess;
            _dungeonIds = copiedDungeonIds.AsReadOnly();
            _prerequisiteDungeonIds = prerequisites.AsReadOnly();
            _monsterIds = copiedMonsterIds.AsReadOnly();
            _entranceExceptDungeonIds = copiedEntranceIds.AsReadOnly();
            _rewardableDungeonIds = copiedRewardableIds.AsReadOnly();
            _alwaysVisibleDungeonIds = copiedAlwaysVisibleIds.AsReadOnly();
            _clearRewardGroups = copiedRewards.AsReadOnly();
            _monsterIdSet = new HashSet<int>(copiedMonsterIds);
        }

        internal int GroupKey { get; }
        internal byte Difficulty { get; }
        internal bool IsAntonDungeonSequence { get; }
        internal IReadOnlyList<int> DungeonIds => _dungeonIds;
        internal IReadOnlyList<int> PrerequisiteDungeonIds =>
            _prerequisiteDungeonIds;
        internal IReadOnlyCollection<int> MonsterIds => _monsterIds;
        internal bool ShowIndividualProcess { get; }
        internal IReadOnlyCollection<int> EntranceExceptDungeonIds =>
            _entranceExceptDungeonIds;
        internal IReadOnlyCollection<int> RewardableDungeonIds =>
            _rewardableDungeonIds;
        internal IReadOnlyCollection<int> AlwaysVisibleDungeonIds =>
            _alwaysVisibleDungeonIds;
        internal IReadOnlyList<SequentialDungeonRewardGroup> ClearRewardGroups =>
            _clearRewardGroups;

        internal int IndexOf(int dungeonId) => _dungeonIds.IndexOf(dungeonId);

        internal bool ContainsMonster(int monsterId) =>
            _monsterIdSet.Contains(monsterId);

        private static List<int> CopyPositiveUnique(
            IEnumerable<int> values,
            string parameterName,
            bool allowEmpty)
        {
            var copied = (values ?? Enumerable.Empty<int>()).ToList();
            if ((!allowEmpty && copied.Count == 0)
                || copied.Any(value => value <= 0)
                || copied.Distinct().Count() != copied.Count)
            {
                throw new ArgumentException(
                    "Sequential identifiers must be positive and unique.",
                    parameterName);
            }

            return copied;
        }

        private static void EnsureSubset(
            IEnumerable<int> values,
            HashSet<int> dungeonIds,
            string parameterName)
        {
            if (values.Any(value => !dungeonIds.Contains(value)))
            {
                throw new ArgumentException(
                    "Sequential capabilities must reference a configured dungeon.",
                    parameterName);
            }
        }
    }

    internal sealed class SequentialDungeonDefinitionCatalog
    {
        private const string DefinitionPath =
            "etc/sequential_dungeon_info.etc";

        private static readonly Lazy<SequentialDungeonDefinitionCatalog>
            CurrentCatalog =
                new Lazy<SequentialDungeonDefinitionCatalog>(LoadCurrent);

        private readonly ReadOnlyCollection<SequentialDungeonDefinition>
            _definitions;
        private readonly Dictionary<int, SequentialDungeonDefinition>
            _byGroupKey;
        private readonly Dictionary<int, List<SequentialDungeonDefinition>>
            _byDungeon;
        private readonly Dictionary<int, SequentialDungeonDefinition>
            _primaryByDungeon;
        private readonly Dictionary<int, SequentialDungeonDefinition>
            _entranceByDungeon;
        private readonly Dictionary<int, SequentialDungeonDefinition>
            _rewardableByDungeon;

        private SequentialDungeonDefinitionCatalog(
            string sourcePath,
            IEnumerable<SequentialDungeonDefinition> definitions)
        {
            sourcePath = string.IsNullOrWhiteSpace(sourcePath)
                ? DefinitionPath
                : sourcePath;
            var copied = (definitions
                    ?? Enumerable.Empty<SequentialDungeonDefinition>())
                .Where(definition => definition != null)
                .ToList();
            _definitions = copied.AsReadOnly();
            _byGroupKey = copied.ToDictionary(
                definition => definition.GroupKey);
            _byDungeon = new Dictionary<
                int,
                List<SequentialDungeonDefinition>>();
            foreach (var definition in copied)
            {
                foreach (var dungeonId in definition.DungeonIds)
                {
                    if (!_byDungeon.TryGetValue(
                            dungeonId,
                            out var candidates))
                    {
                        candidates = new List<SequentialDungeonDefinition>();
                        _byDungeon[dungeonId] = candidates;
                    }
                    candidates.Add(definition);
                }
            }

            _primaryByDungeon = BuildPrimaryIndex(sourcePath, _byDungeon);
            _entranceByDungeon = BuildCapabilityIndex(
                sourcePath,
                _byDungeon,
                (definition, dungeonId) =>
                    definition.EntranceExceptDungeonIds.Contains(dungeonId),
                "entrance");
            _rewardableByDungeon = BuildCapabilityIndex(
                sourcePath,
                _byDungeon,
                (definition, dungeonId) =>
                    definition.RewardableDungeonIds.Contains(dungeonId),
                "rewardable");
        }

        internal static SequentialDungeonDefinitionCatalog Current =>
            CurrentCatalog.Value;

        internal IReadOnlyList<SequentialDungeonDefinition> Definitions =>
            _definitions;

        internal bool TryGetByGroupKey(
            int groupKey,
            out SequentialDungeonDefinition definition)
        {
            definition = null;
            return groupKey > 0
                && _byGroupKey.TryGetValue(groupKey, out definition);
        }

        internal bool TryResolvePrimaryByDungeonId(
            int dungeonId,
            out SequentialDungeonDefinition definition)
        {
            return ResolvePrimaryByDungeonId(dungeonId, out definition)
                == SequentialDungeonCapabilityResolution.Resolved;
        }

        internal SequentialDungeonCapabilityResolution ResolvePrimaryByDungeonId(
            int dungeonId,
            out SequentialDungeonDefinition definition)
        {
            definition = null;
            if (dungeonId <= 0
                || !_byDungeon.TryGetValue(dungeonId, out var candidates))
            {
                return SequentialDungeonCapabilityResolution.Absent;
            }

            if (_primaryByDungeon.TryGetValue(dungeonId, out definition))
                return SequentialDungeonCapabilityResolution.Resolved;

            var individual = candidates
                .Where(value => value.ShowIndividualProcess)
                .ToList();
            if (individual.Count == 1)
            {
                definition = individual[0];
                return SequentialDungeonCapabilityResolution.Resolved;
            }
            if (individual.Count == 0 && candidates.Count == 1)
            {
                definition = candidates[0];
                return SequentialDungeonCapabilityResolution.Resolved;
            }
            return SequentialDungeonCapabilityResolution.Ambiguous;
        }

        internal bool TryResolveEntranceByDungeonId(
            int dungeonId,
            out SequentialDungeonDefinition definition)
        {
            return ResolveEntranceByDungeonId(dungeonId, out definition)
                == SequentialDungeonCapabilityResolution.Resolved;
        }

        internal SequentialDungeonCapabilityResolution ResolveEntranceByDungeonId(
            int dungeonId,
            out SequentialDungeonDefinition definition)
        {
            if (dungeonId > 0
                && _entranceByDungeon.TryGetValue(dungeonId, out definition))
            {
                return SequentialDungeonCapabilityResolution.Resolved;
            }
            return ResolveCapabilityByDungeonId(
                dungeonId,
                value => value.EntranceExceptDungeonIds.Contains(dungeonId),
                out definition);
        }

        internal bool TryResolveRewardableByDungeonId(
            int dungeonId,
            out SequentialDungeonDefinition definition)
        {
            return ResolveRewardableByDungeonId(dungeonId, out definition)
                == SequentialDungeonCapabilityResolution.Resolved;
        }

        internal SequentialDungeonCapabilityResolution ResolveRewardableByDungeonId(
            int dungeonId,
            out SequentialDungeonDefinition definition)
        {
            if (dungeonId > 0
                && _rewardableByDungeon.TryGetValue(dungeonId, out definition))
            {
                return SequentialDungeonCapabilityResolution.Resolved;
            }
            return ResolveCapabilityByDungeonId(
                dungeonId,
                value => value.RewardableDungeonIds.Contains(dungeonId),
                out definition);
        }

        internal bool IsUniqueAntonAwakeningDefinition(
            SequentialDungeonDefinition definition)
        {
            if (definition == null)
                return false;

            var candidates = Definitions
                .Where(IsAntonAwakeningCandidate)
                .ToList();
            return candidates.Count == 1
                && candidates[0].GroupKey == definition.GroupKey;
        }

        internal bool ContainsConfiguredMonster(
            int dungeonId,
            int monsterId)
        {
            if (dungeonId <= 0
                || monsterId <= 0
                || !_byDungeon.TryGetValue(dungeonId, out var candidates))
            {
                return false;
            }

            return candidates.Any(
                definition => definition.ContainsMonster(monsterId));
        }

        private SequentialDungeonCapabilityResolution ResolveCapabilityByDungeonId(
            int dungeonId,
            Func<SequentialDungeonDefinition, bool> predicate,
            out SequentialDungeonDefinition definition)
        {
            definition = null;
            if (dungeonId <= 0
                || !_byDungeon.TryGetValue(dungeonId, out var candidates))
            {
                return SequentialDungeonCapabilityResolution.Absent;
            }

            var matching = candidates.Where(predicate).ToList();
            if (matching.Count == 1)
            {
                definition = matching[0];
                return SequentialDungeonCapabilityResolution.Resolved;
            }
            return matching.Count == 0
                ? SequentialDungeonCapabilityResolution.Absent
                : SequentialDungeonCapabilityResolution.Ambiguous;
        }

        private static bool IsAntonAwakeningCandidate(
            SequentialDungeonDefinition definition)
            => definition != null
                && definition.IsAntonDungeonSequence
                && definition.ShowIndividualProcess
                && definition.EntranceExceptDungeonIds.Count > 0
                && definition.RewardableDungeonIds.Count > 0
                && definition.ClearRewardGroups.Count > 0;

        internal static SequentialDungeonDefinitionCatalog Parse(
            string text,
            Func<IReadOnlyList<int>, byte?> difficultyResolver)
            => Parse(
                text,
                difficultyResolver,
                _ => false,
                DefinitionPath);

        internal static SequentialDungeonDefinitionCatalog Parse(
            string text,
            Func<IReadOnlyList<int>, byte?> difficultyResolver,
            Func<IReadOnlyList<int>, bool> antonDungeonSequenceResolver)
            => Parse(
                text,
                difficultyResolver,
                antonDungeonSequenceResolver,
                DefinitionPath);

        private static SequentialDungeonDefinitionCatalog Parse(
            string text,
            Func<IReadOnlyList<int>, byte?> difficultyResolver,
            Func<IReadOnlyList<int>, bool> antonDungeonSequenceResolver,
            string sourcePath)
        {
            if (difficultyResolver == null)
                throw new ArgumentNullException(nameof(difficultyResolver));
            if (antonDungeonSequenceResolver == null)
            {
                throw new ArgumentNullException(
                    nameof(antonDungeonSequenceResolver));
            }
            sourcePath = string.IsNullOrWhiteSpace(sourcePath)
                ? DefinitionPath
                : sourcePath;
            if (string.IsNullOrWhiteSpace(text))
            {
                return new SequentialDungeonDefinitionCatalog(
                    sourcePath,
                    Array.Empty<SequentialDungeonDefinition>());
            }

            var root = new ScriptParser().Parse(text);
            var definitions = new List<SequentialDungeonDefinition>();
            var seenGroupKeys = new HashSet<int>();
            var conflictedGroupKeys = new HashSet<int>();
            foreach (var section in root.GetChildren("sequential dungeon"))
            {
                var hasGroupKey = TryReadGroupKey(
                    section,
                    text,
                    out var groupKey);
                if (!section.HasEndTag)
                {
                    LogDiagnostic(
                        sourcePath,
                        hasGroupKey ? groupKey.ToString() : "unknown",
                        "sequential dungeon",
                        "missing closing tag at line "
                            + (section.StartLineIndex + 1));
                    continue;
                }
                if (!hasGroupKey)
                {
                    LogDiagnostic(
                        sourcePath,
                        "unknown",
                        "group key",
                        "expected exactly one positive integer at line "
                            + (section.StartLineIndex + 1));
                    continue;
                }

                if (!seenGroupKeys.Add(groupKey))
                {
                    definitions.RemoveAll(
                        definition => definition.GroupKey == groupKey);
                    conflictedGroupKeys.Add(groupKey);
                    LogDiagnostic(
                        sourcePath,
                        groupKey.ToString(),
                        "group key",
                        "duplicate group key");
                    continue;
                }

                if (!TryParseDefinition(
                        section,
                        text,
                        groupKey,
                        difficultyResolver,
                        antonDungeonSequenceResolver,
                        out var definition,
                        out var field,
                        out var reason))
                {
                    LogDiagnostic(
                        sourcePath,
                        groupKey.ToString(),
                        field,
                        reason);
                    continue;
                }

                definitions.Add(definition);
            }

            if (conflictedGroupKeys.Count > 0)
            {
                definitions.RemoveAll(
                    definition => conflictedGroupKeys.Contains(
                        definition.GroupKey));
            }

            return new SequentialDungeonDefinitionCatalog(
                sourcePath,
                definitions);
        }

        internal static SequentialDungeonDefinitionCatalog Load(
            string sourcePath,
            Func<string, string> readText,
            Func<IReadOnlyList<int>, byte?> difficultyResolver)
            => Load(
                sourcePath,
                readText,
                difficultyResolver,
                _ => false);

        internal static SequentialDungeonDefinitionCatalog Load(
            string sourcePath,
            Func<string, string> readText,
            Func<IReadOnlyList<int>, byte?> difficultyResolver,
            Func<IReadOnlyList<int>, bool> antonDungeonSequenceResolver)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException(
                    "A sequential ETC source path is required.",
                    nameof(sourcePath));
            if (readText == null)
                throw new ArgumentNullException(nameof(readText));
            if (difficultyResolver == null)
                throw new ArgumentNullException(nameof(difficultyResolver));
            if (antonDungeonSequenceResolver == null)
            {
                throw new ArgumentNullException(
                    nameof(antonDungeonSequenceResolver));
            }

            try
            {
                var catalog = Parse(
                    readText(sourcePath),
                    difficultyResolver,
                    antonDungeonSequenceResolver,
                    sourcePath);
                foreach (var definition in catalog.Definitions)
                {
                    LogDiagnostic(
                        sourcePath,
                        definition.GroupKey.ToString(),
                        "definition",
                        "loaded difficulty=" + definition.Difficulty
                            + " dungeons="
                            + string.Join(",", definition.DungeonIds));
                }
                LogDiagnostic(
                    sourcePath,
                    "none",
                    "catalog",
                    $"loaded groups={catalog.Definitions.Count} "
                        + $"dungeons={catalog._byDungeon.Count}");
                return catalog;
            }
            catch (Exception ex)
            {
                LogDiagnostic(
                    sourcePath,
                    "unknown",
                    "file",
                    "load failed: " + ex.Message);
                return new SequentialDungeonDefinitionCatalog(
                    sourcePath,
                    Array.Empty<SequentialDungeonDefinition>());
            }
        }

        private static SequentialDungeonDefinitionCatalog LoadCurrent() =>
            Load(
                DefinitionPath,
                PvfArchiveAccessor.ReadText,
                ResolveDifficulty,
                ResolveAntonDungeonSequence);

        private static byte? ResolveDifficulty(
            IReadOnlyList<int> dungeonIds)
        {
            HashSet<int> commonDifficulties = null;
            foreach (var dungeonId in dungeonIds)
            {
                var dungeon = Dungeon.GetDungeonFile(dungeonId);
                var difficulties = new HashSet<int>(
                    (dungeon?.DesignateDungeonDifficulty
                        ?? Array.Empty<int>())
                    .Where(value => value >= 0 && value <= 4));
                if (difficulties.Count == 0)
                    return null;

                if (commonDifficulties == null)
                    commonDifficulties = difficulties;
                else
                    commonDifficulties.IntersectWith(difficulties);
            }

            return commonDifficulties != null
                && commonDifficulties.Count == 1
                    ? (byte?)commonDifficulties.Single()
                    : null;
        }

        private static bool ResolveAntonDungeonSequence(
            IReadOnlyList<int> dungeonIds)
        {
            if (dungeonIds == null || dungeonIds.Count == 0)
                return false;

            foreach (var dungeonId in dungeonIds)
            {
                try
                {
                    var dungeon = Dungeon.GetDungeonFile(dungeonId);
                    if (dungeon == null
                        || !dungeon.HasTag("anton dungeon"))
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryParseDefinition(
            ScriptNode section,
            string text,
            int groupKey,
            Func<IReadOnlyList<int>, byte?> difficultyResolver,
            Func<IReadOnlyList<int>, bool> antonDungeonSequenceResolver,
            out SequentialDungeonDefinition definition,
            out string field,
            out string reason)
        {
            definition = null;
            field = string.Empty;
            reason = string.Empty;

            if (!TryReadRequiredPositiveList(
                    section,
                    "dungeon index check",
                    text,
                    out var dungeonIds,
                    out reason))
            {
                field = "dungeon index check";
                return false;
            }
            if (!TryReadOptionalPositiveList(
                    section,
                    "monster index check",
                    text,
                    out var monsterIds,
                    out reason))
            {
                field = "monster index check";
                return false;
            }
            if (!TryReadFlag(
                    section,
                    "show individual process",
                    text,
                    out var showIndividualProcess,
                    out reason))
            {
                field = "show individual process";
                return false;
            }
            if (!TryReadOptionalPositiveList(
                    section,
                    "entrance except dungeon",
                    text,
                    out var entranceExceptDungeonIds,
                    out reason))
            {
                field = "entrance except dungeon";
                return false;
            }
            if (!TryReadOptionalPositiveList(
                    section,
                    "rewardable dungeon index",
                    text,
                    out var rewardableDungeonIds,
                    out reason))
            {
                field = "rewardable dungeon index";
                return false;
            }
            if (!TryReadOptionalPositiveList(
                    section,
                    "always visible dungeon",
                    text,
                    out var alwaysVisibleDungeonIds,
                    out reason))
            {
                field = "always visible dungeon";
                return false;
            }
            if (!TryReadClearRewardGroups(
                    section,
                    text,
                    out var clearRewardGroups,
                    out reason))
            {
                field = "clear reward item";
                return false;
            }

            var readOnlyDungeonIds =
                new ReadOnlyCollection<int>(dungeonIds);
            byte? difficulty;
            try
            {
                difficulty = difficultyResolver(readOnlyDungeonIds);
            }
            catch (Exception ex)
            {
                field = "difficulty";
                reason = "resolution failed: " + ex.Message;
                return false;
            }

            if (!difficulty.HasValue)
            {
                field = "difficulty";
                reason = "dungeons do not have one common designated difficulty";
                return false;
            }

            bool isAntonDungeonSequence;
            try
            {
                isAntonDungeonSequence = antonDungeonSequenceResolver(
                    readOnlyDungeonIds);
            }
            catch (Exception ex)
            {
                field = "Anton dungeon classification";
                reason = "resolution failed: " + ex.Message;
                return false;
            }

            try
            {
                definition = new SequentialDungeonDefinition(
                    groupKey,
                    difficulty.Value,
                    isAntonDungeonSequence,
                    dungeonIds,
                    monsterIds,
                    showIndividualProcess,
                    entranceExceptDungeonIds,
                    rewardableDungeonIds,
                    alwaysVisibleDungeonIds,
                    clearRewardGroups);
                return true;
            }
            catch (ArgumentException ex)
            {
                field = "definition";
                reason = ex.Message;
                return false;
            }
        }

        private static bool TryReadGroupKey(
            ScriptNode section,
            string text,
            out int groupKey)
        {
            groupKey = 0;
            var tokens = ReadTokens(section, text);
            return tokens.Count == 1
                && int.TryParse(tokens[0], out groupKey)
                && groupKey > 0;
        }

        private static bool TryReadRequiredPositiveList(
            ScriptNode section,
            string tag,
            string text,
            out List<int> values,
            out string error)
        {
            values = null;
            error = string.Empty;
            var nodes = section.GetChildren(tag);
            if (!TryValidateClosedNodes(nodes, out error))
                return false;
            if (nodes.Count != 1)
            {
                error = $"{tag} must occur exactly once";
                return false;
            }

            return TryParsePositiveList(
                nodes[0],
                tag,
                text,
                allowEmpty: false,
                out values,
                out error);
        }

        private static bool TryReadOptionalPositiveList(
            ScriptNode section,
            string tag,
            string text,
            out List<int> values,
            out string error)
        {
            values = new List<int>();
            error = string.Empty;
            var nodes = section.GetChildren(tag);
            if (!TryValidateClosedNodes(nodes, out error))
                return false;
            if (nodes.Count > 1)
            {
                error = $"{tag} must not be repeated";
                return false;
            }
            if (nodes.Count == 0)
                return true;

            return TryParsePositiveList(
                nodes[0],
                tag,
                text,
                allowEmpty: true,
                out values,
                out error);
        }

        private static bool TryParsePositiveList(
            ScriptNode node,
            string tag,
            string text,
            bool allowEmpty,
            out List<int> values,
            out string error)
        {
            values = new List<int>();
            error = string.Empty;
            var tokens = ReadTokens(node, text);
            if (!allowEmpty && tokens.Count == 0)
            {
                error = $"{tag} must not be empty";
                return false;
            }

            foreach (var token in tokens)
            {
                if (!int.TryParse(token, out var value) || value <= 0)
                {
                    error = $"{tag} contains invalid token '{token}'";
                    return false;
                }
                values.Add(value);
            }

            if (values.Distinct().Count() != values.Count)
            {
                error = $"{tag} contains duplicate identifiers";
                return false;
            }
            return true;
        }

        private static bool TryReadFlag(
            ScriptNode section,
            string tag,
            string text,
            out bool value,
            out string error)
        {
            value = false;
            error = string.Empty;
            var nodes = section.GetChildren(tag);
            if (nodes.Count > 1)
            {
                error = $"{tag} must not be repeated";
                return false;
            }
            if (nodes.Count == 0)
                return true;

            var node = nodes[0];
            var isImplicitEmptyFlag = string.Equals(
                    tag,
                    "show individual process",
                    StringComparison.Ordinal)
                && node?.HasEndTag != true
                && node?.DataItems?.Count == 0;
            if (node?.HasEndTag != true && !isImplicitEmptyFlag)
            {
                error = "missing closing tag at line "
                    + ((node?.StartLineIndex ?? -1) + 1);
                return false;
            }
            if (node?.DataItems?.Count > 0)
            {
                error = $"{tag} flag must not contain data";
                return false;
            }

            value = true;
            return true;
        }

        private static bool TryReadClearRewardGroups(
            ScriptNode section,
            string text,
            out List<SequentialDungeonRewardGroup> rewards,
            out string error)
        {
            rewards = new List<SequentialDungeonRewardGroup>();
            error = string.Empty;
            var nodes = section.GetChildren("clear reward item");
            if (!TryValidateClosedNodes(nodes, out error))
                return false;
            if (nodes.Count > 1)
            {
                error = "clear reward item must not be repeated";
                return false;
            }
            if (nodes.Count == 0)
                return true;

            var tokens = ReadTokens(nodes[0], text);
            if (tokens.Count == 0 || tokens.Count % 3 != 0)
            {
                error = "clear reward item must contain complete triples";
                return false;
            }

            var totalWeight = 0L;
            for (var index = 0; index < tokens.Count; index += 3)
            {
                if (!int.TryParse(tokens[index], out var weight)
                    || !int.TryParse(
                        tokens[index + 1],
                        out var rewardGroupItemId)
                    || !int.TryParse(tokens[index + 2], out var cardState)
                    || weight <= 0
                    || rewardGroupItemId <= 0
                    || cardState < 0)
                {
                    error = "clear reward item contains an invalid triple";
                    return false;
                }

                totalWeight += weight;
                if (totalWeight > int.MaxValue)
                {
                    error = "clear reward item weight exceeds Int32 capacity";
                    return false;
                }
                rewards.Add(new SequentialDungeonRewardGroup(
                    weight,
                    rewardGroupItemId,
                    cardState));
            }
            return true;
        }

        private static bool TryValidateClosedNodes(
            IReadOnlyList<ScriptNode> nodes,
            out string error)
        {
            error = string.Empty;
            if (nodes == null)
                return true;

            foreach (var node in nodes)
            {
                if (node?.HasEndTag == true)
                    continue;

                error = "missing closing tag at line "
                    + ((node?.StartLineIndex ?? -1) + 1);
                return false;
            }
            return true;
        }

        private static List<string> ReadTokens(
            ScriptNode node,
            string text)
        {
            var tokens = new List<string>();
            if (node?.DataItems == null)
                return tokens;

            foreach (var dataItem in node.DataItems)
            {
                tokens.AddRange(ScriptValueTokenizer.Tokenize(
                    dataItem.GetContent(text)));
            }
            return tokens;
        }

        private static Dictionary<int, SequentialDungeonDefinition>
            BuildPrimaryIndex(
                string sourcePath,
                IReadOnlyDictionary<
                    int,
                    List<SequentialDungeonDefinition>> byDungeon)
        {
            var result = new Dictionary<int, SequentialDungeonDefinition>();
            foreach (var pair in byDungeon)
            {
                var individual = pair.Value
                    .Where(value => value.ShowIndividualProcess)
                    .ToList();
                if (individual.Count == 1)
                {
                    result[pair.Key] = individual[0];
                }
                else if (individual.Count == 0 && pair.Value.Count == 1)
                {
                    result[pair.Key] = pair.Value[0];
                }
                else
                {
                    LogCapabilityConflict(
                        sourcePath,
                        "primary",
                        pair.Key,
                        pair.Value);
                }
            }
            return result;
        }

        private static Dictionary<int, SequentialDungeonDefinition>
            BuildCapabilityIndex(
                string sourcePath,
                IReadOnlyDictionary<
                    int,
                    List<SequentialDungeonDefinition>> byDungeon,
                Func<SequentialDungeonDefinition, int, bool> predicate,
                string capability)
        {
            var result = new Dictionary<int, SequentialDungeonDefinition>();
            foreach (var pair in byDungeon)
            {
                var candidates = pair.Value
                    .Where(definition => predicate(definition, pair.Key))
                    .ToList();
                if (candidates.Count == 1)
                    result[pair.Key] = candidates[0];
                else if (candidates.Count > 1)
                    LogCapabilityConflict(
                        sourcePath,
                        capability,
                        pair.Key,
                        candidates);
            }
            return result;
        }

        private static void LogCapabilityConflict(
            string sourcePath,
            string capability,
            int dungeonId,
            IEnumerable<SequentialDungeonDefinition> candidates)
        {
            var groupKeys = candidates
                .Select(value => value.GroupKey)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
            LogDiagnostic(
                sourcePath,
                "[" + string.Join(",", groupKeys) + "]",
                capability,
                $"ambiguous capability for dungeon {dungeonId}; "
                    + "all candidate group keys="
                    + string.Join(",", groupKeys));
        }

        private static void LogDiagnostic(
            string sourcePath,
            string groupKey,
            string field,
            string reason)
        {
            FileLogger.Log(
                "[SequentialDungeonDefinitionCatalog] "
                + $"path={Sanitize(sourcePath)} "
                + $"key={Sanitize(groupKey)} "
                + $"field={Sanitize(field)} "
                + $"reason={Sanitize(reason)}");
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";
            return value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }
    }
}
