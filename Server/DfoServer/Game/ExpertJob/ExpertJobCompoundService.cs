using System;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Game.ExpertJob
{
    internal static class ExpertJobCompoundService
    {
        internal const byte ErrorInventoryFull = 4;
        internal const byte ErrorLevelTooLow = 14;
        internal const byte ErrorInvalidState = 19;
        internal const byte ErrorInsufficientMaterials = 21;

        internal static bool TryCraftProduct(
            InventoryService inventory,
            ExpertJobCompoundCommand command,
            uint currentExperience,
            ExpertJobState state,
            ExpertJobRecipeConfig config,
            IExpertJobExtractionConfig extractionConfig,
            out ExpertJobCompoundResult result)
        {
            result = new ExpertJobCompoundResult { ErrorCode = ErrorInvalidState };
            if (inventory == null
                || command == null
                || !command.IsProductCraft
                || command.RecipeItemId <= 0
                || command.RequestedCount == 0
                || state == null
                || config == null
                || !config.RecipesByItemId.TryGetValue(command.RecipeItemId, out var recipe)
                || !state.LearnedRecipeIds.Contains(recipe.RecipeItemId))
            {
                return false;
            }

            var expertJobLevel = config.GetLevel(currentExperience);
            if (config.CompoundRates.Count == 0
                && expertJobLevel < recipe.RequiredLevel)
            {
                result.ErrorCode = ErrorLevelTooLow;
                return false;
            }

            if (!InventoryCompoundItemRecipeService.TryParseCompoundRecipe(
                    command.RecipeItemId,
                    out var compoundRecipe)
                || compoundRecipe.Outputs.Count != 1
                || compoundRecipe.Outputs[0].ItemTemplateId != recipe.ProductItemId)
            {
                return false;
            }

            foreach (var output in compoundRecipe.Outputs)
            {
                var attemptedCount = (long)output.Count * command.RequestedCount;
                if (attemptedCount <= 0 || attemptedCount > int.MaxValue)
                    return false;
                result.AttemptedOutputs.Add(new ExpertJobCompoundOutput
                {
                    ItemId = output.ItemTemplateId,
                    Count = (int)attemptedCount,
                });
            }

            CalculateAttempts(
                command.RequestedCount,
                recipe,
                config.ResolveCompoundRate(recipe.RequiredLevel, expertJobLevel),
                out var successCount,
                out var experienceGain);
            var request = new CompoundItemRecipeRequest
            {
                SourceValue = command.RecipeItemId,
                SourceIsItemId = true,
                RequestedCount = command.RequestedCount,
                OutputCount = (ushort)successCount,
            };
            if (!InventoryCompoundItemRecipeService.TryCompoundItemRecipe(
                    inventory,
                    request,
                    out var compoundResult))
            {
                result.ErrorCode = compoundResult?.ErrorCode ?? ErrorInvalidState;
                return false;
            }

            foreach (var output in compoundResult.Rewards
                         .Where(item => item.ItemTemplateId > 0 && item.GrantedCount > 0)
                         .GroupBy(item => item.ItemTemplateId)
                         .OrderBy(group => group.Key))
            {
                result.Outputs.Add(new ExpertJobCompoundOutput
                {
                    ItemId = output.Key,
                    Count = output.Sum(item => item.GrantedCount),
                });
            }
            if (successCount > 0 && result.Outputs.Count == 0)
                throw new InvalidOperationException("expert-job product craft granted no output");
            foreach (var slotIndex in compoundResult.GetMainRefreshSlots())
                result.AddChangedMainSlot(slotIndex);

            result.SuccessCount = successCount;
            result.FailureCount = command.RequestedCount - successCount;
            result.ExperienceGain = experienceGain;
            result.ExtractorInventoryChanged = extractionConfig != null
                && extractionConfig.ExpertJobType == config.ExpertJobType
                && extractionConfig.Extractors.ContainsKey(recipe.ProductItemId);
            result.GoldSpent = compoundResult.GoldSpent;
            CompleteExperience(config, currentExperience, result);
            result.ErrorCode = 0;
            return true;
        }

        internal static void CompleteExperience(
            ExpertJobRecipeConfig config,
            uint currentExperience,
            ExpertJobCompoundResult result)
        {
            result.FinalExperience = (uint)Math.Min(
                uint.MaxValue,
                (ulong)currentExperience + (uint)Math.Max(0, result.ExperienceGain));
            result.LearnedRecipeIds.AddRange(config.GetNewAutoLearnRecipeIds(
                currentExperience,
                result.FinalExperience));
            result.RequiresExpertJobInfoRefresh = config.GetLevel(currentExperience)
                != config.GetLevel(result.FinalExperience);
        }

        private static void CalculateAttempts(
            ushort requestedCount,
            ExpertJobRecipeDefinition recipe,
            ExpertJobCompoundRateDefinition compoundRate,
            out int successCount,
            out int experienceGain)
        {
            successCount = 0;
            long totalExperience = 0;
            var successRate = compoundRate?.SuccessRatePercent ?? 100;
            var minimumExperience = compoundRate?.MinimumExperienceGain
                ?? recipe.MinimumExperienceGain;
            var maximumExperience = compoundRate?.MaximumExperienceGain
                ?? recipe.MaximumExperienceGain;

            for (var index = 0; index < requestedCount; index++)
            {
                if (ServerRandom.Next(100) >= successRate)
                    continue;
                successCount++;
                if (totalExperience < int.MaxValue)
                    totalExperience += NextInclusive(minimumExperience, maximumExperience);
            }
            experienceGain = (int)Math.Min(int.MaxValue, totalExperience);
        }

        internal static int NextInclusive(int minimum, int maximum)
            => maximum <= minimum
                ? minimum
                : minimum + ServerRandom.Next(maximum - minimum + 1);
    }
}
