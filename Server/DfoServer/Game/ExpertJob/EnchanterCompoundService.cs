using System;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Game.ExpertJob
{
    internal static class EnchanterCompoundService
    {
        internal const byte ErrorInventoryFull = ExpertJobCompoundService.ErrorInventoryFull;
        internal const byte ErrorLevelTooLow = ExpertJobCompoundService.ErrorLevelTooLow;
        internal const byte ErrorInvalidState = ExpertJobCompoundService.ErrorInvalidState;
        internal const byte ErrorInsufficientMaterials = ExpertJobCompoundService.ErrorInsufficientMaterials;

        internal static bool TryCraftBead(
            InventoryService inventory,
            ExpertJobCompoundCommand command,
            uint currentExperience,
            out ExpertJobCompoundResult result)
        {
            result = new ExpertJobCompoundResult { ErrorCode = ErrorInvalidState };
            if (inventory == null
                || command == null
                || command.RecipeItemId <= 0
                || command.RequestedCount == 0
                || !command.IsCardCraft)
            {
                return false;
            }

            if (!command.IsCardCraft || command.RequestedCount != 1)
                return false;

            var config = EnchanterConfigProvider.Config;
            if (!config.CardRecipesByItemId.TryGetValue(command.RecipeItemId, out var recipe))
                return false;

            var expertJobLevel = config.GetLevel(currentExperience);
            if (expertJobLevel < recipe.RequiredLevel)
            {
                result.ErrorCode = ErrorLevelTooLow;
                return false;
            }
            if (!config.CardExperienceRulesByLevel.TryGetValue(
                    expertJobLevel,
                    out var experienceRule)
                || recipe.Qualification < 0
                || recipe.Qualification >= experienceRule.SuccessRates.Length)
            {
                return false;
            }

            var card = inventory.GetItem(InventoryListType.Main, command.CardSlotIndex);
            if (card == null
                || card.Count <= 0
                || !config.CardsByItemId.TryGetValue(card.ItemId, out var cardDefinition)
                || cardDefinition.Qualification != recipe.Qualification
                || !config.BeadItemIdByCardItemId.TryGetValue(card.ItemId, out var beadItemId))
            {
                return false;
            }
            if (!InventoryCreateService.TryCreateCore(
                    beadItemId,
                    ItemCreateReason.Unknown,
                    1,
                    out var beadCore))
            {
                return false;
            }
            beadCore.EnchantUpgradeCount = card.EnchantUpgradeCount;

            if (!InventoryMaterialConsumptionService.HasEnough(inventory, recipe.Materials))
            {
                result.ErrorCode = ErrorInsufficientMaterials;
                return false;
            }

            var planningInventory = InventoryCompoundPlanning.CloneInventory(inventory);
            if (!InventoryDeleteService.TryUseStackableForClient(
                    planningInventory,
                    InventoryListType.Main,
                    command.CardSlotIndex,
                    card.ItemId,
                    out _)
                || !InventoryMaterialConsumptionService.TryConsume(
                    planningInventory,
                    recipe.Materials,
                    null))
            {
                throw new InvalidOperationException(
                    "enchanter bead planning mutation failed after validation");
            }

            if (!InventoryRewardGrantService.TryPlanBatch(
                    planningInventory,
                    new[]
                    {
                        InventoryRewardGrantRequest.Existing(
                            beadCore,
                            1,
                            ItemCreateReason.Unknown),
                    },
                    out var rewardPlan)
                || rewardPlan == null
                || !rewardPlan.Success)
            {
                result.ErrorCode = ErrorInventoryFull;
                return false;
            }
            result.AttemptedOutputs.Add(new ExpertJobCompoundOutput
            {
                ItemId = beadItemId,
                Count = 1,
            });
            var succeeded = ServerRandom.Next(100)
                < experienceRule.SuccessRates[recipe.Qualification];

            if (!InventoryDeleteService.TryUseStackableForClient(
                    inventory,
                    InventoryListType.Main,
                    command.CardSlotIndex,
                    card.ItemId,
                    out _))
            {
                throw new InvalidOperationException(
                    "enchanter bead card mutation failed after validation");
            }
            result.AddChangedMainSlot(command.CardSlotIndex);

            var consumedMaterials = new System.Collections.Generic.List<InventoryMaterialConsumptionEntry>();
            if (!InventoryMaterialConsumptionService.TryConsume(
                    inventory,
                    recipe.Materials,
                    consumedMaterials))
            {
                throw new InvalidOperationException(
                    "enchanter bead material mutation failed after validation");
            }
            foreach (var material in consumedMaterials)
                result.AddChangedMainSlot(material.SlotIndex);

            if (succeeded)
            {
                if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                        inventory,
                        rewardPlan,
                        out var rewardBatch)
                    || rewardBatch == null
                    || !rewardBatch.Success
                    || rewardBatch.Results.Count != 1
                    || !rewardBatch.Results[0].Success)
                {
                    throw new InvalidOperationException(
                        "enchanter bead reward mutation failed after validation");
                }

                var reward = rewardBatch.Results[0];
                result.Outputs.Add(new ExpertJobCompoundOutput
                {
                    ItemId = beadItemId,
                    Count = reward.GrantedCount,
                });
                result.AddChangedMainSlot(reward.SlotIndex);
                result.SuccessCount = 1;
                result.ExperienceGain = NextInclusive(
                    experienceRule.MinimumExperienceGain,
                    experienceRule.MaximumExperienceGain);
            }
            else
            {
                result.FailureCount = 1;
            }

            ExpertJobCompoundService.CompleteExperience(
                config.RecipeConfig,
                currentExperience,
                result);
            result.ErrorCode = 0;
            return true;
        }

        private static int NextInclusive(int minimum, int maximum)
            => ExpertJobCompoundService.NextInclusive(minimum, maximum);
    }
}
