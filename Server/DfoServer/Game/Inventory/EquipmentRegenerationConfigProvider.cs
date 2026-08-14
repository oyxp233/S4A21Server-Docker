using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal static class EquipmentRegenerationConfigProvider
    {
        private const string PvfPath = "etc/randomoption/regenerationrandomoption.etc";
        private const string OverallPath = "etc/randomoption/randomizedoptionoverall1.etc";
        private static readonly Lazy<Config> Current = new Lazy<Config>(Load);

        internal static Config LoadCurrent() => Current.Value;

        private static Config Load()
        {
            var config = new Config();
            try
            {
                var text = PvfArchiveAccessor.ReadText(PvfPath);
                ParseChooseParts(text, config);
                ParseIntegerSection(text, "except item index", config.ExceptItemIds);
                ParseProbabilityLegacy(text, config);
                ParseExceptionWeights(text, config.ExceptionWeights);
                ParseMaterialSection(text, "compound need material for random", config.RandomMaterials);
                ParseMaterialSection(text, "compound need material for specific", config.SpecificMaterials);
                ParseLevelCostSection(text, "compound random apply cost by level", config.RandomLevelCosts);
                ParseLevelCostSection(text, "compound specific apply cost by level", config.SpecificLevelCosts);
                var overallText = PvfArchiveAccessor.ReadText(OverallPath);
                ParseRegenLevelLimit(overallText, config);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[EquipmentRegenerationConfig] load failed: {ex.Message}");
            }

            return config;
        }

        private static void ParseRegenLevelLimit(string text, Config config)
        {
            var values = ReadSectionIntegers(text, "regen level limit", allowUnclosedAtEnd: true);
            if (values.Count == 0)
                values = ReadSectionIntegers(text, "level limit");
            for (var index = 0; index + 3 < values.Count; index += 4)
            {
                var group = values[index];
                var level = values[index + 1];
                var currentWeight = values[index + 2];
                var plusFiveWeight = values[index + 3];
                if (group <= 0 || level < 0 || currentWeight < 0 || plusFiveWeight < 0)
                    continue;
                if (!config.RegenLevelLimits.TryGetValue(group, out var byLevel))
                {
                    byLevel = new Dictionary<int, RegenLevelLimit>();
                    config.RegenLevelLimits[group] = byLevel;
                }
                byLevel[level] = new RegenLevelLimit
                {
                    CurrentWeight = currentWeight,
                    PlusFiveWeight = plusFiveWeight,
                };
            }
        }

        private static void ParseChooseParts(string text, Config config)
        {
            foreach (Match section in Regex.Matches(
                text ?? string.Empty,
                @"\[choose part\](.*?)\[/choose part\]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var values = Regex.Matches(section.Groups[1].Value, @"`([^`]*)`")
                    .Cast<Match>()
                    .Select(match => match.Groups[1].Value.Trim())
                    .ToList();
                var number = Regex.Match(section.Groups[1].Value, @"(?:^|\s)(\d+)\s*(?:`|$)");
                if (!number.Success || !ushort.TryParse(number.Groups[1].Value, out var part) || values.Count == 0)
                    continue;
                config.Parts[part] = values.Skip(1)
                    .Where(value => value.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void ParseIntegerSection(string text, string name, HashSet<int> target)
        {
            var section = Regex.Match(text ?? string.Empty,
                $@"\[{Regex.Escape(name)}\](.*?)\[/{Regex.Escape(name)}\]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!section.Success)
                return;
            foreach (Match match in Regex.Matches(section.Groups[1].Value, @"(?<![\w-])-?\d+"))
                if (int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
                    target.Add(id);
        }

        private static void ParseProbabilityLegacy(string text, Config config)
        {
            var section = Regex.Match(text ?? string.Empty,
                @"\[probability legacy\](.*?)\[/probability legacy\]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (section.Success && double.TryParse(Regex.Match(section.Groups[1].Value, @"[-+]?\d+(?:\.\d+)?").Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
                config.LegacyWeight = weight;
        }

        private static void ParseExceptionWeights(string text, Dictionary<int, double> target)
        {
            var section = Regex.Match(text ?? string.Empty,
                @"\[probability exception handling\](.*?)\[/probability exception handling\]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!section.Success)
                return;
            var tokens = Regex.Matches(section.Groups[1].Value, @"[-+]?\d+(?:\.\d+)?")
                .Cast<Match>().Select(match => match.Value).ToArray();
            for (var index = 0; index + 1 < tokens.Length; index += 2)
            {
                if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                    || !double.TryParse(tokens[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight)
                    || id <= 0 || weight <= 0)
                    continue;
                target[id] = weight;
            }
        }

        private static void ParseMaterialSection(
            string text,
            string name,
            Dictionary<int, List<EquipmentRegenerationMaterial>> target)
        {
            var values = ReadSectionIntegers(text, name);
            var index = 0;
            while (index + 1 < values.Count)
            {
                var group = values[index++];
                var count = values[index++];
                if (group <= 0 || count <= 0 || index + count * 2 > values.Count)
                    break;

                var materials = new List<EquipmentRegenerationMaterial>();
                for (var entry = 0; entry < count; entry++)
                {
                    var itemId = values[index++];
                    var itemCount = values[index++];
                    if (itemId > 0 && itemCount > 0)
                        materials.Add(new EquipmentRegenerationMaterial
                        {
                            ItemTemplateId = itemId,
                            Count = itemCount,
                        });
                }

                if (materials.Count > 0)
                    target[group] = materials;
            }
        }

        private static void ParseLevelCostSection(
            string text,
            string name,
            Dictionary<int, Dictionary<int, int>> target)
        {
            var values = ReadSectionIntegers(text, name);
            for (var index = 0; index + 2 < values.Count; index += 3)
            {
                var group = values[index];
                var level = values[index + 1];
                var cost = values[index + 2];
                if (group <= 0 || level < 0 || cost <= 0)
                    continue;
                if (!target.TryGetValue(group, out var byLevel))
                {
                    byLevel = new Dictionary<int, int>();
                    target[group] = byLevel;
                }
                byLevel[level] = cost;
            }
        }

        private static List<int> ReadSectionIntegers(
            string text,
            string name,
            bool allowUnclosedAtEnd = false)
        {
            var source = text ?? string.Empty;
            var section = Regex.Match(source,
                $@"\[{Regex.Escape(name)}\](.*?)\[/{Regex.Escape(name)}\]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!section.Success && allowUnclosedAtEnd)
            {
                var startTag = $"[{name}]";
                var start = source.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    var bodyStart = start + startTag.Length;
                    var body = source.Substring(bodyStart);
                    return Regex.Matches(body, @"(?<![\w-])-?\d+")
                        .Cast<Match>()
                        .Select(match => int.Parse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture))
                        .ToList();
                }
            }
            return section.Success
                ? Regex.Matches(section.Groups[1].Value, @"(?<![\w-])-?\d+")
                    .Cast<Match>()
                    .Select(match => int.Parse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture))
                    .ToList()
                : new List<int>();
        }

        internal sealed class Config
        {
            public Dictionary<ushort, HashSet<string>> Parts { get; } = new Dictionary<ushort, HashSet<string>>();
            public HashSet<int> ExceptItemIds { get; } = new HashSet<int>();
            public Dictionary<int, double> ExceptionWeights { get; } = new Dictionary<int, double>();
            public Dictionary<int, List<EquipmentRegenerationMaterial>> RandomMaterials { get; } = new Dictionary<int, List<EquipmentRegenerationMaterial>>();
            public Dictionary<int, List<EquipmentRegenerationMaterial>> SpecificMaterials { get; } = new Dictionary<int, List<EquipmentRegenerationMaterial>>();
            public Dictionary<int, Dictionary<int, int>> RandomLevelCosts { get; } = new Dictionary<int, Dictionary<int, int>>();
            public Dictionary<int, Dictionary<int, int>> SpecificLevelCosts { get; } = new Dictionary<int, Dictionary<int, int>>();
            public Dictionary<int, Dictionary<int, RegenLevelLimit>> RegenLevelLimits { get; } = new Dictionary<int, Dictionary<int, RegenLevelLimit>>();
            public double LegacyWeight { get; set; } = 1.0;

            public IReadOnlyList<(int Level, double Weight)> GetTargetLevels(int group, int level)
            {
                if (!RegenLevelLimits.TryGetValue(group, out var byLevel))
                    return new[] { (level, 1.0) };
                if (!byLevel.TryGetValue(level, out var limit))
                    limit = byLevel.Where(pair => pair.Key <= level).OrderByDescending(pair => pair.Key).Select(pair => pair.Value).FirstOrDefault();
                if (limit == null)
                    return new[] { (level, 1.0) };
                var result = new List<(int Level, double Weight)>();
                if (limit.CurrentWeight > 0)
                    result.Add((level, limit.CurrentWeight));
                if (level < 85 && limit.PlusFiveWeight > 0)
                    result.Add((level + 5, limit.PlusFiveWeight));
                return result.Count == 0 ? new[] { (level, 1.0) } : result;
            }

            public IReadOnlyList<EquipmentRegenerationMaterial> GetMaterials(int group, bool specific, int level)
            {
                var source = specific ? SpecificMaterials : RandomMaterials;
                if (!source.TryGetValue(group, out var baseMaterials))
                    return Array.Empty<EquipmentRegenerationMaterial>();

                var costs = specific ? SpecificLevelCosts : RandomLevelCosts;
                var multiplier = 1;
                if (costs.TryGetValue(group, out var byLevel))
                {
                    if (!byLevel.TryGetValue(level, out multiplier))
                        multiplier = byLevel
                            .Where(pair => pair.Key <= level)
                            .OrderByDescending(pair => pair.Key)
                            .Select(pair => pair.Value)
                            .FirstOrDefault(1);
                }
                multiplier = Math.Max(1, multiplier);
                return baseMaterials.Select(material => new EquipmentRegenerationMaterial
                {
                    ItemTemplateId = material.ItemTemplateId,
                    Count = checked(material.Count * multiplier),
                }).ToList();
            }

            public bool IsLegalPart(string groupName, ushort part)
            {
                if (part == 0)
                    return true;
                return Parts.TryGetValue(part, out var groups)
                    && groups.Contains(groupName ?? string.Empty);
            }

            public bool IsKnownGroup(string groupName)
                => Parts.Values.Any(groups => groups.Contains(groupName ?? string.Empty));
        }

        internal sealed class RegenLevelLimit
        {
            public int CurrentWeight { get; set; }
            public int PlusFiveWeight { get; set; }
        }
    }
}
