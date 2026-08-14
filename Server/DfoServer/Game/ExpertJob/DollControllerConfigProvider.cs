using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class DollControllerConfig : IExpertJobExtractionConfig
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

        byte IExpertJobExtractionConfig.ExpertJobType => ExpertJobStateCodec.DollControllerType;
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

    internal static class DollControllerConfigProvider
    {
        private const string PvfPath = "character/expertjob/doll_controller.exj";
        private static readonly Lazy<DollControllerConfig> ConfigValue =
            new Lazy<DollControllerConfig>(Load);

        internal static DollControllerConfig Config => ConfigValue.Value;

        private static DollControllerConfig Load()
        {
            var content = PvfArchiveAccessor.ReadText(PvfPath);
            var root = new ScriptParser().Parse(content);
            var config = new DollControllerConfig
            {
                RecipeConfig = ExpertJobRecipeConfigParser.Parse(
                    root,
                    content,
                    PvfPath,
                    ExpertJobStateCodec.DollControllerType,
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
                "doll_controller",
                config.RecipeConfig.ExperienceThresholds.Count,
                item => item.DollControllerExtractionIndex,
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
