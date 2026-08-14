using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class EnchanterCardRecipeDefinition
    {
        internal int Qualification { get; set; }
        internal int RecipeItemId { get; set; }
        internal int RequiredLevel { get; set; }
        internal IReadOnlyList<InventoryMaterialRequirement> Materials { get; set; } =
            Array.Empty<InventoryMaterialRequirement>();
    }

    internal sealed class EnchanterCardDefinition
    {
        internal int Qualification { get; set; }
    }

    internal sealed class EnchanterCardExperienceRule
    {
        internal int ExpertJobLevel { get; set; }
        internal int[] SuccessRates { get; set; } = Array.Empty<int>();
        internal int ExtraRate { get; set; }
        internal int MinimumExperienceGain { get; set; }
        internal int MaximumExperienceGain { get; set; }
    }

    internal sealed class EnchanterConfig : IExpertJobExtractionConfig
    {
        internal ExpertJobRecipeConfig RecipeConfig { get; set; }
        internal int MaximumStoreCharge { get; set; }
        internal int InitialEndurance { get; set; }
        internal int EnduranceReduction { get; set; }
        internal int EnduranceReductionMinimumLevel { get; set; }
        internal int ExtractionBaseConst { get; set; }
        internal Dictionary<int, ExpertJobExtractorDefinition> Extractors { get; } =
            new Dictionary<int, ExpertJobExtractorDefinition>();
        internal Dictionary<(int ExtractorItemId, int Rarity, int EquipmentState), ExpertJobExtractionRule>
            ExtractionRules { get; } =
                new Dictionary<(int ExtractorItemId, int Rarity, int EquipmentState), ExpertJobExtractionRule>();
        internal Dictionary<int, List<ExpertJobExtractionSelectionRule>> AdditionalResults { get; } =
            new Dictionary<int, List<ExpertJobExtractionSelectionRule>>();
        internal Dictionary<int, List<ExpertJobExtractionSelectionRule>> BigWinResults { get; } =
            new Dictionary<int, List<ExpertJobExtractionSelectionRule>>();
        internal List<int> ExperienceThresholds => RecipeConfig.ExperienceThresholds;
        internal Dictionary<int, int> AutoLearnRecipes => RecipeConfig.AutoLearnRecipes;
        internal Dictionary<int, int> StoreSkills => RecipeConfig.Skills;
        internal Dictionary<int, int> CardQualificationLevelRequirements { get; } =
            new Dictionary<int, int>();
        internal Dictionary<int, EnchanterCardRecipeDefinition> CardRecipesByItemId { get; } =
            new Dictionary<int, EnchanterCardRecipeDefinition>();
        internal Dictionary<int, EnchanterCardDefinition> CardsByItemId { get; } =
            new Dictionary<int, EnchanterCardDefinition>();
        internal Dictionary<int, int> BeadItemIdByCardItemId { get; } =
            new Dictionary<int, int>();
        internal Dictionary<int, EnchanterCardExperienceRule> CardExperienceRulesByLevel { get; } =
            new Dictionary<int, EnchanterCardExperienceRule>();
        internal Dictionary<int, ExpertJobRecipeDefinition> RecipesByItemId =>
            RecipeConfig.RecipesByItemId;
        internal List<EnchanterRepairRule> RepairRules { get; } = new List<EnchanterRepairRule>();

        internal int GetLevel(uint experience) => RecipeConfig.GetLevel(experience);

        internal bool CanLearnRecipe(uint experience, ExpertJobRecipeDefinition recipe)
            => RecipeConfig.CanLearnRecipe(experience, recipe);

        internal IReadOnlyList<int> GetAutoLearnRecipeIds(uint experience)
        {
            return RecipeConfig.GetAutoLearnRecipeIds(experience);
        }

        internal IReadOnlyList<int> GetNewAutoLearnRecipeIds(
            uint previousExperience,
            uint currentExperience)
        {
            return RecipeConfig.GetNewAutoLearnRecipeIds(previousExperience, currentExperience);
        }

        internal IReadOnlyList<byte> GetStoreSkillIds(uint experience)
        {
            var level = GetLevel(experience);
            return StoreSkills
                .Where(pair => pair.Value <= level && pair.Key <= byte.MaxValue)
                .OrderBy(pair => pair.Key)
                .Select(pair => (byte)pair.Key)
                .ToArray();
        }

        internal IReadOnlyList<byte> GetCardQualificationLevels(uint experience)
        {
            var level = GetLevel(experience);
            return CardQualificationLevelRequirements
                .Where(pair => pair.Value <= level && pair.Key >= 0 && pair.Key <= byte.MaxValue)
                .OrderBy(pair => pair.Key)
                .Select(pair => (byte)pair.Key)
                .ToArray();
        }

        internal EnchanterRepairRule GetRepairRule(int level)
            => level > 0 && level <= RepairRules.Count ? RepairRules[level - 1] : null;

        byte IExpertJobExtractionConfig.ExpertJobType => ExpertJobStateCodec.EnchanterType;
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
            => Math.Max(
                1,
                (int)Math.Floor(
                    Math.Max(1, metadata?.SellGold ?? 0) * rule.Multiplier / ExtractionBaseConst));
    }

    internal sealed class EnchanterRepairRule
    {
        internal int FullRepairCost { get; set; }
        internal int MaximumEndurance { get; set; }
    }

    internal static class EnchanterConfigProvider
    {
        private const string PvfPath = "character/expertjob/enchanter.exj";
        private static readonly Lazy<EnchanterConfig> ConfigValue = new Lazy<EnchanterConfig>(Load);

        internal static EnchanterConfig Config => ConfigValue.Value;

        private static EnchanterConfig Load()
        {
            var content = PvfArchiveAccessor.ReadText(PvfPath);
            var root = new ScriptParser().Parse(content);
            var config = new EnchanterConfig
            {
                RecipeConfig = ExpertJobRecipeConfigParser.Parse(
                    root,
                    content,
                    PvfPath,
                    ExpertJobStateCodec.EnchanterType,
                    requireProductExperience: true),
                MaximumStoreCharge = ReadSingleInt(root, content, "limit store charge"),
                InitialEndurance = ReadSingleInt(root, content, "endurance initial value"),
            };
            var enduranceReduction = ReadTokens(root, content, "endurance reduce");
            if (enduranceReduction.Length != 2)
                throw new InvalidOperationException($"PVF {PvfPath} [endurance reduce] is invalid");
            config.EnduranceReduction = ParseInt(enduranceReduction[1]);
            config.EnduranceReductionMinimumLevel = ParseInt(enduranceReduction[0]);
            ParseRepairRules(ReadTokens(root, content, "endurance repair cost"), config);

            ParseCardQualifications(root, content, config);
            ParseCardExperienceRules(ReadTokens(root, content, "monstercard exp"), config);
            var extractionExperience = ExpertJobPvfValueReader.ParseExperienceRanges(
                ReadTokens(root, content, "extract exp"),
                PvfPath,
                "extract exp");
            ExpertJobExtractionConfigParser.ParseExtractors(
                ReadTokens(root, content, "items"),
                extractionExperience,
                PvfPath,
                "enchanter",
                config.ExperienceThresholds.Count,
                item => item.EnchanterExtractionIndex,
                config.Extractors);
            ParseExtractionBase(ReadTokens(root, content, "enchanter extraction result item"), config);
            ExpertJobExtractionConfigParser.ParseExtractionRules(
                ReadTokens(root, content, "extraction result"),
                PvfPath,
                config.ExtractionRules);
            ExpertJobExtractionConfigParser.ParseSelections(
                ReadTokens(root, content, "additional result"),
                PvfPath,
                config.AdditionalResults);
            ExpertJobExtractionConfigParser.ParseSelections(
                ReadTokens(root, content, "big win result"),
                PvfPath,
                config.BigWinResults);

            if (config.MaximumStoreCharge < 0
                || config.InitialEndurance <= 0
                || config.EnduranceReduction <= 0
                || config.EnduranceReductionMinimumLevel <= 0
                || config.RepairRules.Count != config.ExperienceThresholds.Count
                || config.ExtractionBaseConst <= 0
                || config.ExperienceThresholds.Count == 0
                || config.AutoLearnRecipes.Count == 0
                || config.Extractors.Count == 0
                || config.ExtractionRules.Count == 0
                || config.CardQualificationLevelRequirements.Count == 0
                || config.CardRecipesByItemId.Count != config.CardQualificationLevelRequirements.Count
                || config.CardsByItemId.Count == 0
                || config.BeadItemIdByCardItemId.Count == 0
                || config.CardExperienceRulesByLevel.Count != config.ExperienceThresholds.Count)
            {
                throw new InvalidOperationException($"PVF {PvfPath} has invalid enchanter configuration");
            }
            ValidateReferences(config);
            ExpertJobExtractionConfigParser.ValidateReferences(
                PvfPath,
                config.Extractors,
                config.ExtractionRules,
                config.AdditionalResults,
                config.BigWinResults);
            return config;
        }

        private static string NormalizeTag(string value)
            => (value ?? string.Empty).Replace("`", string.Empty).Trim();

        private static bool HasRequiredEnchanterSkill(
            string needSkill,
            IReadOnlyDictionary<int, int> skills)
        {
            var values = (needSkill ?? string.Empty)
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 2
                || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var skillId)
                || !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var skillLevel))
                return false;
            return skillLevel > 0 && skills.ContainsKey(skillId);
        }

        private static void ParseRepairRules(string[] tokens, EnchanterConfig config)
        {
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [endurance repair cost] is invalid");
            for (var index = 0; index < tokens.Length; index += 2)
            {
                var cost = ParseInt(tokens[index]);
                var endurance = ParseInt(tokens[index + 1]);
                if (cost <= 0 || endurance <= 0)
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid endurance repair rule");
                config.RepairRules.Add(new EnchanterRepairRule
                {
                    FullRepairCost = cost,
                    MaximumEndurance = endurance,
                });
            }
        }

        private static void ParseExtractionBase(string[] tokens, EnchanterConfig config)
        {
            if (tokens.Length != 2 || ParseInt(tokens[0]) <= 0)
                throw new InvalidOperationException($"PVF {PvfPath} [enchanter extraction result item] is invalid");
            config.ExtractionBaseConst = ParseInt(tokens[1]);
        }

        private static void ValidateReferences(EnchanterConfig config)
        {
            var maximumLevel = config.ExperienceThresholds.Count;
            if (config.CardQualificationLevelRequirements.Any(pair =>
                    pair.Key < 0 || pair.Key > byte.MaxValue || pair.Value <= 0 || pair.Value > maximumLevel)
                || config.CardRecipesByItemId.Values.Any(recipe =>
                    !config.CardQualificationLevelRequirements.TryGetValue(
                        recipe.Qualification, out var requiredLevel)
                    || requiredLevel != recipe.RequiredLevel
                    || recipe.Materials.Count == 0)
                || config.CardsByItemId.Keys.Any(cardItemId =>
                    !config.BeadItemIdByCardItemId.ContainsKey(cardItemId))
                || config.CardExperienceRulesByLevel.Keys.Any(level =>
                    level <= 0 || level > maximumLevel))
            {
                throw new InvalidOperationException($"PVF {PvfPath} has invalid enchanter cross references");
            }
        }

        private static void ParseCardQualifications(
            ScriptNode root,
            string content,
            EnchanterConfig config)
        {
            var rarityRecipe = root.GetChild("rarity recipe");
            if (rarityRecipe == null)
                throw new InvalidOperationException($"PVF {PvfPath} has no [rarity recipe]");

            foreach (var rarityNode in rarityRecipe.GetChildren("rarity"))
            {
                var rarity = ParseSingleNodeInt(rarityNode, content, "rarity");
                var recipeItemId = ParseSingleNodeInt(rarityNode, content, "recipe");
                var requiredLevel = ParseSingleNodeInt(rarityNode, content, "expert job level");
                if (!DfoServer.Game.Inventory.ItemMetadataResolver.TryLoadStackableFile(
                        recipeItemId,
                        out var recipeItem)
                    || !NormalizeTag(recipeItem.StackableType).StartsWith(
                        "[recipe]",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        NormalizeTag(recipeItem.ExpertJobOnlyType),
                        "enchanter",
                        StringComparison.OrdinalIgnoreCase)
                    || recipeItem.ExpertJobOnlyLevel != requiredLevel
                    || !HasRequiredEnchanterSkill(recipeItem.NeedSkill, config.StoreSkills))
                {
                    throw new InvalidOperationException($"PVF {PvfPath} has an invalid card recipe item");
                }

                var materials = ParseMaterialRequirements(recipeItem.InputItem);
                if (rarity < 0 || rarity > byte.MaxValue || recipeItemId <= 0 || requiredLevel <= 0
                    || materials.Count == 0
                    || config.CardQualificationLevelRequirements.ContainsKey(rarity)
                    || config.CardRecipesByItemId.ContainsKey(recipeItemId))
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid rarity recipe");
                config.CardQualificationLevelRequirements.Add(rarity, requiredLevel);
                config.CardRecipesByItemId.Add(recipeItemId, new EnchanterCardRecipeDefinition
                {
                    Qualification = rarity,
                    RecipeItemId = recipeItemId,
                    RequiredLevel = requiredLevel,
                    Materials = materials,
                });

                var baseResults = ReadTokens(rarityNode, content, "base result");
                if (baseResults.Length == 0 || baseResults.Length % 2 != 0)
                    throw new InvalidOperationException($"PVF {PvfPath} [base result] row width is not 2");
                for (var index = 0; index < baseResults.Length; index += 2)
                {
                    var cardItemId = ParseInt(baseResults[index]);
                    var beadItemId = ParseInt(baseResults[index + 1]);
                    DfoServer.Game.Inventory.ItemMetadataResolver.TryLoadStackableFile(
                        cardItemId,
                        out var card);
                    DfoServer.Game.Inventory.ItemMetadataResolver.TryLoadStackableFile(
                        beadItemId,
                        out var bead);
                    if (cardItemId <= 0
                        || beadItemId <= 0
                        || config.BeadItemIdByCardItemId.ContainsKey(cardItemId)
                        || (ItemMetadataResolver.IsMonsterCardBead(bead)
                            && !BeadContainsCardId(bead, cardItemId)))
                    {
                        throw new InvalidOperationException(
                            $"PVF {PvfPath} has an invalid card bead result " +
                            $"card={cardItemId} bead={beadItemId}");
                    }
                    config.BeadItemIdByCardItemId.Add(cardItemId, beadItemId);
                    if (DfoServer.Game.Inventory.ItemMetadataResolver.IsEnchanterCard(card))
                    {
                        config.CardsByItemId[cardItemId] = new EnchanterCardDefinition
                        {
                            Qualification = rarity,
                        };
                    }
                }
            }

        }

        private static bool BeadContainsCardId(StackableItemFile bead, int cardItemId)
        {
            if (bead == null || cardItemId <= 0)
                return false;

            if (bead.MonsterCardIds != null)
            {
                foreach (var beadCardId in bead.MonsterCardIds)
                {
                    if (beadCardId == cardItemId)
                        return true;
                }
            }

            return bead.MonsterCardId == cardItemId;
        }

        private static IReadOnlyList<InventoryMaterialRequirement> ParseMaterialRequirements(
            string inputItems)
        {
            var tokens = (inputItems ?? string.Empty)
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                return Array.Empty<InventoryMaterialRequirement>();

            var merged = new Dictionary<int, int>();
            for (var index = 0; index < tokens.Length; index += 2)
            {
                var itemId = ParseInt(tokens[index]);
                var count = ParseInt(tokens[index + 1]);
                if (itemId <= 0
                    || count <= 0
                    || !DfoServer.Game.Inventory.ItemMetadataResolver.TryLoadStackableFile(
                        itemId,
                        out _))
                {
                    return Array.Empty<InventoryMaterialRequirement>();
                }

                merged[itemId] = checked((merged.TryGetValue(itemId, out var current) ? current : 0) + count);
            }

            return merged
                .OrderBy(pair => pair.Key)
                .Select(pair => new InventoryMaterialRequirement(pair.Key, pair.Value))
                .ToArray();
        }

        private static void ParseCardExperienceRules(string[] tokens, EnchanterConfig config)
        {
            const int rowWidth = 9;
            if (tokens.Length == 0 || tokens.Length % rowWidth != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [monstercard exp] row width is not 9");

            for (var index = 0; index < tokens.Length; index += rowWidth)
            {
                var level = ParseInt(tokens[index]);
                var rates = new int[5];
                for (var rateIndex = 0; rateIndex < rates.Length; rateIndex++)
                    rates[rateIndex] = ParseInt(tokens[index + 1 + rateIndex]);
                var rule = new EnchanterCardExperienceRule
                {
                    ExpertJobLevel = level,
                    SuccessRates = rates,
                    ExtraRate = ParseInt(tokens[index + 6]),
                    MinimumExperienceGain = ParseInt(tokens[index + 7]),
                    MaximumExperienceGain = ParseInt(tokens[index + 8]),
                };
                if (level <= 0
                    || config.CardExperienceRulesByLevel.ContainsKey(level)
                    || rates.Any(rate => rate < 0 || rate > 100)
                    || rule.ExtraRate < 0
                    || rule.ExtraRate > 100
                    || rule.MinimumExperienceGain < 0
                    || rule.MaximumExperienceGain < rule.MinimumExperienceGain)
                {
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid monstercard exp rule");
                }
                config.CardExperienceRulesByLevel.Add(level, rule);
            }
        }

        private static int ParseSingleNodeInt(ScriptNode parent, string content, string tag)
        {
            var node = string.Equals(parent.Tag, tag, StringComparison.OrdinalIgnoreCase)
                ? parent
                : parent.GetChild(tag);
            var tokens = node == null
                ? Array.Empty<string>()
                : node.GetFirstDataContent(content).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 1)
                throw new InvalidOperationException($"PVF {PvfPath} [{tag}] must contain one integer");
            return ParseInt(tokens[0]);
        }

        private static int ReadSingleInt(ScriptNode root, string content, string tag)
        {
            var tokens = ReadTokens(root, content, tag);
            return tokens.Length == 0 ? 0 : ParseInt(tokens[0]);
        }

        private static string[] ReadTokens(ScriptNode root, string content, string tag)
        {
            var node = root.Children.FirstOrDefault(child =>
                string.Equals(child.Tag, tag, StringComparison.OrdinalIgnoreCase));
            return node == null
                ? Array.Empty<string>()
                : node.GetFirstDataContent(content).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        private static int ParseInt(string value)
            => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    }
}
