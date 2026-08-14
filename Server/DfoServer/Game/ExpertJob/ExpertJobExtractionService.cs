using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Game.ExpertJob
{
    internal static class ExpertJobExtractionService
    {
        internal const byte ErrorInventoryFull = 4;
        internal const byte ErrorInvalidItem = 13;
        internal const byte ErrorInvalidState = 19;

        internal static bool TryExtract(
            InventoryService inventory,
            ExpertJobExtractionCommand command,
            uint currentExperience,
            IExpertJobExtractionConfig config,
            out ExpertJobExtractionResult result)
        {
            result = new ExpertJobExtractionResult { ErrorCode = ErrorInvalidItem };
            if (inventory == null
                || command == null
                || config == null
                || command.ExtractorType != config.ExpertJobType
                || command.ExtractorSlotIndex < 0
                || command.TargetListType != InventoryListType.Main
                || command.TargetSlotIndex < 0
                || command.ExtractorSlotIndex == command.TargetSlotIndex)
                return false;

            var extractor = inventory.GetItem(InventoryListType.Main, command.ExtractorSlotIndex);
            var target = inventory.GetItem(command.TargetListType, command.TargetSlotIndex);
            if (extractor == null
                || target == null
                || !config.Extractors.TryGetValue(extractor.ItemId, out var extractorDefinition)
                || config.RecipeConfig.GetLevel(currentExperience)
                    < extractorDefinition.RequiredExpertJobLevel
                || !target.IsEquipmentItem())
                return false;

            var metadata = ItemMetadataResolver.Resolve(target.ItemId);
            if (metadata == null
                || !config.ExtractionRules.TryGetValue(
                    (extractor.ItemId, metadata.Rarity, ExpertJobEquipmentStateResolver.Resolve(target)),
                    out var rule))
                return false;

            var request = new DisjointItemRequest
            {
                ItemSpace = InventoryListType.Main,
                TargetSlotIndex = command.TargetSlotIndex,
                DisjointItemSlotIndex = -1,
            };
            if (!InventoryDisjointService.TryDisjointItem(
                    inventory,
                    request,
                    (ItemCore _, ItemMetadata sourceMetadata, out List<DisjointMaterialResult> materials, out byte errorCode) =>
                    {
                        materials = CalculateMaterials(sourceMetadata, rule, config);
                        errorCode = materials.Count > 0 ? (byte)0 : ErrorInvalidItem;
                        return materials.Count > 0;
                    },
                    out var disjointResult))
            {
                result.ErrorCode = disjointResult?.ErrorCode == DisjointItemResult.ErrorInventoryFull
                    ? ErrorInventoryFull
                    : ErrorInvalidItem;
                return false;
            }

            var experienceGain = extractorDefinition.MaximumExperienceGain
                    <= extractorDefinition.MinimumExperienceGain
                ? extractorDefinition.MinimumExperienceGain
                : extractorDefinition.MinimumExperienceGain + ServerRandom.Next(
                    extractorDefinition.MaximumExperienceGain
                    - extractorDefinition.MinimumExperienceGain + 1);
            var finalExperience = (uint)Math.Min(
                uint.MaxValue,
                (ulong)currentExperience + (uint)experienceGain);
            result = new ExpertJobExtractionResult
            {
                ErrorCode = 0,
                TargetListType = command.TargetListType,
                TargetSlotIndex = command.TargetSlotIndex,
                ExperienceGain = experienceGain,
                FinalExperience = finalExperience,
            };
            foreach (var material in disjointResult.Materials)
            {
                result.Materials.Add(new ExpertJobExtractionMaterial
                {
                    SlotIndex = material.SlotIndex,
                    ItemTemplateId = material.ItemTemplateId,
                    Count = material.Count,
                });
            }
            result.InventoryMutations.AddRange(disjointResult.InventoryMutations);
            result.LearnedRecipeIds.AddRange(config.RecipeConfig.GetNewAutoLearnRecipeIds(
                currentExperience,
                finalExperience));
            return true;
        }

        private static List<DisjointMaterialResult> CalculateMaterials(
            ItemMetadata metadata,
            ExpertJobExtractionRule rule,
            IExpertJobExtractionConfig config)
        {
            var result = new List<DisjointMaterialResult>();
            Add(result, rule.ResultItemId, config.CalculateBaseMaterialCount(metadata, rule));

            var bigWin = ServerRandom.Next(10000)
                < Math.Max(0, Math.Min(100, rule.BigWinChancePercent)) * 100;
            var table = bigWin ? rule.BigWinTable : rule.AdditionalTable;
            var selections = bigWin ? config.BigWinResults : config.AdditionalResults;
            if (selections.TryGetValue(table, out var rows))
            {
                var selected = ExpertJobSelectionRuleSelector.Select(rows, metadata.Grade);
                if (selected != null)
                {
                    var count = selected.QuantityMultiplier > 0
                        ? (int)Math.Floor(metadata.Grade * selected.QuantityMultiplier)
                        : 1;
                    Add(result, selected.ItemId, Math.Max(1, count));
                }
            }
            return result;
        }

        private static void Add(List<DisjointMaterialResult> target, int itemId, int count)
        {
            if (itemId <= 0 || count <= 0)
                return;
            foreach (var item in target)
            {
                if (item.ItemTemplateId != itemId)
                    continue;
                item.Count += count;
                return;
            }
            target.Add(new DisjointMaterialResult
            {
                ItemTemplateId = itemId,
                Count = count,
                SlotIndex = -1,
            });
        }
    }
}
