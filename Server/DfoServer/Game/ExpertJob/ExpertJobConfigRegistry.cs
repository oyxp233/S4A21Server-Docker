namespace DfoServer.Game.ExpertJob
{
    internal static class ExpertJobConfigRegistry
    {
        internal static bool TryGetRecipeConfig(
            int expertJobType,
            out ExpertJobRecipeConfig config)
        {
            switch (expertJobType)
            {
                case ExpertJobStateCodec.EnchanterType:
                    config = EnchanterConfigProvider.Config.RecipeConfig;
                    return true;
                case ExpertJobStateCodec.AlchemistType:
                    config = AlchemistConfigProvider.Config.RecipeConfig;
                    return true;
                case ExpertJobStateCodec.DollControllerType:
                    config = DollControllerConfigProvider.Config.RecipeConfig;
                    return true;
                default:
                    config = null;
                    return false;
            }
        }

        internal static bool TryResolveRecipe(
            int recipeItemId,
            out ExpertJobRecipeConfig config)
        {
            foreach (var candidateType in new[]
                     {
                         ExpertJobStateCodec.EnchanterType,
                         ExpertJobStateCodec.AlchemistType,
                         ExpertJobStateCodec.DollControllerType,
                     })
            {
                if (TryGetRecipeConfig(candidateType, out var candidate)
                    && candidate.RecipesByItemId.ContainsKey(recipeItemId))
                {
                    config = candidate;
                    return true;
                }
            }

            config = null;
            return false;
        }

        internal static bool TryGetExtractionConfig(
            int expertJobType,
            out IExpertJobExtractionConfig config)
        {
            switch (expertJobType)
            {
                case ExpertJobStateCodec.EnchanterType:
                    config = EnchanterConfigProvider.Config;
                    return true;
                case ExpertJobStateCodec.AlchemistType:
                    config = AlchemistConfigProvider.Config;
                    return true;
                case ExpertJobStateCodec.DollControllerType:
                    config = DollControllerConfigProvider.Config;
                    return true;
                default:
                    config = null;
                    return false;
            }
        }
    }
}
