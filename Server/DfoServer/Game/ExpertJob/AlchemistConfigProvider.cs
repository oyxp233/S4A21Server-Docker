using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class AlchemistConfig : IExpertJobExtractionConfig
    {
        internal ExpertJobRecipeConfig RecipeConfig { get; set; }
        internal Dictionary<int, ExpertJobExtractorDefinition> Extractors { get; } =
            new Dictionary<int, ExpertJobExtractorDefinition>();
        internal Dictionary<(int ExtractorItemId, int Rarity, int EquipmentState), ExpertJobExtractionRule>
            ExtractionRules { get; } =
                new Dictionary<(int ExtractorItemId, int Rarity, int EquipmentState), ExpertJobExtractionRule>();
        internal Dictionary<int, List<ExpertJobExtractionSelectionRule>> AdditionalResults { get; } =
            new Dictionary<int, List<ExpertJobExtractionSelectionRule>>();
        internal Dictionary<int, List<ExpertJobExtractionSelectionRule>> BigWinResults { get; } =
            new Dictionary<int, List<ExpertJobExtractionSelectionRule>>();

        byte IExpertJobExtractionConfig.ExpertJobType => ExpertJobStateCodec.AlchemistType;
        ExpertJobRecipeConfig IExpertJobExtractionConfig.RecipeConfig => RecipeConfig;
        IReadOnlyDictionary<int, ExpertJobExtractorDefinition> IExpertJobExtractionConfig.Extractors
            => Extractors;
        IReadOnlyDictionary<(int ExtractorItemId, int Rarity, int EquipmentState), ExpertJobExtractionRule>
            IExpertJobExtractionConfig.ExtractionRules => ExtractionRules;
        IReadOnlyDictionary<int, List<ExpertJobExtractionSelectionRule>>
            IExpertJobExtractionConfig.AdditionalResults => AdditionalResults;
        IReadOnlyDictionary<int, List<ExpertJobExtractionSelectionRule>>
            IExpertJobExtractionConfig.BigWinResults => BigWinResults;

        int IExpertJobExtractionConfig.CalculateBaseMaterialCount(
            ItemMetadata metadata,
            ExpertJobExtractionRule rule)
            => Math.Max(1, (int)Math.Floor(Math.Max(1, metadata?.Grade ?? 0) * rule.Multiplier));
    }

    internal static class AlchemistConfigProvider
    {
        private const string PvfPath = "character/expertjob/alchemist.exj";
        private static readonly Lazy<AlchemistConfig> ConfigValue = new Lazy<AlchemistConfig>(Load);

        internal static AlchemistConfig Config => ConfigValue.Value;

        private static AlchemistConfig Load()
        {
            var content = PvfArchiveAccessor.ReadText(PvfPath);
            var root = new ScriptParser().Parse(content);
            var config = new AlchemistConfig
            {
                RecipeConfig = ExpertJobRecipeConfigParser.Parse(
                    root,
                    content,
                    PvfPath,
                    ExpertJobStateCodec.AlchemistType,
                    requireProductExperience: false),
            };
            var extractionExperience = ExpertJobPvfValueReader.ParseExperienceRanges(
                ExpertJobPvfValueReader.ReadTokens(root, content, "extract exp"),
                PvfPath,
                "extract exp");
            ExpertJobExtractionConfigParser.ParseExtractors(
                ExpertJobPvfValueReader.ReadTokens(root, content, "items"),
                extractionExperience,
                PvfPath,
                "alchemist",
                config.RecipeConfig.ExperienceThresholds.Count,
                item => item.AlchemistExtractionIndex,
                config.Extractors);
            ExpertJobExtractionConfigParser.ParseExtractionRules(
                ExpertJobPvfValueReader.ReadTokens(root, content, "extraction result"),
                PvfPath,
                config.ExtractionRules);
            ExpertJobExtractionConfigParser.ParseSelections(
                ExpertJobPvfValueReader.ReadTokens(root, content, "additional result"),
                PvfPath,
                config.AdditionalResults);
            ExpertJobExtractionConfigParser.ParseSelections(
                ExpertJobPvfValueReader.ReadTokens(root, content, "big win result"),
                PvfPath,
                config.BigWinResults);
            ExpertJobExtractionConfigParser.ValidateReferences(
                PvfPath,
                config.Extractors,
                config.ExtractionRules,
                config.AdditionalResults,
                config.BigWinResults);
            return config;
        }
    }
}
