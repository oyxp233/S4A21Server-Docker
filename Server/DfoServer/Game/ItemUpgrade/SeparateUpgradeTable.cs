using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.Game.ItemUpgrade
{
    internal sealed class SeparateUpgradeTable
    {
        internal int MaxLevel { get; private set; }
        internal bool IsStructurallyValid { get; private set; } = true;
        internal List<SeparateUpgradeLevel> Levels { get; } = new List<SeparateUpgradeLevel>();
        internal Dictionary<int, SeparateUpgradeMaterial> MaterialsByGrade { get; }
            = new Dictionary<int, SeparateUpgradeMaterial>();
        internal List<double> ItemWeightsByRarity { get; } = new List<double>();

        internal static SeparateUpgradeTable Parse(string content)
        {
            content ??= string.Empty;
            var root = new ScriptParser().Parse(content);
            var table = new SeparateUpgradeTable();

            foreach (var node in root.Children)
            {
                var values = ParseNumbers(node.GetContent(content));
                switch (node.Tag.ToLowerInvariant())
                {
                    case "table":
                        if (values.Count == 0 || values.Count % 4 != 0)
                            table.IsStructurallyValid = false;
                        for (var index = 0; index + 3 < values.Count; index += 4)
                        {
                            table.Levels.Add(new SeparateUpgradeLevel
                            {
                                TargetLevel = table.Levels.Count + 1,
                                SuccessWeight = ToInt(values[index + 2]),
                                MaterialWeight = values[index + 3],
                            });
                        }
                        break;
                    case "separate upgrade max":
                        table.MaxLevel = values.Count > 0 ? ToInt(values[0]) : 0;
                        break;
                    case "item weights by grade":
                        if (values.Count == 0 || values.Count % 3 != 0)
                            table.IsStructurallyValid = false;
                        for (var index = 0; index + 2 < values.Count; index += 3)
                        {
                            var grade = ToInt(values[index]);
                            if (table.MaterialsByGrade.ContainsKey(grade))
                                table.IsStructurallyValid = false;
                            table.MaterialsByGrade[grade] = new SeparateUpgradeMaterial
                            {
                                ItemTemplateId = ToInt(values[index + 1]),
                                BaseCount = ToInt(values[index + 2]),
                            };
                        }
                        break;
                    case "item weights by rarity":
                        table.ItemWeightsByRarity.AddRange(values);
                        break;
                }
            }

            return table;
        }

        internal bool TryGetLevel(int targetLevel, out SeparateUpgradeLevel level)
        {
            level = targetLevel > 0 && targetLevel <= Levels.Count
                ? Levels[targetLevel - 1]
                : null;
            return level != null && level.TargetLevel == targetLevel;
        }

        private static List<double> ParseNumbers(string text)
        {
            var result = new List<double>();
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"-?\d+(?:\.\d+)?"))
            {
                if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    result.Add(value);
            }
            return result;
        }

        private static int ToInt(double value)
            => Convert.ToInt32(Math.Round(value, MidpointRounding.AwayFromZero));
    }

    internal sealed class SeparateUpgradeLevel
    {
        internal int TargetLevel { get; set; }
        internal int SuccessWeight { get; set; }
        internal double MaterialWeight { get; set; }
    }

    internal sealed class SeparateUpgradeMaterial
    {
        internal int ItemTemplateId { get; set; }
        internal int BaseCount { get; set; }
    }
}
