using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class ExpertJobRecipeLearningResult
    {
        internal bool Handled { get; set; }
        internal bool Success { get; set; }
        internal byte ErrorCode { get; set; }
        internal int RecipeId { get; set; }
        internal int RemainingCount { get; set; }
    }

    internal static class ExpertJobRecipeLearningService
    {
        internal const byte ErrorRequirementsNotMet = 13;
        internal const byte ErrorLevelTooLow = 14;

        internal static ExpertJobRecipeLearningResult TryLearn(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemId,
            uint experience,
            ExpertJobState state,
            ExpertJobRecipeConfig config)
        {
            var result = new ExpertJobRecipeLearningResult();
            if (config == null
                || !config.RecipesByItemId.TryGetValue(expectedItemId, out var recipe))
                return result;

            result.Handled = true;
            result.RecipeId = recipe.RecipeItemId;
            if (inventory == null
                || state == null
                || listType != InventoryListType.Main)
            {
                result.ErrorCode = ErrorRequirementsNotMet;
                return result;
            }
            if (!config.CanLearnRecipe(experience, recipe))
            {
                result.ErrorCode = ErrorLevelTooLow;
                return result;
            }
            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    listType,
                    slotIndex,
                    expectedItemId,
                    1,
                    out var consumed))
            {
                result.ErrorCode = ErrorRequirementsNotMet;
                return result;
            }

            if (!state.LearnedRecipeIds.Contains(recipe.RecipeItemId))
            {
                state.LearnedRecipeIds.Add(recipe.RecipeItemId);
                state.LearnedRecipeIds.Sort();
            }
            result.Success = true;
            result.RemainingCount = consumed.RemainingCount;
            return result;
        }
    }
}
