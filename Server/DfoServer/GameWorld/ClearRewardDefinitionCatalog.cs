using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.GameWorld
{
    internal readonly struct ClearRewardGradeRange
    {
        internal ClearRewardGradeRange(int down, int up)
        {
            Down = Math.Max(0, down);
            Up = Math.Max(0, up);
        }

        internal int Down { get; }
        internal int Up { get; }
    }

    internal enum ClearRewardDropProfile
    {
        Default = 0,
        Event = 1,
        Event2 = 2,
        PcRoomDefault = 3,
        PcRoomBonus = 4,
        PcRoomEvent = 5,
    }

    internal sealed class ClearRewardDefinition
    {
        private static readonly string[] ProfileNames =
        {
            "default",
            "event",
            "event2",
            "pcroom default",
            "pcroom bonus",
            "pcroom event",
        };

        private readonly Dictionary<string, ClearRewardDropLevel[]> _dropProfiles;
        private readonly int[] _itemTypeWeights;
        private readonly int[] _difficultyItemBonuses;
        private readonly int[] _rarityThresholds;
        private readonly double[] _partyMemberRates;
        private readonly double[] _difficultyGoldRates;
        private readonly Dictionary<int, int> _mapCountRates;
        private readonly Dictionary<int, int> _paidCardCosts;
        private readonly Dictionary<int, ClearRewardGradeRange> _gradeRanges;
        private readonly int[] _rarityControl;

        internal ClearRewardDefinition(
            Dictionary<string, ClearRewardDropLevel[]> dropProfiles,
            int[] itemTypeWeights,
            int[] difficultyItemBonuses,
            int[] rarityThresholds,
            double[] partyMemberRates,
            double[] difficultyGoldRates,
            Dictionary<int, int> mapCountRates,
            Dictionary<int, int> paidCardCosts,
            double paidCardCreateRate,
            Dictionary<int, ClearRewardGradeRange> gradeRanges,
            int[] rarityControl)
        {
            _dropProfiles = dropProfiles
                ?? new Dictionary<string, ClearRewardDropLevel[]>(
                    StringComparer.OrdinalIgnoreCase);
            _itemTypeWeights = itemTypeWeights ?? Array.Empty<int>();
            _difficultyItemBonuses = difficultyItemBonuses ?? Array.Empty<int>();
            _rarityThresholds = rarityThresholds ?? Array.Empty<int>();
            _partyMemberRates = partyMemberRates ?? Array.Empty<double>();
            _difficultyGoldRates = difficultyGoldRates ?? Array.Empty<double>();
            _mapCountRates = mapCountRates ?? new Dictionary<int, int>();
            _paidCardCosts = paidCardCosts ?? new Dictionary<int, int>();
            PaidCardCreateRate = Math.Max(0.0, paidCardCreateRate);
            _gradeRanges = gradeRanges
                ?? new Dictionary<int, ClearRewardGradeRange>();
            _rarityControl = rarityControl ?? Array.Empty<int>();
        }

        internal double PaidCardCreateRate { get; }
        internal int ItemTypeCount => _itemTypeWeights.Length;
        internal int RarityThresholdCount => _rarityThresholds.Length;
        internal IReadOnlyList<int> RarityControl => _rarityControl;

        internal int GetDropProbability(ClearRewardDropProfile profile, int level)
        {
            var index = (int)profile;
            if (index < 0 || index >= ProfileNames.Length
                || !_dropProfiles.TryGetValue(ProfileNames[index], out var ranges))
            {
                return 0;
            }

            foreach (var range in ranges)
            {
                if (level >= range.MinimumLevel && level <= range.MaximumLevel)
                    return Math.Max(0, range.Probability);
            }

            return 0;
        }

        internal int GetItemTypeWeight(int itemType)
        {
            var index = itemType - 1;
            return index >= 0 && index < _itemTypeWeights.Length
                ? Math.Max(0, _itemTypeWeights[index])
                : 0;
        }

        internal int GetItemTypeWeightTotal()
        {
            var total = 0;
            foreach (var weight in _itemTypeWeights)
                total = checked(total + Math.Max(0, weight));
            return total;
        }

        internal int GetDifficultyItemBonus(int difficulty) =>
            GetIndexedValue(_difficultyItemBonuses, difficulty, 0);

        internal double GetDifficultyGoldRate(int difficulty) =>
            GetIndexedValue(_difficultyGoldRates, difficulty, 1.0);

        internal double GetPartyMemberRate(int partyMemberCount) =>
            GetIndexedValue(_partyMemberRates, partyMemberCount - 1, 1.0);

        internal int GetRarityThreshold(int rarity) =>
            GetIndexedValue(_rarityThresholds, rarity, 0);

        internal int GetPaidCardCost(int level)
        {
            if (_paidCardCosts.TryGetValue(level, out var exact))
                return Math.Max(0, exact);

            var closestLevel = -1;
            var closestCost = 0;
            foreach (var entry in _paidCardCosts)
            {
                if (entry.Key <= level && entry.Key > closestLevel)
                {
                    closestLevel = entry.Key;
                    closestCost = entry.Value;
                }
            }

            if (closestLevel >= 0)
                return Math.Max(0, closestCost);

            foreach (var entry in _paidCardCosts)
            {
                if (closestLevel < 0 || entry.Key < closestLevel)
                {
                    closestLevel = entry.Key;
                    closestCost = entry.Value;
                }
            }

            return Math.Max(0, closestCost);
        }

        internal ClearRewardGradeRange GetGradeRange(int level)
        {
            if (_gradeRanges.TryGetValue(level, out var exact))
                return exact;

            var closestLevel = -1;
            var closest = default(ClearRewardGradeRange);
            foreach (var entry in _gradeRanges)
            {
                if (entry.Key <= level && entry.Key > closestLevel)
                {
                    closestLevel = entry.Key;
                    closest = entry.Value;
                }
            }

            return closest;
        }

        internal double GetExplorationItemRate(
            int totalRoomCount,
            int visitedRoomCount)
        {
            totalRoomCount = Math.Max(1, totalRoomCount);
            visitedRoomCount = Math.Max(0, Math.Min(totalRoomCount, visitedRoomCount));
            var exploredRatio = (double)visitedRoomCount / totalRoomCount;
            if (!_mapCountRates.TryGetValue(totalRoomCount, out var configuredRate))
                return exploredRatio;

            return Math.Max(0.0, configuredRate) * exploredRatio / 10000.0;
        }

        private static int GetIndexedValue(int[] values, int index, int fallback)
        {
            if (values == null || values.Length == 0)
                return fallback;
            if (index < 0 || index >= values.Length)
                return fallback;
            return values[index];
        }

        private static double GetIndexedValue(
            double[] values,
            int index,
            double fallback)
        {
            if (values == null || values.Length == 0)
                return fallback;
            if (index < 0 || index >= values.Length)
                return fallback;
            return values[index];
        }

        internal readonly struct ClearRewardDropLevel
        {
            internal ClearRewardDropLevel(
                int minimumLevel,
                int maximumLevel,
                int probability)
            {
                MinimumLevel = minimumLevel;
                MaximumLevel = maximumLevel;
                Probability = probability;
            }

            internal int MinimumLevel { get; }
            internal int MaximumLevel { get; }
            internal int Probability { get; }
        }
    }

    internal static class ClearRewardDefinitionCatalog
    {
        private const string ConfigPath = "etc/itemdropinfo_clearreward.etc";
        private static readonly Lazy<ClearRewardDefinition> Definition =
            new Lazy<ClearRewardDefinition>(Load);

        internal static ClearRewardDefinition Current => Definition.Value;

        internal static void WarmUp()
        {
            _ = Definition.Value;
        }

        internal static ClearRewardDefinition Parse(string text)
        {
            var root = new ScriptParser().Parse(text ?? string.Empty);
            return new ClearRewardDefinition(
                ParseDropProfiles(root.GetChild("drop prob"), text),
                ParseInts(root.GetChild("drop item type prob"), text),
                ParseInts(root.GetChild("dungeon difficulty drop bonusrate"), text),
                ParseInts(root.GetChild("basis of rarity dicision"), text),
                ParseDoubles(root.GetChild("party member drop bonusrate"), text),
                ParseDoubles(
                    root.GetChild("dungeon difficulty gold drop bonusrate"),
                    text),
                ParsePairs(root.GetChild("reward item rate per map max count"), text),
                ParsePairs(root.GetChild("gold card cost table"), text),
                ParseFirstDouble(root.GetChild("gold card create rate"), text),
                ParseGradeRanges(root.GetChild("item drop ref table"), text),
                ParseInts(root.GetChild("item drop rarity control"), text));
        }

        private static ClearRewardDefinition Load()
        {
            try
            {
                var definition = Parse(PvfArchiveAccessor.ReadText(ConfigPath));
                FileLogger.Log(
                    $"[ClearRewardDefinition] loaded: itemTypes={definition.ItemTypeCount} " +
                    $"rarities={definition.RarityThresholdCount} " +
                    $"level86Cost={definition.GetPaidCardCost(86)}");
                return definition;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ClearRewardDefinition] failed to load {ConfigPath}; " +
                    $"item cards disabled: {ex.Message}");
                return Parse(string.Empty);
            }
        }

        private static Dictionary<string, ClearRewardDefinition.ClearRewardDropLevel[]>
            ParseDropProfiles(ScriptNode node, string text)
        {
            var result = new Dictionary<string, ClearRewardDefinition.ClearRewardDropLevel[]>(
                StringComparer.OrdinalIgnoreCase);
            var tokens = Tokenize(node, text);
            string name = null;
            var values = new List<int>();

            void Flush()
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;
                var ranges = new List<ClearRewardDefinition.ClearRewardDropLevel>();
                for (var index = 0; index + 2 < values.Count; index += 3)
                {
                    ranges.Add(new ClearRewardDefinition.ClearRewardDropLevel(
                        values[index],
                        values[index + 1],
                        values[index + 2]));
                }
                result[name] = ranges.ToArray();
                values.Clear();
            }

            foreach (var token in tokens)
            {
                if (int.TryParse(
                        token,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    values.Add(value);
                    continue;
                }

                Flush();
                name = token.Trim();
            }

            Flush();
            return result;
        }

        private static Dictionary<int, int> ParsePairs(ScriptNode node, string text)
        {
            var values = ParseInts(node, text);
            var result = new Dictionary<int, int>();
            for (var index = 0; index + 1 < values.Length; index += 2)
                result[values[index]] = values[index + 1];
            return result;
        }

        private static Dictionary<int, ClearRewardGradeRange> ParseGradeRanges(
            ScriptNode node,
            string text)
        {
            var values = ParseInts(node, text);
            var result = new Dictionary<int, ClearRewardGradeRange>();
            for (var index = 0; index + 2 < values.Length; index += 3)
            {
                result[values[index]] = new ClearRewardGradeRange(
                    values[index + 1],
                    values[index + 2]);
            }
            return result;
        }

        private static int[] ParseInts(ScriptNode node, string text)
        {
            var result = new List<int>();
            foreach (var token in Tokenize(node, text))
            {
                if (int.TryParse(
                        token,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    result.Add(value);
                }
            }
            return result.ToArray();
        }

        private static double[] ParseDoubles(ScriptNode node, string text)
        {
            var result = new List<double>();
            foreach (var token in Tokenize(node, text))
            {
                if (double.TryParse(
                        token,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    result.Add(value);
                }
            }
            return result.ToArray();
        }

        private static double ParseFirstDouble(ScriptNode node, string text)
        {
            var values = ParseDoubles(node, text);
            return values.Length > 0 ? values[0] : 0.0;
        }

        private static IReadOnlyList<string> Tokenize(ScriptNode node, string text)
        {
            return ScriptValueTokenizer.Tokenize(
                node?.GetFirstDataContent(text ?? string.Empty));
        }
    }
}
