using System;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Game.ExpertJob
{
    internal static class EnchanterStoreUseService
    {
        internal const byte ErrorInvalidItem = 13;
        internal const byte ErrorInvalidState = 19;
        internal const byte ErrorInsufficientGold = 21;
        internal const byte ErrorNoEndurance = 189;

        internal static bool TryEnchant(
            InventoryService requesterInventory,
            InventoryService ownerInventory,
            ExpertJobStoreSession store,
            EnchanterStoreUseCommand command,
            uint currentExperience,
            int ownerGoldCarryLimit,
            out EnchanterStoreUseResult result)
        {
            result = new EnchanterStoreUseResult { ErrorCode = ErrorInvalidItem };
            if (requesterInventory == null
                || ownerInventory == null
                || store?.Enchanter == null
                || store.Kind != ExpertJobStoreKind.EnchantShop
                || command == null
                || command.OwnerUserId != store.OwnerUserId
                || command.Mode != 2
                || command.TargetListType != InventoryListType.Main
                || command.CardListType != InventoryListType.Main
                || command.TargetSlotIndex < 0
                || command.CardSlotIndex < 0
                || command.TargetSlotIndex == command.CardSlotIndex)
            {
                return false;
            }

            var config = EnchanterConfigProvider.Config;
            var ownerLevel = config.GetLevel(currentExperience);
            var enduranceReduction = ownerLevel >= config.EnduranceReductionMinimumLevel
                ? config.EnduranceReduction
                : 0;
            if (!config.CardRecipesByItemId.TryGetValue(command.RecipeItemId, out var recipe)
                || recipe.RequiredLevel > ownerLevel
                || !ContainsQualification(store.Enchanter.CardQualificationLevels, recipe.Qualification)
                || !config.CardExperienceRulesByLevel.TryGetValue(ownerLevel, out var experienceRule)
                || recipe.Qualification < 0
                || recipe.Qualification >= experienceRule.SuccessRates.Length)
            {
                return false;
            }

            if (store.Enchanter.Endurance < enduranceReduction)
            {
                result.ErrorCode = ErrorNoEndurance;
                return false;
            }

            var card = requesterInventory.GetItem(command.CardListType, command.CardSlotIndex);
            var target = requesterInventory.GetItem(command.TargetListType, command.TargetSlotIndex);
            if (card == null
                || card.Count <= 0
                || !config.CardsByItemId.TryGetValue(card.ItemId, out var cardDefinition)
                || cardDefinition.Qualification != recipe.Qualification
                || target == null
                || !target.IsEquipmentItem()
                || IsItemLocked(requesterInventory, target)
                || !ItemMetadataResolver.TryValidateMonsterCardTarget(
                    card.ItemId, target.ItemId, card.EnchantUpgradeCount, out _))
            {
                return false;
            }

            var selfService = requesterInventory.CharacterId == ownerInventory.CharacterId;
            var requesterGold = GetGold(requesterInventory);
            var ownerGold = GetGold(ownerInventory);
            var originalRequesterGold = requesterGold;
            var originalOwnerGold = ownerGold;
            if (!selfService && requesterGold < store.Cost)
            {
                result.ErrorCode = ErrorInsufficientGold;
                return false;
            }
            if (!selfService && (long)ownerGold + store.Cost > ownerGoldCarryLimit)
            {
                result.ErrorCode = ErrorInsufficientGold;
                return false;
            }

            var originalTarget = target.Copy();
            var originalCard = card.Copy();
            var originalEndurance = store.Enchanter.Endurance;
            try
            {
                var success = ServerRandom.Next(100)
                    < experienceRule.SuccessRates[recipe.Qualification];
                if (success)
                {
                    var updatedTarget = target.Copy();
                    updatedTarget.EnchantCardId = card.ItemId;
                    updatedTarget.EnchantUpgradeCount = card.EnchantUpgradeCount;
                    if (!requesterInventory.SetItem(
                            command.TargetListType, command.TargetSlotIndex, updatedTarget))
                    {
                        throw new InvalidOperationException(
                            "enchanter target mutation failed after validation");
                    }
                }

                if (!InventoryDeleteService.TryUseStackableForClient(
                        requesterInventory,
                        command.CardListType,
                        command.CardSlotIndex,
                        card.ItemId,
                        out _))
                {
                    throw new InvalidOperationException(
                        "enchanter card mutation failed after validation");
                }

                if (!selfService)
                {
                    requesterGold -= store.Cost;
                    ownerGold += store.Cost;
                    if (!requesterInventory.SetMainVirtualCount(
                            InventoryService.MainVirtualCurrencySlotStart, requesterGold)
                        || !ownerInventory.SetMainVirtualCount(
                            InventoryService.MainVirtualCurrencySlotStart, ownerGold))
                    {
                        throw new InvalidOperationException(
                            "enchanter gold mutation failed after validation");
                    }
                }

                store.Enchanter.Endurance = Math.Max(
                    0, store.Enchanter.Endurance - enduranceReduction);
                var experienceGain = success
                    ? NextInclusive(
                        experienceRule.MinimumExperienceGain,
                        experienceRule.MaximumExperienceGain)
                    : 0;
                var finalExperience = (uint)Math.Min(
                    uint.MaxValue, (ulong)currentExperience + (uint)experienceGain);
                result = new EnchanterStoreUseResult
                {
                    ErrorCode = 0,
                    EnchantSucceeded = success,
                    TargetListType = command.TargetListType,
                    TargetSlotIndex = command.TargetSlotIndex,
                    CardListType = command.CardListType,
                    CardSlotIndex = command.CardSlotIndex,
                    RequesterGold = requesterGold,
                    OwnerGold = ownerGold,
                    Endurance = store.Enchanter.Endurance,
                    ExperienceGain = experienceGain,
                    FinalExperience = finalExperience,
                };
                return true;
            }
            catch
            {
                requesterInventory.SetItem(
                    command.TargetListType, command.TargetSlotIndex, originalTarget);
                requesterInventory.SetItem(
                    command.CardListType, command.CardSlotIndex, originalCard);
                requesterInventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    originalRequesterGold);
                if (!selfService)
                {
                    ownerInventory.SetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart,
                        originalOwnerGold);
                }
                store.Enchanter.Endurance = originalEndurance;
                throw;
            }
        }

        private static bool ContainsQualification(
            System.Collections.Generic.IReadOnlyList<byte> qualifications,
            int qualification)
        {
            if (qualifications == null || qualification < 0 || qualification > byte.MaxValue)
                return false;
            foreach (var value in qualifications)
            {
                if (value == qualification)
                    return true;
            }
            return false;
        }

        private static bool IsItemLocked(InventoryService inventory, ItemCore core)
        {
            return core.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        private static int GetGold(InventoryService inventory)
            => inventory.GetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;

        private static int NextInclusive(int minimum, int maximum)
            => maximum <= minimum ? minimum : minimum + ServerRandom.Next(maximum - minimum + 1);
    }
}
