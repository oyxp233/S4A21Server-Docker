using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryEmblemCompoundService
    {
        internal static bool TryCompoundEmblems(
            InventoryService inventory,
            EmblemCompoundRequest request,
            out EmblemCompoundResult result)
        {
            result = Error(EmblemCompoundResult.ErrorInvalidRequest);
            if (inventory == null || request?.Inputs == null || request.Inputs.Count < 2 || request.Inputs.Count > 5)
                return false;

            var consumedBySlot = request.Inputs
                .GroupBy(input => input.SlotIndex)
                .ToDictionary(group => group.Key, group => group.ToList());
            var sources = new Dictionary<short, ItemCore>();
            var grades = new List<int>();

            foreach (var pair in consumedBySlot)
            {
                var source = inventory.GetItem(InventoryListType.Main, pair.Key);
                if (source == null
                    || !InventoryStackRuleService.IsStackable(source)
                    || source.Count < pair.Value.Count)
                    return false;

                foreach (var input in pair.Value)
                {
                    if (input.ItemTemplateId != source.ItemId)
                        return false;
                }

                var metadata = ItemMetadataResolver.Resolve(source.ItemId);
                if (metadata == null
                    || !metadata.IsStackable
                    || !IsAvatarEmblem(metadata.StackableType)
                    || metadata.Grade <= 0)
                    return false;

                sources[pair.Key] = source;
                for (var index = 0; index < pair.Value.Count; index++)
                    grades.Add(metadata.Grade);
            }

            var compoundGrade = grades.Min();
            if (!EmblemCompoundConfigProvider.TryRollReward(
                    compoundGrade,
                    request.Inputs.Count,
                    out var boosterItemTemplateId,
                    out var rewardItemTemplateId,
                    out var rewardCount))
            {
                FileLogger.Log($"[EmblemCompound] no PVF mapping grade={compoundGrade} count={request.Inputs.Count} grades={string.Join(",", grades)}");
                return false;
            }

            var rewardRequests = new[]
            {
                InventoryRewardGrantRequest.Create(rewardItemTemplateId, rewardCount, ItemCreateReason.Unknown)
            };
            var planningInventory = InventoryCompoundPlanning.CloneInventory(inventory);
            if (!ConsumeEmblemInputs(planningInventory, consumedBySlot, sources))
                return false;
            if (!InventoryRewardGrantService.TryPlanBatch(planningInventory, rewardRequests, out var plan)
                || plan == null
                || !plan.Success)
            {
                result = Error(EmblemCompoundResult.ErrorInventoryFull);
                return false;
            }

            if (!ConsumeEmblemInputs(inventory, consumedBySlot, sources))
                return false;
            if (!InventoryRewardGrantService.TryApplyPreparedBatch(inventory, plan, out var grantBatch)
                || grantBatch == null
                || !grantBatch.Success
                || grantBatch.Results.Count == 0
                || !grantBatch.Results[0].Success)
            {
                result = Error(EmblemCompoundResult.ErrorInventoryFull);
                return false;
            }

            var reward = grantBatch.Results[0];
            result = new EmblemCompoundResult
            {
                ErrorCode = 0,
                RewardItemTemplateId = rewardItemTemplateId,
                RewardSlotIndex = reward.SlotIndex,
                RewardGrantedCount = rewardCount,
                RewardStackCount = ResolveFinalStackCount(inventory, reward),
                PvfBoosterItemTemplateId = boosterItemTemplateId,
            };
            foreach (var slot in consumedBySlot.Keys.OrderBy(slot => slot))
                result.ChangedSlots.Add(slot);
            if (!result.ChangedSlots.Contains(reward.SlotIndex))
                result.ChangedSlots.Add(reward.SlotIndex);
            return true;
        }

        private static bool ConsumeEmblemInputs(
            InventoryService inventory,
            IReadOnlyDictionary<short, List<EmblemCompoundInput>> consumedBySlot,
            IReadOnlyDictionary<short, ItemCore> sources)
        {
            foreach (var pair in consumedBySlot.OrderBy(pair => pair.Key))
            {
                if (!sources.TryGetValue(pair.Key, out var source)
                    || !InventoryDeleteService.TryConsumeFromSlot(
                        inventory,
                        InventoryListType.Main,
                        pair.Key,
                        source.ItemId,
                        pair.Value.Count,
                        out var delete)
                    || !delete.Success)
                {
                    return false;
                }
            }

            return true;
        }

        private static int ResolveFinalStackCount(InventoryService inventory, InventoryRewardGrantResult reward)
        {
            if (reward == null)
                return 0;
            if (reward.Kind == InventoryRewardGrantKind.MainVirtualCount)
                return reward.FinalCount;

            var item = inventory?.GetItem(reward.ListType, reward.SlotIndex);
            if (item == null)
                return reward.GrantedCount;

            return InventoryStackRuleService.IsStackable(item)
                ? item.Count
                : Math.Max(1, reward.GrantedCount);
        }

        private static bool IsAvatarEmblem(string stackableType)
        {
            if (string.IsNullOrWhiteSpace(stackableType))
                return false;

            var normalized = stackableType.Replace("`", string.Empty).Trim();
            return normalized.StartsWith("[avatar emblem]", StringComparison.OrdinalIgnoreCase);
        }

        private static EmblemCompoundResult Error(byte code) => new EmblemCompoundResult { ErrorCode = code };
    }
}
