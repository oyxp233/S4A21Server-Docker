using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryCompoundItemRecipeService
    {
        internal static bool TryCompoundItemRecipe(
            InventoryService inventory,
            CompoundItemRecipeRequest request,
            out CompoundItemRecipeResult result)
        {
            result = new CompoundItemRecipeResult
            {
                RequestedCount = request != null ? request.RequestedCount : (ushort)0,
            };

            if (inventory == null || request == null || request.RequestedCount == 0)
                return Fail(result, 17);

            if (!TryResolveSource(inventory, request, result, out var sourceItemId, out var sourceSlotIndex, out var source))
                return false;

            result.SourceItemTemplateId = sourceItemId;
            if (!TryParseCompoundRecipe(sourceItemId, out var recipe))
                return Fail(result, 17);

            result.PvfPath = recipe.PvfPath;
            result.RecipeType = recipe.RecipeType;

            if (!TryMultiplyRecipeEntries(
                    recipe.Materials,
                    request.RequestedCount,
                    out var materials)
                || !TryMultiplyRecipeEntries(
                    recipe.Outputs,
                    request.OutputCount ?? request.RequestedCount,
                    out var outputs))
            {
                return Fail(result, 17);
            }
            if ((request.OutputCount ?? request.RequestedCount) > 0 && outputs.Count == 0)
                return Fail(result, 17);
            if (!HasEnoughMaterials(inventory, materials))
                return Fail(result, 21);

            var rewardRequests = BuildRewardRequests(outputs);
            var totalGoldCost = (long)recipe.GoldCost * request.RequestedCount;
            if (totalGoldCost < 0 || totalGoldCost > int.MaxValue)
                return Fail(result, 17);
            var goldCost = (int)totalGoldCost;

            var planningInventory = InventoryCompoundPlanning.CloneInventory(inventory);
            if (!DeleteMaterials(planningInventory, materials, null))
                return Fail(result, 17);
            if (!request.SourceIsItemId
                && (!InventoryDeleteService.TryConsumeFromSlot(
                        planningInventory,
                        InventoryListType.Main,
                        sourceSlotIndex,
                        source.ItemId,
                        request.RequestedCount,
                        out var planningSourceDelete)
                    || !planningSourceDelete.Success))
            {
                return Fail(result, 17);
            }

            InventoryRewardGrantBatchPlan plan = null;
            if (rewardRequests.Count > 0
                && (!InventoryRewardGrantService.TryPlanBatch(
                        planningInventory,
                        rewardRequests,
                        out plan)
                    || plan == null
                    || !plan.Success))
            {
                return Fail(result, 4);
            }

            var deleted = new List<CompoundItemDeletedEntry>();
            if (goldCost > 0)
            {
                if (!inventory.TryConsumeMainItem(0, goldCost, out var goldConsume) || !goldConsume.Success)
                    return Fail(result, 22);
                result.GoldSpent = goldCost;
                result.UpdatedGold = goldConsume.RemainingCount;
            }

            if (!DeleteMaterials(inventory, materials, deleted))
                return Fail(result, 17);
            if (!request.SourceIsItemId)
            {
                if (!InventoryDeleteService.TryConsumeFromSlot(
                        inventory,
                        InventoryListType.Main,
                        sourceSlotIndex,
                        source.ItemId,
                        request.RequestedCount,
                        out var sourceDelete)
                    || !sourceDelete.Success)
                {
                    return Fail(result, 17);
                }

                deleted.Insert(0, new CompoundItemDeletedEntry
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = sourceSlotIndex,
                    Count = sourceDelete.DeletedCount,
                    ItemTemplateId = source.ItemId,
                });
                result.SourceConsumed = true;
            }

            InventoryRewardGrantBatchResult grantBatch = null;
            if (plan != null
                && (!InventoryRewardGrantService.TryApplyPreparedBatch(
                        inventory,
                        plan,
                        out grantBatch)
                    || grantBatch == null
                    || !grantBatch.Success))
            {
                return Fail(result, 4);
            }

            result.DeletedEntries.AddRange(deleted);
            if (grantBatch != null)
                AddRewardResults(inventory, grantBatch.Results, result.Rewards);
            result.ErrorCode = 0;
            return true;
        }

        private static bool TryResolveSource(
            InventoryService inventory,
            CompoundItemRecipeRequest request,
            CompoundItemRecipeResult result,
            out int sourceItemId,
            out short sourceSlotIndex,
            out ItemCore source)
        {
            sourceItemId = request.SourceValue;
            sourceSlotIndex = -1;
            source = null;
            if (request.SourceIsItemId)
                return sourceItemId > 0 || Fail(result, 17);

            if (request.SourceValue < short.MinValue || request.SourceValue > short.MaxValue)
                return Fail(result, 17);

            source = inventory.GetItem(InventoryListType.Main, (short)request.SourceValue);
            if (source == null)
                return Fail(result, 17);

            sourceItemId = source.ItemId;
            sourceSlotIndex = (short)request.SourceValue;
            result.SourceSlotIndex = sourceSlotIndex;
            if (InventoryStackRuleService.IsStackable(source) && source.Count < request.RequestedCount)
                return Fail(result, 17);

            return true;
        }

        internal static bool TryParseCompoundRecipe(int itemTemplateId, out CompoundItemRecipeDefinition recipe)
        {
            recipe = null;
            if (!ItemMetadataResolver.TryLoadStackableFile(itemTemplateId, out StackableItemFile stackable)
                || stackable == null)
                return false;

            var stackableType = NormalizeRecipeTag(stackable.StackableType);
            if (!stackableType.Equals("[recipe]", StringComparison.OrdinalIgnoreCase))
                return false;

            var values = ParseRecipeIntList(stackable.IntData);
            var materials = new List<CompoundItemRecipeEntry>();
            var outputs = new List<CompoundItemRecipeEntry>();
            var goldCost = 0;

            if (!TryParseEncodedRecipe(values, out materials, out outputs)
                || outputs.Count == 0)
            {
                materials = ParseInputOutputEntries(stackable.InputItem);
                outputs = ParseInputOutputEntries(stackable.OutputItem);
                goldCost = ParseGoldCostFromInputItem(stackable.InputItem);
                if (materials.Count == 0 || outputs.Count == 0)
                    return false;
            }

            var entry = ItemMetadataResolver.GetStackableEntry(itemTemplateId);
            recipe = new CompoundItemRecipeDefinition
            {
                PvfPath = entry?.FilePath ?? string.Empty,
                RecipeType = ResolveRecipeType(stackable),
                Materials = materials,
                Outputs = outputs,
                GoldCost = goldCost,
            };
            return true;
        }

        private static bool TryMultiplyRecipeEntries(
            IReadOnlyList<CompoundItemRecipeEntry> entries,
            ushort requestedCount,
            out List<CompoundItemRecipeEntry> result)
        {
            result = new List<CompoundItemRecipeEntry>();
            var merged = new Dictionary<int, long>();
            if (entries == null)
                return true;

            foreach (var entry in entries)
            {
                var count = (long)entry.Count * requestedCount;
                if (entry.ItemTemplateId <= 0 || count <= 0)
                    continue;

                var total = (merged.TryGetValue(entry.ItemTemplateId, out var current)
                    ? current
                    : 0L) + count;
                if (total > int.MaxValue)
                    return false;
                merged[entry.ItemTemplateId] = total;
            }

            result = merged
                .OrderBy(pair => pair.Key)
                .Select(pair => new CompoundItemRecipeEntry(pair.Key, (int)pair.Value))
                .ToList();
            return true;
        }

        private static bool HasEnoughMaterials(
            InventoryService inventory,
            IReadOnlyList<CompoundItemRecipeEntry> materials)
        {
            return InventoryMaterialConsumptionService.HasEnough(
                inventory,
                BuildMaterialRequirements(materials));
        }

        private static bool DeleteMaterials(
            InventoryService inventory,
            IReadOnlyList<CompoundItemRecipeEntry> materials,
            List<CompoundItemDeletedEntry> deleted)
        {
            var consumed = new List<InventoryMaterialConsumptionEntry>();
            if (!InventoryMaterialConsumptionService.TryConsume(
                    inventory,
                    BuildMaterialRequirements(materials),
                    consumed))
            {
                return false;
            }

            if (deleted != null)
            {
                foreach (var entry in consumed)
                {
                    deleted.Add(new CompoundItemDeletedEntry
                    {
                        ListType = InventoryListType.Main,
                        SlotIndex = entry.SlotIndex,
                        Count = entry.Count,
                        ItemTemplateId = entry.ItemTemplateId,
                    });
                }
            }
            return true;
        }

        private static bool TryParseEncodedRecipe(
            IReadOnlyList<int> values,
            out List<CompoundItemRecipeEntry> materials,
            out List<CompoundItemRecipeEntry> outputs)
        {
            materials = new List<CompoundItemRecipeEntry>();
            outputs = new List<CompoundItemRecipeEntry>();
            if (values == null || values.Count == 0)
                return false;

            var position = 0;
            var materialCount = values[position++];
            if (materialCount < 0 || values.Count < position + materialCount * 2)
                return false;

            for (var index = 0; index < materialCount; index++)
                materials.Add(new CompoundItemRecipeEntry(values[position++], values[position++]));

            if (position >= values.Count)
                return true;

            var outputCount = values[position++];
            if (outputCount < 0 || values.Count < position + outputCount * 2)
                return false;

            for (var index = 0; index < outputCount; index++)
                outputs.Add(new CompoundItemRecipeEntry(values[position++], values[position++]));
            return true;
        }

        private static List<InventoryMaterialRequirement> BuildMaterialRequirements(
            IReadOnlyList<CompoundItemRecipeEntry> materials)
        {
            return materials
                .Select(material => new InventoryMaterialRequirement(
                    material.ItemTemplateId,
                    material.Count))
                .ToList();
        }

        private static List<InventoryRewardGrantRequest> BuildRewardRequests(
            IReadOnlyList<CompoundItemRecipeEntry> outputs)
        {
            var requests = new List<InventoryRewardGrantRequest>();
            foreach (var output in outputs)
                requests.Add(InventoryRewardGrantRequest.Create(
                    output.ItemTemplateId,
                    output.Count,
                    ItemCreateReason.Unknown));
            return requests;
        }

        private static void AddRewardResults(
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantResult> rewards,
            List<BoosterRewardResult> target)
        {
            if (rewards == null || target == null)
                return;

            foreach (var reward in rewards)
            {
                if (reward == null || !reward.Success)
                    continue;

                var core = reward.SlotIndex >= 0
                    ? inventory.GetItem(reward.ListType, reward.SlotIndex)
                    : reward.Core;
                var stackCount = ResolveFinalCount(core, reward);
                var itemTemplateId = reward.ItemTemplateId > 0
                    ? reward.ItemTemplateId
                    : core?.ItemId ?? 0;
                target.Add(new BoosterRewardResult
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = itemTemplateId,
                    GrantedCount = reward.GrantedCount,
                    StackCount = stackCount,
                });
            }
        }

        private static int ResolveFinalCount(ItemCore core, InventoryRewardGrantResult reward)
        {
            if (reward.Kind == InventoryRewardGrantKind.MainVirtualCount)
                return reward.FinalCount;
            if (core == null)
                return Math.Max(1, reward.GrantedCount);
            return InventoryStackRuleService.IsStackable(core)
                ? core.Count
                : Math.Max(1, reward.GrantedCount);
        }

        private static List<int> ParseRecipeIntList(string text)
        {
            var values = new List<int>();
            if (string.IsNullOrWhiteSpace(text))
                return values;

            foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }

            return values;
        }

        private static string ResolveRecipeType(StackableItemFile stackable)
        {
            if (stackable?.StringDataItems != null && stackable.StringDataItems.Count > 0)
                return string.Join(",", stackable.StringDataItems.Select(NormalizeRecipeTag));

            return NormalizeRecipeTag(stackable?.StringData);
        }

        private static string NormalizeRecipeTag(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var trimmed = text.Trim();
            var first = trimmed.IndexOf('`');
            if (first >= 0)
            {
                var second = trimmed.IndexOf('`', first + 1);
                if (second > first)
                    return trimmed.Substring(first + 1, second - first - 1).Trim();
            }

            return trimmed.Replace("`", string.Empty).Trim();
        }

        private static bool Fail(CompoundItemRecipeResult result, byte errorCode)
        {
            if (result != null)
                result.ErrorCode = errorCode;
            return false;
        }

        private static List<CompoundItemRecipeEntry> ParseInputOutputEntries(string text)
        {
            var entries = new List<CompoundItemRecipeEntry>();
            if (string.IsNullOrWhiteSpace(text))
                return entries;

            var values = ParseRecipeIntList(text);
            for (var i = 0; i + 1 < values.Count; i += 2)
            {
                var itemId = values[i];
                var count = values[i + 1];
                if (itemId > 0 && count > 0)
                    entries.Add(new CompoundItemRecipeEntry(itemId, count));
            }

            return entries;
        }

        private static int ParseGoldCostFromInputItem(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            var values = ParseRecipeIntList(text);
            for (var i = 0; i + 1 < values.Count; i += 2)
            {
                if (values[i] == 0)
                    return Math.Max(0, values[i + 1]);
            }

            return 0;
        }
    }
}
