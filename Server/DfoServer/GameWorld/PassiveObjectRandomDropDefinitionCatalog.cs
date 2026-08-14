using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace DfoServer.GameWorld
{
    internal readonly struct PassiveObjectDropGradeRange
    {
        internal PassiveObjectDropGradeRange(int down, int up)
        {
            Down = down;
            Up = up;
        }

        internal int Down { get; }
        internal int Up { get; }
    }

    internal sealed class PassiveObjectRandomDropDefinition
    {
        internal const int CategoryCount = 5;
        internal const int ItemTypeCount = 4;
        internal const int DifficultyCount = 5;
        internal const int ActorTypeCount = 4;
        internal const int RarityThresholdCount = 7;

        private readonly DropRateRange[] _dropRates;
        private readonly int[,] _rarityThresholds;
        private readonly double[,] _difficultyRates;
        private readonly double[,] _actorTypeRates;
        private readonly IReadOnlyDictionary<int, PassiveObjectDropGradeRange>
            _gradeRanges;

        internal PassiveObjectRandomDropDefinition(
            DropRateRange[] dropRates,
            int[,] rarityThresholds,
            double[,] difficultyRates,
            double[,] actorTypeRates,
            IDictionary<int, PassiveObjectDropGradeRange> gradeRanges,
            int[] rarityControl)
        {
            _dropRates = (DropRateRange[])dropRates.Clone();
            _rarityThresholds = (int[,])rarityThresholds.Clone();
            _difficultyRates = (double[,])difficultyRates.Clone();
            _actorTypeRates = (double[,])actorTypeRates.Clone();
            _gradeRanges = new ReadOnlyDictionary<int, PassiveObjectDropGradeRange>(
                new Dictionary<int, PassiveObjectDropGradeRange>(gradeRanges));
            RarityControl = Array.AsReadOnly((int[])rarityControl.Clone());
            IsValid = true;
            FailureReason = string.Empty;
        }

        private PassiveObjectRandomDropDefinition(string failureReason)
        {
            _dropRates = Array.Empty<DropRateRange>();
            _rarityThresholds = new int[0, 0];
            _difficultyRates = new double[0, 0];
            _actorTypeRates = new double[0, 0];
            _gradeRanges = new ReadOnlyDictionary<int, PassiveObjectDropGradeRange>(
                new Dictionary<int, PassiveObjectDropGradeRange>());
            RarityControl = Array.AsReadOnly(Array.Empty<int>());
            FailureReason = failureReason ?? "invalid definition";
        }

        internal bool IsValid { get; }
        internal string FailureReason { get; }
        internal IReadOnlyList<int> RarityControl { get; }

        internal int GetBaseRate(int level, int category)
        {
            if (!IsValid || category < 0 || category >= CategoryCount)
                return 0;

            for (var index = 0; index < _dropRates.Length; index++)
            {
                var range = _dropRates[index];
                if (level >= range.MinimumLevel && level <= range.MaximumLevel)
                    return range.GetRate(category);
            }
            return 0;
        }

        internal double GetDifficultyRate(int category, int difficulty)
        {
            if (!IsValid
                || category < 0
                || category >= CategoryCount
                || difficulty < 0
                || difficulty >= DifficultyCount)
            {
                return 0.0;
            }
            return _difficultyRates[category, difficulty];
        }

        internal double GetActorTypeRate(int category, int actorType)
        {
            if (!IsValid
                || category < 0
                || category >= CategoryCount
                || actorType < 0
                || actorType >= ActorTypeCount)
            {
                return 0.0;
            }
            return _actorTypeRates[category, actorType];
        }

        internal int GetRarityThreshold(int itemType, int rarity)
        {
            if (!IsValid
                || itemType < 1
                || itemType > ItemTypeCount
                || rarity < 0
                || rarity >= RarityThresholdCount)
            {
                return 0;
            }
            return _rarityThresholds[itemType - 1, rarity];
        }

        internal bool TryGetGradeRange(
            int level,
            out PassiveObjectDropGradeRange range)
        {
            if (IsValid && _gradeRanges.TryGetValue(level, out range))
                return true;
            range = default;
            return false;
        }

        internal static PassiveObjectRandomDropDefinition Disabled(
            string failureReason) =>
            new PassiveObjectRandomDropDefinition(failureReason);

        internal readonly struct DropRateRange
        {
            private readonly int[] _rates;

            internal DropRateRange(
                int minimumLevel,
                int maximumLevel,
                int[] rates)
            {
                MinimumLevel = minimumLevel;
                MaximumLevel = maximumLevel;
                _rates = (int[])rates.Clone();
            }

            internal int MinimumLevel { get; }
            internal int MaximumLevel { get; }

            internal int GetRate(int category) =>
                category >= 0 && category < _rates.Length
                    ? _rates[category]
                    : 0;
        }
    }

    internal static class PassiveObjectRandomDropDefinitionCatalog
    {
        private const string ConfigPath = "etc/itemdropinfo_object.etc";
        private static readonly Lazy<PassiveObjectRandomDropDefinition> Definition =
            new Lazy<PassiveObjectRandomDropDefinition>(Load);

        internal static PassiveObjectRandomDropDefinition Current =>
            Definition.Value;

        internal static void WarmUp()
        {
            _ = Definition.Value;
        }

        internal static PassiveObjectRandomDropDefinition Parse(string text)
        {
            try
            {
                var source = text ?? string.Empty;
                var root = new ScriptParser().Parse(source);
                var countValues = ParseInts(
                    root.GetChild("drop prob count"),
                    source);
                if (countValues.Length != 1 || countValues[0] <= 0)
                    throw new FormatException("[drop prob count] must contain one positive value.");

                var dropValues = ParseInts(root.GetChild("drop prob"), source);
                var expectedDropValues = checked(countValues[0] * 7);
                if (dropValues.Length != expectedDropValues)
                    throw new FormatException("[drop prob] does not match its declared count.");

                var ranges = new PassiveObjectRandomDropDefinition.DropRateRange[
                    countValues[0]];
                var previousMaximum = 0;
                for (var index = 0; index < ranges.Length; index++)
                {
                    var offset = index * 7;
                    var minimum = dropValues[offset];
                    var maximum = dropValues[offset + 1];
                    if (minimum <= previousMaximum || maximum < minimum)
                        throw new FormatException("[drop prob] level ranges overlap or are unordered.");

                    var rates = new int[PassiveObjectRandomDropDefinition.CategoryCount];
                    for (var category = 0; category < rates.Length; category++)
                    {
                        rates[category] = dropValues[offset + 2 + category];
                        if (rates[category] < 0)
                            throw new FormatException("[drop prob] contains a negative rate.");
                    }

                    ranges[index] = new PassiveObjectRandomDropDefinition.DropRateRange(
                        minimum,
                        maximum,
                        rates);
                    previousMaximum = maximum;
                }

                var rarity = ParseExactIntMatrix(
                    root.GetChild("basis of rarity dicision"),
                    source,
                    PassiveObjectRandomDropDefinition.ItemTypeCount,
                    PassiveObjectRandomDropDefinition.RarityThresholdCount,
                    "[basis of rarity dicision]");
                for (var row = 0; row < rarity.GetLength(0); row++)
                {
                    var previous = -1;
                    for (var column = 0; column < rarity.GetLength(1); column++)
                    {
                        if (rarity[row, column] < previous)
                            throw new FormatException("rarity thresholds must be cumulative.");
                        previous = rarity[row, column];
                    }
                }

                var difficulty = ParseExactDoubleMatrix(
                    root.GetChild("dungeon difficulty drop bonusrate"),
                    source,
                    PassiveObjectRandomDropDefinition.CategoryCount,
                    PassiveObjectRandomDropDefinition.DifficultyCount,
                    "[dungeon difficulty drop bonusrate]");
                var actorType = ParseExactDoubleMatrix(
                    root.GetChild("monster type drop bonusrate"),
                    source,
                    PassiveObjectRandomDropDefinition.CategoryCount,
                    PassiveObjectRandomDropDefinition.ActorTypeCount,
                    "[monster type drop bonusrate]");

                var gradeValues = ParseInts(
                    root.GetChild("item drop ref table"),
                    source);
                if (gradeValues.Length == 0 || gradeValues.Length % 3 != 0)
                    throw new FormatException("[item drop ref table] must contain level/down/up triples.");
                var gradeRanges = new Dictionary<int, PassiveObjectDropGradeRange>();
                for (var index = 0; index < gradeValues.Length; index += 3)
                {
                    var level = gradeValues[index];
                    var down = gradeValues[index + 1];
                    var up = gradeValues[index + 2];
                    if (level <= 0 || level > 200 || down < 0 || up < 0
                        || gradeRanges.ContainsKey(level))
                    {
                        throw new FormatException("[item drop ref table] contains an invalid or duplicate level.");
                    }
                    gradeRanges.Add(level, new PassiveObjectDropGradeRange(down, up));
                }

                var rarityControl = ParseInts(
                    root.GetChild("item drop rarity control"),
                    source);
                if (rarityControl.Length == 0)
                    throw new FormatException("[item drop rarity control] is missing.");

                return new PassiveObjectRandomDropDefinition(
                    ranges,
                    rarity,
                    difficulty,
                    actorType,
                    gradeRanges,
                    rarityControl);
            }
            catch (Exception ex)
            {
                return PassiveObjectRandomDropDefinition.Disabled(ex.Message);
            }
        }

        private static PassiveObjectRandomDropDefinition Load()
        {
            try
            {
                var definition = Parse(PvfArchiveAccessor.ReadText(ConfigPath));
                if (!definition.IsValid)
                {
                    FileLogger.Log(
                        $"[PassiveObjectRandomDropDefinition] disabled: " +
                        definition.FailureReason);
                }
                else
                {
                    FileLogger.Log(
                        "[PassiveObjectRandomDropDefinition] loaded from " +
                        ConfigPath);
                }
                return definition;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[PassiveObjectRandomDropDefinition] failed to load " +
                    $"{ConfigPath}: {ex.Message}");
                return PassiveObjectRandomDropDefinition.Disabled(ex.Message);
            }
        }

        private static int[,] ParseExactIntMatrix(
            ScriptNode node,
            string text,
            int rows,
            int columns,
            string name)
        {
            var values = ParseInts(node, text);
            if (values.Length != rows * columns)
                throw new FormatException(name + " has an invalid shape.");

            var result = new int[rows, columns];
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var value = values[row * columns + column];
                    if (value < 0)
                        throw new FormatException(name + " contains a negative value.");
                    result[row, column] = value;
                }
            }
            return result;
        }

        private static double[,] ParseExactDoubleMatrix(
            ScriptNode node,
            string text,
            int rows,
            int columns,
            string name)
        {
            var values = ParseDoubles(node, text);
            if (values.Length != rows * columns)
                throw new FormatException(name + " has an invalid shape.");

            var result = new double[rows, columns];
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var value = values[row * columns + column];
                    if (value < 0.0 || double.IsNaN(value) || double.IsInfinity(value))
                        throw new FormatException(name + " contains an invalid multiplier.");
                    result[row, column] = value;
                }
            }
            return result;
        }

        private static int[] ParseInts(ScriptNode node, string text)
        {
            var tokens = ScriptValueTokenizer.Tokenize(
                node?.GetFirstDataContent(text ?? string.Empty));
            var values = new int[tokens.Count];
            for (var index = 0; index < tokens.Count; index++)
            {
                if (!int.TryParse(
                        tokens[index],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out values[index]))
                {
                    throw new FormatException("Expected an integer token.");
                }
            }
            return values;
        }

        private static double[] ParseDoubles(ScriptNode node, string text)
        {
            var tokens = ScriptValueTokenizer.Tokenize(
                node?.GetFirstDataContent(text ?? string.Empty));
            var values = new double[tokens.Count];
            for (var index = 0; index < tokens.Count; index++)
            {
                if (!double.TryParse(
                        tokens[index],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out values[index]))
                {
                    throw new FormatException("Expected a numeric token.");
                }
            }
            return values;
        }
    }
}
