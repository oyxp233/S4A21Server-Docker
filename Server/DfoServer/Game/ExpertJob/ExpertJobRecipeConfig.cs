using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DfoServer.Game.Inventory;
using PvfLib;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class ExpertJobRecipeDefinition
    {
        internal int RecipeItemId { get; set; }
        internal int ProductItemId { get; set; }
        internal int RequiredLevel { get; set; }
        internal int MinimumExperienceGain { get; set; }
        internal int MaximumExperienceGain { get; set; }
    }

    internal sealed class ExpertJobCompoundRateDefinition
    {
        internal int MaximumLevelDifference { get; set; }
        internal int SuccessRatePercent { get; set; }
        internal int MinimumExperienceGain { get; set; }
        internal int MaximumExperienceGain { get; set; }
    }

    internal sealed class ExpertJobRecipeConfig
    {
        private const int RecipeLearningLevelOffset = 2;

        internal byte ExpertJobType { get; set; }
        internal List<int> ExperienceThresholds { get; } = new List<int>();
        internal Dictionary<int, int> AutoLearnRecipes { get; } = new Dictionary<int, int>();
        internal Dictionary<int, int> Skills { get; } = new Dictionary<int, int>();
        internal List<ExpertJobCompoundRateDefinition> CompoundRates { get; } =
            new List<ExpertJobCompoundRateDefinition>();
        internal Dictionary<int, ExpertJobRecipeDefinition> RecipesByItemId { get; } =
            new Dictionary<int, ExpertJobRecipeDefinition>();

        internal int GetLevel(uint experience)
        {
            var level = 1;
            foreach (var threshold in ExperienceThresholds)
            {
                if (experience < threshold)
                    break;
                level++;
            }
            return Math.Min(ExperienceThresholds.Count, level);
        }

        internal bool CanLearnRecipe(uint experience, ExpertJobRecipeDefinition recipe)
            => recipe != null
                && recipe.RequiredLevel <= GetLevel(experience) + RecipeLearningLevelOffset;

        internal IReadOnlyList<int> GetAutoLearnRecipeIds(uint experience)
        {
            var level = GetLevel(experience);
            return AutoLearnRecipes
                .Where(pair => pair.Key <= level)
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .ToArray();
        }

        internal IReadOnlyList<int> GetNewAutoLearnRecipeIds(
            uint previousExperience,
            uint currentExperience)
        {
            var previousLevel = GetLevel(previousExperience);
            var currentLevel = GetLevel(currentExperience);
            if (currentLevel <= previousLevel)
                return Array.Empty<int>();
            return AutoLearnRecipes
                .Where(pair => pair.Key > previousLevel && pair.Key <= currentLevel)
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .ToArray();
        }

        internal ExpertJobCompoundRateDefinition ResolveCompoundRate(
            int recipeLevel,
            int expertJobLevel)
        {
            if (CompoundRates.Count == 0)
                return null;

            var levelDifference = recipeLevel - expertJobLevel;
            foreach (var rate in CompoundRates)
            {
                if (rate.MaximumLevelDifference >= levelDifference)
                    return rate;
            }
            return CompoundRates[CompoundRates.Count - 1];
        }
    }

    internal static class ExpertJobRecipeConfigParser
    {
        internal static ExpertJobRecipeConfig Parse(
            ScriptNode root,
            string content,
            string pvfPath,
            byte expertJobType,
            bool requireProductExperience)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            var config = new ExpertJobRecipeConfig { ExpertJobType = expertJobType };
            ParseExperienceThresholds(
                ExpertJobPvfValueReader.ReadTokens(root, content, "expertness exp"),
                pvfPath,
                config.ExperienceThresholds);
            ParsePairs(
                ExpertJobPvfValueReader.ReadTokens(root, content, "auto learn recipe"),
                pvfPath,
                "auto learn recipe",
                config.AutoLearnRecipes);
            ParsePairs(
                ExpertJobPvfValueReader.ReadTokens(root, content, "skill"),
                pvfPath,
                "skill",
                config.Skills);
            ParseCompoundRates(
                ExpertJobPvfValueReader.ReadTokens(root, content, "compound rate"),
                pvfPath,
                config.CompoundRates);

            var productExperience = ExpertJobPvfValueReader.ParseExperienceRanges(
                ExpertJobPvfValueReader.ReadTokens(root, content, "product exp"),
                pvfPath,
                "product exp",
                allowEmpty: !requireProductExperience);
            ParseRecipes(
                ExpertJobPvfValueReader.ReadTokens(root, content, "items"),
                productExperience,
                pvfPath,
                requireProductExperience,
                config);
            Validate(config, pvfPath);
            return config;
        }

        private static void ParseExperienceThresholds(
            string[] tokens,
            string pvfPath,
            ICollection<int> target)
        {
            if (tokens.Length == 0 || tokens.Length % 3 != 0)
                throw new InvalidOperationException($"PVF {pvfPath} [expertness exp] row width is not 3");
            var previous = -1;
            for (var index = 0; index < tokens.Length; index += 3)
            {
                var threshold = ExpertJobPvfValueReader.ParseInt(tokens[index]);
                if (threshold <= previous)
                    throw new InvalidOperationException($"PVF {pvfPath} has invalid expertness thresholds");
                target.Add(threshold);
                previous = threshold;
            }
        }

        private static void ParsePairs(
            string[] tokens,
            string pvfPath,
            string tag,
            IDictionary<int, int> target)
        {
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                throw new InvalidOperationException($"PVF {pvfPath} [{tag}] row width is not 2");
            for (var index = 0; index < tokens.Length; index += 2)
            {
                var key = ExpertJobPvfValueReader.ParseInt(tokens[index]);
                var value = ExpertJobPvfValueReader.ParseInt(tokens[index + 1]);
                if (key <= 0 || value <= 0 || target.ContainsKey(key))
                    throw new InvalidOperationException($"PVF {pvfPath} [{tag}] has invalid or duplicate values");
                target.Add(key, value);
            }
        }

        private static void ParseCompoundRates(
            string[] tokens,
            string pvfPath,
            ICollection<ExpertJobCompoundRateDefinition> target)
        {
            if (tokens.Length == 0)
                return;
            if (tokens.Length % 4 != 0)
                throw new InvalidOperationException($"PVF {pvfPath} [compound rate] row width is not 4");

            var previousDifference = int.MinValue;
            for (var index = 0; index < tokens.Length; index += 4)
            {
                var rate = new ExpertJobCompoundRateDefinition
                {
                    MaximumLevelDifference = ExpertJobPvfValueReader.ParseInt(tokens[index]),
                    SuccessRatePercent = ExpertJobPvfValueReader.ParseInt(tokens[index + 1]),
                    MinimumExperienceGain = ExpertJobPvfValueReader.ParseInt(tokens[index + 2]),
                    MaximumExperienceGain = ExpertJobPvfValueReader.ParseInt(tokens[index + 3]),
                };
                if (rate.MaximumLevelDifference <= previousDifference
                    || rate.SuccessRatePercent < 0
                    || rate.SuccessRatePercent > 100
                    || rate.MinimumExperienceGain < 0
                    || rate.MaximumExperienceGain < rate.MinimumExperienceGain)
                {
                    throw new InvalidOperationException($"PVF {pvfPath} has invalid compound rate values");
                }
                target.Add(rate);
                previousDifference = rate.MaximumLevelDifference;
            }
        }

        private static void ParseRecipes(
            string[] tokens,
            IReadOnlyDictionary<int, (int Minimum, int Maximum)> productExperience,
            string pvfPath,
            bool requireProductExperience,
            ExpertJobRecipeConfig config)
        {
            if (tokens.Length == 0 || tokens.Length % 4 != 0)
                throw new InvalidOperationException($"PVF {pvfPath} [items] row width is not 4");

            for (var index = 0; index < tokens.Length; index += 4)
            {
                var recipeItemId = ExpertJobPvfValueReader.ParseInt(tokens[index + 1]);
                var productItemId = ExpertJobPvfValueReader.ParseInt(tokens[index + 2]);
                var requiredLevel = ExpertJobPvfValueReader.ParseInt(tokens[index + 3]);
                var hasExperience = productExperience.TryGetValue(recipeItemId, out var experience);
                if (requireProductExperience && !hasExperience)
                    continue;
                if (!ItemMetadataResolver.TryLoadStackableFile(recipeItemId, out var recipeItem)
                    || !IsRecipeForExpertJob(recipeItem, config.Skills))
                    continue;
                if (recipeItemId <= 0
                    || productItemId <= 0
                    || requiredLevel <= 0
                    || requiredLevel > config.ExperienceThresholds.Count
                    || config.RecipesByItemId.ContainsKey(recipeItemId))
                {
                    throw new InvalidOperationException($"PVF {pvfPath} has invalid recipe definition");
                }

                config.RecipesByItemId.Add(recipeItemId, new ExpertJobRecipeDefinition
                {
                    RecipeItemId = recipeItemId,
                    ProductItemId = productItemId,
                    RequiredLevel = requiredLevel,
                    MinimumExperienceGain = hasExperience ? experience.Minimum : 0,
                    MaximumExperienceGain = hasExperience ? experience.Maximum : 0,
                });
            }

            if (config.RecipesByItemId.Count == 0)
                throw new InvalidOperationException($"PVF {pvfPath} has no learnable recipe definitions");
        }

        private static bool IsRecipeForExpertJob(
            StackableItemFile item,
            IReadOnlyDictionary<int, int> skills)
        {
            if (item == null
                || !ExpertJobPvfValueReader.NormalizeTag(item.StackableType).StartsWith(
                    "[recipe]",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    ExpertJobPvfValueReader.NormalizeTag(item.ItemCategory),
                    "expertjob recipe",
                    StringComparison.OrdinalIgnoreCase))
                return false;

            var needSkill = (item.NeedSkill ?? string.Empty)
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return needSkill.Length >= 2
                && int.TryParse(
                    needSkill[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var skillId)
                && int.TryParse(
                    needSkill[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var skillLevel)
                && skillLevel > 0
                && skills.ContainsKey(skillId);
        }

        private static void Validate(ExpertJobRecipeConfig config, string pvfPath)
        {
            var maximumLevel = config.ExperienceThresholds.Count;
            if (maximumLevel == 0
                || config.AutoLearnRecipes.Count == 0
                || config.Skills.Count == 0
                || config.AutoLearnRecipes.Any(pair => pair.Key > maximumLevel)
                || config.AutoLearnRecipes.Values.Distinct().Count() != config.AutoLearnRecipes.Count
                || config.Skills.Any(pair => pair.Key > byte.MaxValue || pair.Value > maximumLevel))
            {
                throw new InvalidOperationException($"PVF {pvfPath} has invalid recipe cross references");
            }
        }
    }

    internal static class ExpertJobPvfValueReader
    {
        internal static string[] ReadTokens(ScriptNode root, string content, string tag)
        {
            var node = root?.Children.FirstOrDefault(child =>
                string.Equals(child.Tag, tag, StringComparison.OrdinalIgnoreCase));
            return node == null
                ? Array.Empty<string>()
                : node.GetFirstDataContent(content)
                    .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        internal static Dictionary<int, (int Minimum, int Maximum)> ParseExperienceRanges(
            string[] tokens,
            string pvfPath,
            string tag,
            bool allowEmpty = false)
        {
            if ((tokens.Length == 0 && !allowEmpty) || tokens.Length % 3 != 0)
                throw new InvalidOperationException($"PVF {pvfPath} [{tag}] row width is not 3");
            var result = new Dictionary<int, (int Minimum, int Maximum)>();
            for (var index = 0; index < tokens.Length; index += 3)
            {
                var itemId = ParseInt(tokens[index]);
                var minimum = ParseInt(tokens[index + 1]);
                var maximum = ParseInt(tokens[index + 2]);
                if (itemId <= 0 || minimum < 0 || maximum < minimum || result.ContainsKey(itemId))
                    throw new InvalidOperationException($"PVF {pvfPath} has invalid {tag} range");
                result.Add(itemId, (minimum, maximum));
            }
            return result;
        }

        internal static int ParseInt(string value)
            => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        internal static double ParseDouble(string value)
            => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

        internal static string NormalizeTag(string value)
            => (value ?? string.Empty).Replace("`", string.Empty).Trim();
    }
}
