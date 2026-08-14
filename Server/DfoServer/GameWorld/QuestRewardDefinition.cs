using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal enum QuestRewardKind
    {
        Item,
        Title,
        GrowType,
        AwakeningType,
        CreatureEvolution,
        ExpertJob,
        SlotExpansion,
        EventCreatureEvolution,
        Ridable,
        EventSkill,
        HellChallenge,
        CrackOfDimension,
    }

    internal enum QuestRewardSelectionPolicy
    {
        Forbidden,
        Required,
        Optional,
    }

    internal readonly struct QuestGoldRewardProjection
    {
        internal QuestGoldRewardProjection(
            uint fixedAmount,
            bool hasFormulaMarker)
        {
            FixedAmount = fixedAmount;
            HasFormulaMarker = hasFormulaMarker;
        }

        internal uint FixedAmount { get; }
        internal bool HasFormulaMarker { get; }
        internal bool HasFixedAmount => FixedAmount > 0;
    }

    internal sealed class QuestRewardDefinition
    {
        private readonly IReadOnlyList<QuestRewardItemRule> _fixedItems;
        private readonly IReadOnlyList<QuestRewardItemRule> _selectableItems;
        private readonly IReadOnlyList<EventCreatureEvolutionOption>
            _eventCreatureOptions;

        private QuestRewardDefinition(
            int questId,
            QuestRewardKind kind,
            int chainType,
            QuestRewardSelectionPolicy selectionPolicy,
            IReadOnlyList<QuestRewardItemRule> fixedItems,
            IReadOnlyList<QuestRewardItemRule> selectableItems,
            IReadOnlyList<EventCreatureEvolutionOption> eventCreatureOptions,
            int rewardParameter,
            int creatureKind,
            int creatureLevel,
            int questMinLevel,
            char difficulty,
            bool ignoreLevelForExperience,
            bool suppressExperience,
            int goldMultiple)
        {
            QuestId = questId;
            Kind = kind;
            ChainType = chainType;
            SelectionPolicy = selectionPolicy;
            _fixedItems = fixedItems ?? Array.Empty<QuestRewardItemRule>();
            _selectableItems = selectableItems
                ?? Array.Empty<QuestRewardItemRule>();
            _eventCreatureOptions = eventCreatureOptions
                ?? Array.Empty<EventCreatureEvolutionOption>();
            RewardParameter = rewardParameter;
            CreatureKind = creatureKind;
            CreatureLevel = creatureLevel;
            QuestMinLevel = questMinLevel;
            Difficulty = difficulty;
            IgnoreLevelForExperience = ignoreLevelForExperience;
            SuppressExperience = suppressExperience;
            GoldMultiple = goldMultiple;
        }

        internal int QuestId { get; }
        internal QuestRewardKind Kind { get; }
        internal int ChainType { get; }
        internal QuestRewardSelectionPolicy SelectionPolicy { get; }
        internal int RewardParameter { get; }
        internal int CreatureKind { get; }
        internal int CreatureLevel { get; }
        internal int QuestMinLevel { get; }
        internal char Difficulty { get; }
        internal bool IgnoreLevelForExperience { get; }
        internal bool SuppressExperience { get; }
        internal int GoldMultiple { get; }

        internal IEnumerable<int> EnumerateGrantedItemTemplateIds()
        {
            foreach (var item in _fixedItems)
            {
                if (item.ItemId > 0)
                    yield return item.ItemId;
            }
            foreach (var item in _selectableItems)
            {
                if (item.ItemId > 0)
                    yield return item.ItemId;
            }
        }

        internal bool TryProject(
            bool hasRewardSelection,
            int rewardSelectionIndex,
            int playerJob,
            int playerGrowType,
            out List<QuestRewardItem> items,
            out QuestGoldRewardProjection goldReward,
            out int rewardParameter,
            out string error)
        {
            items = new List<QuestRewardItem>();
            var hasGoldMarker = false;
            ulong fixedGoldAmount = 0;
            goldReward = default;
            rewardParameter = RewardParameter;
            error = string.Empty;

            if (!TryValidateSelectionPresence(
                    hasRewardSelection,
                    rewardSelectionIndex,
                    out error))
            {
                return false;
            }

            foreach (var rule in _fixedItems)
            {
                if (rule.ItemId == 0)
                {
                    hasGoldMarker = true;
                    if (GoldMultiple <= 0 && rule.Count > 0)
                    {
                        fixedGoldAmount += (uint)rule.Count;
                        if (fixedGoldAmount > uint.MaxValue)
                        {
                            error = "fixed gold reward exceeds uint32";
                            return false;
                        }
                    }
                    continue;
                }
                if (rule.Matches(playerJob, playerGrowType))
                    items.Add(rule.ToRewardItem());
            }

            if (hasRewardSelection && _selectableItems.Count > 0)
            {
                var eligible = new List<QuestRewardItemRule>();
                foreach (var rule in _selectableItems)
                {
                    if (rule.Matches(playerJob, playerGrowType))
                        eligible.Add(rule);
                }

                if (rewardSelectionIndex >= eligible.Count)
                {
                    error = $"reward selection index {rewardSelectionIndex} " +
                        $"is outside {eligible.Count} eligible entries";
                    return false;
                }
                items.Add(eligible[rewardSelectionIndex].ToRewardItem());
            }

            if (hasRewardSelection && _eventCreatureOptions.Count > 0)
            {
                if (rewardSelectionIndex >= _eventCreatureOptions.Count)
                {
                    error = $"event creature selection index " +
                        $"{rewardSelectionIndex} is outside " +
                        $"{_eventCreatureOptions.Count} entries";
                    return false;
                }
                rewardParameter = _eventCreatureOptions[rewardSelectionIndex]
                    .TargetCreatureId;
            }

            goldReward = new QuestGoldRewardProjection(
                (uint)fixedGoldAmount,
                hasGoldMarker);
            return true;
        }

        internal static bool TryCreate(
            int questId,
            QuestFile quest,
            out QuestRewardDefinition definition,
            out string error)
        {
            definition = null;
            error = string.Empty;
            if (questId <= 0 || quest == null)
            {
                error = "invalid quest reward source";
                return false;
            }

            var rewardTag = QuestData.NormalizeQuestTag(quest.RewardType);
            var kind = QuestRewardKind.Item;
            var chainType = 0;
            var selectionPolicy = QuestRewardSelectionPolicy.Forbidden;
            IReadOnlyList<QuestRewardItemRule> fixedItems =
                Array.Empty<QuestRewardItemRule>();
            IReadOnlyList<QuestRewardItemRule> selectableItems =
                Array.Empty<QuestRewardItemRule>();
            IReadOnlyList<EventCreatureEvolutionOption> eventOptions =
                Array.Empty<EventCreatureEvolutionOption>();
            var rewardParameter = 0;

            switch (rewardTag)
            {
                // Twelve legacy awakening quests omit [reward type] but retain
                // the ordinary item and selection payloads.
                case "":
                case "item":
                case "title":
                    kind = rewardTag == "title"
                        ? QuestRewardKind.Title
                        : QuestRewardKind.Item;
                    if (!TryParseItemRules(
                            quest.RewardIntData,
                            allowGoldMarker: true,
                            out fixedItems,
                            out error)
                        || !TryParseItemRules(
                            quest.RewardSelectionIntData,
                            allowGoldMarker: false,
                            out selectableItems,
                            out error))
                    {
                        return false;
                    }
                    selectionPolicy = selectableItems.Count > 0
                        ? QuestRewardSelectionPolicy.Required
                        : QuestRewardSelectionPolicy.Forbidden;
                    break;

                case "grow type":
                    kind = QuestRewardKind.GrowType;
                    chainType = 1;
                    if (!TryParseScalarReward(quest, 0, out rewardParameter, out error))
                        return false;
                    break;

                case "awakening type":
                    kind = QuestRewardKind.AwakeningType;
                    chainType = 2;
                    if (!TryParseScalarReward(quest, 0, out rewardParameter, out error))
                        return false;
                    break;

                case "creature evolution":
                    kind = QuestRewardKind.CreatureEvolution;
                    chainType = 10;
                    if (!TryParseScalarReward(quest, 1, out rewardParameter, out error)
                        || !TryValidateCreatureSource(quest, out error))
                    {
                        return false;
                    }
                    break;

                case "expert job":
                    kind = QuestRewardKind.ExpertJob;
                    chainType = 20;
                    if (!TryParseScalarReward(quest, 0, out rewardParameter, out error))
                        return false;
                    break;

                case "slot expansion":
                    kind = QuestRewardKind.SlotExpansion;
                    chainType = QuestRewardProjector.ChainTypeSlotExpansion;
                    if (!TryParseScalarReward(quest, 0, out rewardParameter, out error))
                        return false;
                    break;

                case "event creature evolution":
                    kind = QuestRewardKind.EventCreatureEvolution;
                    chainType = 25;
                    selectionPolicy = QuestRewardSelectionPolicy.Required;
                    if (!string.IsNullOrWhiteSpace(quest.RewardIntData))
                    {
                        error = "event creature evolution has unexpected reward int data";
                        return false;
                    }
                    if (!TryParseEventCreatureOptions(
                            quest.RewardSelectionIntData,
                            out eventOptions,
                            out error)
                        || !TryValidateCreatureSource(quest, out error))
                    {
                        return false;
                    }
                    break;

                case "ridable":
                    kind = QuestRewardKind.Ridable;
                    if (!TryParseScalarReward(quest, 0, out rewardParameter, out error))
                        return false;
                    break;

                case "event skill":
                    kind = QuestRewardKind.EventSkill;
                    if (!TryParseScalarReward(quest, 0, out rewardParameter, out error))
                        return false;
                    break;

                case "hell challenge":
                    kind = QuestRewardKind.HellChallenge;
                    if (!TryParseScalarReward(quest, 0, out rewardParameter, out error))
                        return false;
                    break;

                case "crack of dimension":
                    kind = QuestRewardKind.CrackOfDimension;
                    if (!string.IsNullOrWhiteSpace(quest.RewardIntData)
                        || !string.IsNullOrWhiteSpace(
                            quest.RewardSelectionIntData))
                    {
                        error = "crack of dimension reward has unexpected payload";
                        return false;
                    }
                    break;

                default:
                    error = $"unsupported reward type '{rewardTag}'";
                    return false;
            }

            var questMinLevel = quest.Level != null && quest.Level.Length > 0
                ? quest.Level[0]
                : 1;
            var difficulty = !string.IsNullOrEmpty(quest.Difficulty)
                ? quest.Difficulty[0]
                : 'G';
            var grade = QuestData.NormalizeQuestTag(quest.Grade);
            var questType = QuestData.NormalizeQuestTag(quest.Type);
            var suppressExperience = string.Equals(
                    grade,
                    "normaly repeat",
                    StringComparison.Ordinal)
                || string.Equals(
                    questType,
                    "seeking repeat",
                    StringComparison.Ordinal);

            definition = new QuestRewardDefinition(
                questId,
                kind,
                chainType,
                selectionPolicy,
                fixedItems,
                selectableItems,
                eventOptions,
                rewardParameter,
                quest.CreatureKind,
                quest.CreatureLevel,
                questMinLevel,
                difficulty,
                quest.IgnoreQuestLevel4Exp,
                suppressExperience,
                quest.GoldMultiple);
            return true;
        }

        private bool TryValidateSelectionPresence(
            bool hasRewardSelection,
            int rewardSelectionIndex,
            out string error)
        {
            error = string.Empty;
            if (hasRewardSelection && rewardSelectionIndex < 0)
            {
                error = "reward selection index is negative";
                return false;
            }
            if (SelectionPolicy == QuestRewardSelectionPolicy.Required
                && !hasRewardSelection)
            {
                error = "reward selection is required";
                return false;
            }
            if (SelectionPolicy == QuestRewardSelectionPolicy.Forbidden
                && hasRewardSelection)
            {
                error = "reward selection is not allowed";
                return false;
            }
            return true;
        }

        private static bool TryParseScalarReward(
            QuestFile quest,
            int minimumValue,
            out int value,
            out string error)
        {
            value = 0;
            error = string.Empty;
            if (!string.IsNullOrWhiteSpace(quest.RewardSelectionIntData))
            {
                error = "scalar reward has unexpected selection data";
                return false;
            }

            var tokens = SplitTokens(quest.RewardIntData);
            if (tokens.Length != 1
                || !int.TryParse(
                    tokens[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value)
                || value < minimumValue)
            {
                error = $"scalar reward requires one integer >= {minimumValue}";
                return false;
            }
            return true;
        }

        private static bool TryValidateCreatureSource(
            QuestFile quest,
            out string error)
        {
            error = string.Empty;
            if (quest.CreatureKind <= 0 || quest.CreatureLevel < 0)
            {
                error = "creature evolution source kind/level is invalid";
                return false;
            }
            return true;
        }

        private static bool TryParseItemRules(
            string data,
            bool allowGoldMarker,
            out IReadOnlyList<QuestRewardItemRule> rules,
            out string error)
        {
            rules = Array.Empty<QuestRewardItemRule>();
            error = string.Empty;
            var tokens = SplitTokens(data);
            if (tokens.Length == 0)
                return true;

            var parsed = new List<QuestRewardItemRule>();
            var index = 0;
            while (index < tokens.Length)
            {
                if (!TryParseInt(tokens[index++], out var itemId))
                {
                    error = "item reward contains a non-integer item id";
                    return false;
                }

                if (index < tokens.Length && IsJobMarker(tokens[index]))
                {
                    index++;
                    if (itemId <= 0 || index + 2 >= tokens.Length
                        || !TryParseInt(tokens[index++], out var jobId)
                        || !TryParseInt(tokens[index++], out var growType)
                        || !TryParseInt(tokens[index++], out var count)
                        || jobId < -1
                        || growType < -1
                        || count <= 0)
                    {
                        error = "job-filtered item reward is malformed";
                        return false;
                    }
                    parsed.Add(new QuestRewardItemRule(
                        itemId,
                        count,
                        jobId,
                        growType));
                    continue;
                }

                if (index >= tokens.Length
                    || !TryParseInt(tokens[index++], out var ordinaryCount))
                {
                    error = "item reward has no valid count";
                    return false;
                }
                if (itemId == 0)
                {
                    if (!allowGoldMarker || ordinaryCount < 0)
                    {
                        error = "invalid gold marker in item reward";
                        return false;
                    }
                }
                else if (itemId < 0 || ordinaryCount <= 0)
                {
                    error = "item reward contains a non-positive item/count";
                    return false;
                }
                parsed.Add(new QuestRewardItemRule(
                    itemId,
                    ordinaryCount,
                    -1,
                    -1));
            }

            rules = new ReadOnlyCollection<QuestRewardItemRule>(parsed);
            return true;
        }

        private static bool TryParseEventCreatureOptions(
            string data,
            out IReadOnlyList<EventCreatureEvolutionOption> options,
            out string error)
        {
            options = Array.Empty<EventCreatureEvolutionOption>();
            error = string.Empty;
            var tokens = SplitTokens(data);
            if (tokens.Length == 0 || (tokens.Length & 1) != 0)
            {
                error = "event creature evolution options are incomplete";
                return false;
            }

            var parsed = new List<EventCreatureEvolutionOption>();
            for (var index = 0; index < tokens.Length; index += 2)
            {
                if (!TryParseInt(tokens[index], out var displayItemId)
                    || !TryParseInt(tokens[index + 1], out var targetCreatureId)
                    || displayItemId <= 0
                    || targetCreatureId <= 0)
                {
                    error = "event creature evolution option is invalid";
                    return false;
                }
                parsed.Add(new EventCreatureEvolutionOption(
                    displayItemId,
                    targetCreatureId));
            }
            options = new ReadOnlyCollection<EventCreatureEvolutionOption>(parsed);
            return true;
        }

        private static string[] SplitTokens(string data) =>
            string.IsNullOrWhiteSpace(data)
                ? Array.Empty<string>()
                : data.Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);

        private static bool TryParseInt(string token, out int value) =>
            int.TryParse(
                token,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);

        private static bool IsJobMarker(string token) =>
            string.Equals(
                (token ?? string.Empty).Trim('`'),
                "[job]",
                StringComparison.OrdinalIgnoreCase);

        private readonly struct QuestRewardItemRule
        {
            internal QuestRewardItemRule(
                int itemId,
                int count,
                int jobId,
                int growType)
            {
                ItemId = itemId;
                Count = count;
                JobId = jobId;
                GrowType = growType;
            }

            internal int ItemId { get; }
            internal int Count { get; }
            internal int JobId { get; }
            internal int GrowType { get; }

            internal bool Matches(int playerJob, int playerGrowType)
            {
                var jobMatches = playerJob < 0
                    || JobId < 0
                    || JobId == playerJob;
                var growMatches = playerGrowType < 0
                    || GrowType < 0
                    || GrowType == (playerGrowType & 0xF);
                return jobMatches && growMatches;
            }

            internal QuestRewardItem ToRewardItem() =>
                new QuestRewardItem { ItemId = ItemId, Count = Count };
        }

        private readonly struct EventCreatureEvolutionOption
        {
            internal EventCreatureEvolutionOption(
                int displayItemId,
                int targetCreatureId)
            {
                DisplayItemId = displayItemId;
                TargetCreatureId = targetCreatureId;
            }

            internal int DisplayItemId { get; }
            internal int TargetCreatureId { get; }
        }
    }
}
