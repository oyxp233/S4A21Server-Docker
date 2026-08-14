using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Inventory;
using PvfLib;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class ExpertJobExtractionRule
    {
        internal int ResultItemId { get; set; }
        internal double Multiplier { get; set; }
        internal int AdditionalTable { get; set; }
        internal int BigWinTable { get; set; }
        internal int BigWinChancePercent { get; set; }
    }

    internal sealed class ExpertJobExtractorDefinition
    {
        internal int ItemId { get; set; }
        internal int RequiredExpertJobLevel { get; set; }
        internal int ExtractionIndex { get; set; }
        internal int MinimumExperienceGain { get; set; }
        internal int MaximumExperienceGain { get; set; }
    }

    internal sealed class ExpertJobExtractionSelectionRule : ExpertJobSelectionRule
    {
        internal double QuantityMultiplier { get; set; }
    }

    internal interface IExpertJobExtractionConfig
    {
        byte ExpertJobType { get; }
        ExpertJobRecipeConfig RecipeConfig { get; }
        IReadOnlyDictionary<int, ExpertJobExtractorDefinition> Extractors { get; }
        IReadOnlyDictionary<(int ExtractorItemId, int Rarity, int EquipmentState), ExpertJobExtractionRule>
            ExtractionRules { get; }
        IReadOnlyDictionary<int, List<ExpertJobExtractionSelectionRule>> AdditionalResults { get; }
        IReadOnlyDictionary<int, List<ExpertJobExtractionSelectionRule>> BigWinResults { get; }

        int CalculateBaseMaterialCount(ItemMetadata metadata, ExpertJobExtractionRule rule);
    }

    internal static class ExpertJobExtractionConfigParser
    {
        internal static void ParseExtractors(
            string[] itemTokens,
            IReadOnlyDictionary<int, (int Minimum, int Maximum)> extractionExperience,
            string pvfPath,
            string expertJobName,
            int maximumExpertJobLevel,
            Func<StackableItemFile, int> getExtractionIndex,
            IDictionary<int, ExpertJobExtractorDefinition> target)
        {
            if (itemTokens.Length == 0 || itemTokens.Length % 4 != 0)
                throw new InvalidOperationException($"PVF {pvfPath} [items] row width is not 4");
            for (var index = 0; index < itemTokens.Length; index += 4)
            {
                var productItemId = ExpertJobPvfValueReader.ParseInt(itemTokens[index + 2]);
                var requiredLevel = ExpertJobPvfValueReader.ParseInt(itemTokens[index + 3]);
                if (!extractionExperience.TryGetValue(productItemId, out var experience))
                    continue;
                if (requiredLevel <= 0
                    || requiredLevel > maximumExpertJobLevel
                    || target.ContainsKey(productItemId)
                    || !ItemMetadataResolver.TryLoadStackableFile(productItemId, out var item)
                    || !string.Equals(
                        ExpertJobPvfValueReader.NormalizeTag(item.ExpertJobOnlyType),
                        expertJobName,
                        StringComparison.OrdinalIgnoreCase)
                    || item.ExpertJobOnlyLevel != requiredLevel
                    || getExtractionIndex(item) < 0)
                {
                    throw new InvalidOperationException($"PVF {pvfPath} has invalid extractor definition");
                }
                target.Add(productItemId, new ExpertJobExtractorDefinition
                {
                    ItemId = productItemId,
                    RequiredExpertJobLevel = requiredLevel,
                    ExtractionIndex = getExtractionIndex(item),
                    MinimumExperienceGain = experience.Minimum,
                    MaximumExperienceGain = experience.Maximum,
                });
            }
            if (target.Count != extractionExperience.Count
                || target.Values.Select(item => item.ExtractionIndex).Distinct().Count() != target.Count)
                throw new InvalidOperationException($"PVF {pvfPath} has unresolved or duplicate extractors");
        }

        internal static void ParseExtractionRules(
            string[] tokens,
            string pvfPath,
            IDictionary<(int ExtractorItemId, int Rarity, int EquipmentState), ExpertJobExtractionRule>
                target)
        {
            if (tokens.Length == 0 || tokens.Length % 8 != 0)
                throw new InvalidOperationException($"PVF {pvfPath} [extraction result] row width is not 8");
            for (var index = 0; index < tokens.Length; index += 8)
            {
                var key = (
                    ExpertJobPvfValueReader.ParseInt(tokens[index]),
                    ExpertJobPvfValueReader.ParseInt(tokens[index + 1]),
                    ExpertJobPvfValueReader.ParseInt(tokens[index + 2]));
                var rule = new ExpertJobExtractionRule
                {
                    ResultItemId = ExpertJobPvfValueReader.ParseInt(tokens[index + 3]),
                    Multiplier = ExpertJobPvfValueReader.ParseDouble(tokens[index + 4]),
                    AdditionalTable = ExpertJobPvfValueReader.ParseInt(tokens[index + 5]),
                    BigWinTable = ExpertJobPvfValueReader.ParseInt(tokens[index + 6]),
                    BigWinChancePercent = ExpertJobPvfValueReader.ParseInt(tokens[index + 7]),
                };
                if (target.ContainsKey(key)
                    || key.Item1 <= 0
                    || key.Item2 < 0
                    || key.Item2 > 6
                    || key.Item3 < 0
                    || key.Item3 > 2
                    || rule.ResultItemId <= 0
                    || rule.Multiplier <= 0
                    || rule.AdditionalTable < 0
                    || rule.BigWinTable < 0
                    || rule.BigWinChancePercent < 0
                    || rule.BigWinChancePercent > 100)
                {
                    throw new InvalidOperationException($"PVF {pvfPath} has invalid extraction result values");
                }
                target.Add(key, rule);
            }
        }

        internal static void ParseSelections(
            string[] tokens,
            string pvfPath,
            IDictionary<int, List<ExpertJobExtractionSelectionRule>> target)
        {
            if (tokens.Length % 6 != 0)
                throw new InvalidOperationException($"PVF {pvfPath} selection row width is not 6");
            for (var index = 0; index < tokens.Length; index += 6)
            {
                var table = ExpertJobPvfValueReader.ParseInt(tokens[index]);
                if (!target.TryGetValue(table, out var rules))
                {
                    rules = new List<ExpertJobExtractionSelectionRule>();
                    target.Add(table, rules);
                }
                var rule = new ExpertJobExtractionSelectionRule
                {
                    MinimumLevel = ExpertJobPvfValueReader.ParseInt(tokens[index + 1]),
                    MaximumLevel = ExpertJobPvfValueReader.ParseInt(tokens[index + 2]),
                    ItemId = ExpertJobPvfValueReader.ParseInt(tokens[index + 3]),
                    Weight = ExpertJobPvfValueReader.ParseInt(tokens[index + 4]),
                    QuantityMultiplier = ExpertJobPvfValueReader.ParseDouble(tokens[index + 5]),
                };
                if (table <= 0
                    || rule.MinimumLevel < 0
                    || rule.MaximumLevel < rule.MinimumLevel
                    || rule.ItemId <= 0
                    || rule.Weight <= 0
                    || rule.QuantityMultiplier <= 0)
                {
                    throw new InvalidOperationException($"PVF {pvfPath} has invalid selection values");
                }
                rules.Add(rule);
            }
        }

        internal static void ValidateReferences(
            string pvfPath,
            IReadOnlyDictionary<int, ExpertJobExtractorDefinition> extractors,
            IReadOnlyDictionary<(int ExtractorItemId, int Rarity, int EquipmentState), ExpertJobExtractionRule>
                extractionRules,
            IReadOnlyDictionary<int, List<ExpertJobExtractionSelectionRule>> additionalResults,
            IReadOnlyDictionary<int, List<ExpertJobExtractionSelectionRule>> bigWinResults)
        {
            if (extractors.Count == 0 || extractionRules.Count == 0)
                throw new InvalidOperationException($"PVF {pvfPath} has no extraction configuration");
            foreach (var pair in extractionRules)
            {
                var rule = pair.Value;
                if (!extractors.ContainsKey(pair.Key.ExtractorItemId)
                    || (rule.AdditionalTable > 0 && !additionalResults.ContainsKey(rule.AdditionalTable))
                    || (rule.BigWinTable > 0 && !bigWinResults.ContainsKey(rule.BigWinTable)))
                {
                    throw new InvalidOperationException($"PVF {pvfPath} has unresolved extraction references");
                }
            }
        }
    }
}
