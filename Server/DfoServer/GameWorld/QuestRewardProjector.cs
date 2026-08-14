using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    internal struct QuestReward
    {
        public uint Exp;
        public uint Gold;
        public int ChainType;
        public int GrowNumber;
        public int CreatureKind;
        public int CreatureLevel;
        public List<QuestRewardItem> Items;
        public List<QuestRewardItem> ConsumeItems;
    }

    internal sealed class QuestRewardResolution
    {
        private QuestRewardResolution(
            bool isValid,
            QuestReward reward,
            string error)
        {
            IsValid = isValid;
            Reward = reward;
            Error = error ?? string.Empty;
        }

        internal bool IsValid { get; }
        internal QuestReward Reward { get; }
        internal string Error { get; }

        internal static QuestRewardResolution Valid(QuestReward reward)
            => new QuestRewardResolution(true, reward, string.Empty);

        internal static QuestRewardResolution Invalid(
            QuestReward emptyReward,
            string error)
            => new QuestRewardResolution(false, emptyReward, error);
    }

    internal struct QuestRewardItem
    {
        public int ItemId;
        public int Count;
    }

    internal static class QuestRewardProjector
    {
        internal const int ChainTypeSlotExpansion = 21;

        private static readonly Lazy<QuestParameterTable> Parameters =
            new Lazy<QuestParameterTable>(LoadParameters);

        internal static QuestRewardResolution Resolve(
            QuestRewardDefinition definition,
            bool hasRewardSelection,
            int rewardSelectIdx,
            int playerLevel,
            int playerJob,
            int playerGrowType)
        {
            var empty = CreateEmptyReward();
            if (definition == null)
            {
                return QuestRewardResolution.Invalid(
                    empty,
                    "quest reward definition not found");
            }

            try
            {
                if (!definition.TryProject(
                        hasRewardSelection,
                        rewardSelectIdx,
                        playerJob,
                        playerGrowType,
                        out var items,
                        out var goldReward,
                        out var rewardParameter,
                        out var projectionError))
                {
                    return QuestRewardResolution.Invalid(empty, projectionError);
                }

                var exp = definition.SuppressExperience
                    ? 0
                    : Parameters.Value.ComputeExp(
                        playerLevel,
                        definition.QuestMinLevel,
                        definition.Difficulty,
                        definition.IgnoreLevelForExperience);

                uint gold = 0;
                if (definition.ChainType == 0)
                {
                    if (goldReward.HasFixedAmount)
                    {
                        gold = goldReward.FixedAmount;
                    }
                    else if (goldReward.HasFormulaMarker
                             || definition.GoldMultiple > 0)
                    {
                        gold = Parameters.Value.ComputeGoldReward(
                            playerLevel,
                            definition.QuestMinLevel,
                            definition.GoldMultiple,
                            definition.IgnoreLevelForExperience);
                    }
                }

                return QuestRewardResolution.Valid(
                    new QuestReward
                    {
                        Exp = exp,
                        Gold = gold,
                        ChainType = definition.ChainType,
                        GrowNumber = rewardParameter,
                        CreatureKind = definition.CreatureKind,
                        CreatureLevel = definition.CreatureLevel,
                        Items = items,
                        ConsumeItems = new List<QuestRewardItem>(),
                    });
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestRewardProjector] reward calc failed: " +
                    $"quest={definition.QuestId}: {ex.Message}");
                return QuestRewardResolution.Invalid(empty, ex.Message);
            }
        }

        internal static QuestReward CreateEmptyReward()
            => new QuestReward
            {
                Exp = 0,
                Gold = 0,
                ChainType = 0,
                Items = new List<QuestRewardItem>(),
                ConsumeItems = new List<QuestRewardItem>(),
            };

        private static QuestParameterTable LoadParameters()
        {
            try
            {
                return QuestParameterTable.Parse(
                    PvfArchiveAccessor.ReadText("n_Quest/questParameter.etc"));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestRewardProjector] Failed to load " +
                    $"questParameter.etc: {ex.Message}");
                return new QuestParameterTable();
            }
        }
    }

    internal sealed class QuestParameterTable
    {
        private Dictionary<char, int> _difficultyWeight =
            new Dictionary<char, int>();
        private int[] _expTable = Array.Empty<int>();
        private int[] _goldTable = Array.Empty<int>();
        private int _greenPenalty = 80;
        private int _greyPenalty = 30;

        internal uint ComputeExp(
            int playerLevel,
            int questMinLevel,
            char difficulty,
            bool ignoreLevel)
        {
            var levelDiff = playerLevel - questMinLevel;
            var penalty = ignoreLevel ? 100 : ComputeLevelPenalty(levelDiff);
            var lookupLevel = ignoreLevel ? playerLevel : questMinLevel;
            var baseExp = lookupLevel >= 1 && lookupLevel <= _expTable.Length
                ? _expTable[lookupLevel - 1]
                : 0;
            if (!_difficultyWeight.TryGetValue(
                    char.ToUpperInvariant(difficulty),
                    out var weight))
            {
                weight = 10;
            }

            return (uint)(penalty * ((long)weight * baseExp / 100) / 100);
        }

        internal uint ComputeGoldReward(
            int playerLevel,
            int questMinLevel,
            int goldMultiple,
            bool ignoreLevel)
        {
            if (goldMultiple <= 0)
                goldMultiple = 100;
            var levelDiff = playerLevel - questMinLevel;
            var penalty = ignoreLevel ? 100 : ComputeLevelPenalty(levelDiff);
            var lookupIndex = ignoreLevel ? playerLevel - 1 : questMinLevel;
            var baseGold = lookupIndex >= 0 && lookupIndex < _goldTable.Length
                ? _goldTable[lookupIndex]
                : 0;
            return (uint)(goldMultiple * ((long)penalty * baseGold / 100) / 100);
        }

        private int ComputeLevelPenalty(int levelDiff)
        {
            if (levelDiff > 6 && levelDiff <= 11)
                return _greenPenalty;
            if (levelDiff <= 11)
                return 100;
            return _greyPenalty;
        }

        internal static QuestParameterTable Parse(string content)
        {
            var table = new QuestParameterTable();
            if (string.IsNullOrEmpty(content))
                return table;

            var lines = content.Replace("\r\n", "\n").Split('\n');
            string section = null;
            var expValues = new List<int>();
            var goldValues = new List<int>();

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    if (line == "[difficulty]")
                    {
                        section = "diff";
                        continue;
                    }
                    if (line == "[exp reward table]")
                    {
                        section = "exp";
                        continue;
                    }
                    if (line == "[gold reward table]")
                    {
                        section = "gold";
                        continue;
                    }
                    if (line.StartsWith("[green level penalty]"))
                    {
                        section = "green";
                        continue;
                    }
                    if (line.StartsWith("[grey level penalty]"))
                    {
                        section = "grey";
                        continue;
                    }

                    section = null;
                    continue;
                }

                if (section == "green" && line.Length > 0)
                {
                    if (int.TryParse(line.Split(' ')[0], out var value))
                        table._greenPenalty = value;
                    section = null;
                }
                else if (section == "grey" && line.Length > 0)
                {
                    if (int.TryParse(line.Split(' ')[0], out var value))
                        table._greyPenalty = value;
                    section = null;
                }
                else if (section == "diff" && line.Length > 0)
                {
                    var tokens = line.Split(
                        new[] { ' ', '\t' },
                        StringSplitOptions.RemoveEmptyEntries);
                    for (var index = 0; index + 1 < tokens.Length; index += 2)
                    {
                        var key = tokens[index].Trim('`');
                        if (key.Length == 1
                            && int.TryParse(tokens[index + 1], out var value))
                        {
                            table._difficultyWeight[key[0]] = value;
                        }
                    }
                }
                else if (section == "exp" && line.Length > 0)
                {
                    AppendIntegers(line, expValues, requireNonNegative: true);
                }
                else if (section == "gold" && line.Length > 0)
                {
                    AppendIntegers(line, goldValues, requireNonNegative: false);
                }
            }

            table._expTable = expValues.ToArray();
            table._goldTable = goldValues.ToArray();
            return table;
        }

        private static void AppendIntegers(
            string line,
            ICollection<int> output,
            bool requireNonNegative)
        {
            foreach (var token in line.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, out var value)
                    && (!requireNonNegative || value >= 0))
                {
                    output.Add(value);
                }
            }
        }
    }
}
