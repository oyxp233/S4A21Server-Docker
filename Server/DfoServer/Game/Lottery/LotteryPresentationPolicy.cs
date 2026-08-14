using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Lottery
{
    public static class LotteryPresentationPolicy
    {
        public static bool ShouldSendGoldRefresh(LotteryOpenResult result)
            => result != null && result.ConsumedGold > 0;

        internal static ItemCore ResolveResultCore(
            InventoryService inventory,
            LotteryRewardGrant reward)
        {
            if (inventory == null
                || reward == null
                || reward.ItemTemplateId <= 0
                || (reward.ListType != InventoryListType.Main
                    && reward.ListType != InventoryListType.Avatar))
            {
                return null;
            }

            if (reward.DisplayCore != null
                && reward.DisplayCore.ItemId == reward.ItemTemplateId)
                return reward.DisplayCore;

            if (reward.ListType == InventoryListType.Main
                && InventoryService.IsVirtualMainSlot(reward.SlotIndex))
            {
                var virtualItem = inventory.GetMainVirtualCount(reward.SlotIndex);
                if (virtualItem == null || virtualItem.ItemId != reward.ItemTemplateId)
                    return null;

                var core = ItemCore.Create(ItemCore.KindConsumable, virtualItem.ItemId);
                core.Count = virtualItem.Count;
                return core;
            }

            var item = inventory.GetItem(reward.ListType, reward.SlotIndex);
            return item != null && item.ItemId == reward.ItemTemplateId
                ? item
                : null;
        }

        public static IReadOnlyList<LotteryRewardGrant> ResolveDisplayRewards(
            IReadOnlyList<LotteryRewardGrant> rewards)
        {
            if (rewards == null)
                return Array.Empty<LotteryRewardGrant>();

            return rewards
                .Where(reward => reward != null
                    && reward.ItemTemplateId > 0
                    && (reward.ListType == InventoryListType.Main
                        || reward.ListType == InventoryListType.Avatar))
                .ToList();
        }

        public static bool ShouldUseDoubleRewardResultFlow(
            bool useDoubleReward,
            IReadOnlyList<LotteryRewardGrant> displayRewards)
        {
            return useDoubleReward
                && displayRewards != null
                && displayRewards.Count > 1;
        }

        public static int ResolveNativeDisplayValue(
            int resolvedDisplayValue,
            bool useDoubleRewardResultFlow)
        {
            return useDoubleRewardResultFlow
                ? (resolvedDisplayValue > 0 ? 2 : 0)
                : resolvedDisplayValue;
        }

        internal static int ResolveDisplayValue(
            ItemCore item,
            LotteryRewardGrant reward,
            IReadOnlyList<LotteryRewardGrant> sameOpenRewards = null)
        {
            if (item == null)
                return 0;

            var fallback = Math.Max(1, reward?.GrantedCount ?? 1);
            if (reward == null || sameOpenRewards == null || sameOpenRewards.Count == 0)
                return fallback;

            var total = sameOpenRewards
                .Where(candidate => candidate != null
                    && candidate.ListType == reward.ListType
                    && candidate.ItemTemplateId == reward.ItemTemplateId)
                .Sum(candidate => Math.Max(1, candidate.GrantedCount));
            return Math.Max(fallback, total);
        }

        public static IReadOnlyList<LotteryRewardGrant> ResolvePostResultMainRefreshRewards(
            LotteryRewardGrant displayReward,
            IReadOnlyList<LotteryRewardGrant> mainRewards,
            bool useDoubleRewardResultFlow)
        {
            if (mainRewards == null || mainRewards.Count == 0)
                return Array.Empty<LotteryRewardGrant>();

            if (displayReward == null || displayReward.ListType != InventoryListType.Main)
                return mainRewards.ToList();

            return useDoubleRewardResultFlow
                ? mainRewards.Skip(1).ToList()
                : ResolveMainRefreshRewards(mainRewards);
        }

        public static IReadOnlyList<LotteryRewardGrant> ResolveMainRefreshRewards(
            IReadOnlyList<LotteryRewardGrant> mainRewards)
        {
            if (mainRewards == null || mainRewards.Count <= 1)
                return Array.Empty<LotteryRewardGrant>();

            var duplicateNonStackableKeys = new HashSet<string>(mainRewards
                .Where(reward => reward != null && reward.ItemTemplateId > 0)
                .GroupBy(RewardKey)
                .Where(group => !ItemMetadataResolver.Resolve(group.First().ItemTemplateId).IsStackable
                    && group.Sum(reward => Math.Max(1, reward.GrantedCount)) > 1)
                .Select(group => group.Key));
            if (duplicateNonStackableKeys.Count == 0)
                return mainRewards.Skip(1).ToList();

            return mainRewards
                .Where(reward => reward != null && duplicateNonStackableKeys.Contains(RewardKey(reward)))
                .ToList();
        }

        public static bool ShouldSuppressNotice(
            LotteryRewardGrant reward,
            IReadOnlyList<LotteryRewardGrant> sameOpenRewards)
        {
            if (reward == null || sameOpenRewards == null || reward.ItemTemplateId <= 0)
                return false;
            if (ItemMetadataResolver.Resolve(reward.ItemTemplateId).IsStackable)
                return false;

            return sameOpenRewards
                .Where(candidate => candidate != null
                    && candidate.ListType == reward.ListType
                    && candidate.ItemTemplateId == reward.ItemTemplateId)
                .Sum(candidate => Math.Max(1, candidate.GrantedCount)) > 1;
        }

        public static bool IsNoticeEligible(ItemMetadata metadata)
        {
            return metadata != null
                && !metadata.IsStackable
                && (metadata.Rarity >= 3
                    || string.Equals(metadata.ItemCategory, "legacy", StringComparison.OrdinalIgnoreCase));
        }

        private static string RewardKey(LotteryRewardGrant reward)
            => $"{(byte)reward.ListType}:0x{reward.ItemTemplateId:X8}";
    }
}
