using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    // 自选礼盒([booster selection])选中项 -> 应发物品集合的统一语义。
    // 主发奖路径(definition 分类, 有 body[2..4] 分类索引)与兜底路径(PVF 分组)共用,
    // 保证 selnum=0 发整组、selnum=K>0 只发校验后的选中集, 非法输入整体拒绝。
    internal static class BoosterSelectionGrantResolver
    {
        internal static bool TryResolveGrantedItemIds(
            int boosterSelectionNum,
            IReadOnlyList<IReadOnlyList<int>> categories,
            int categoryIndex,
            IReadOnlyList<int> selectedItemIds,
            out List<int> grantedItemIds)
        {
            grantedItemIds = new List<int>();
            if (categories == null || categories.Count == 0
                || selectedItemIds == null || selectedItemIds.Count == 0)
                return false;

            var seen = new HashSet<int>();
            foreach (var itemId in selectedItemIds)
            {
                if (itemId <= 0 || !seen.Add(itemId))
                    return false;
            }

            // categoryIndex < 0 表示调用方没有分类索引(兜底路径), 由选中 id 反查唯一分类。
            var resolvedCategoryIndex = categoryIndex;
            if (resolvedCategoryIndex >= categories.Count)
                return false;
            if (resolvedCategoryIndex < 0)
                resolvedCategoryIndex = ResolveUniqueCategoryIndex(categories, selectedItemIds);

            if (boosterSelectionNum <= 0)
            {
                if (resolvedCategoryIndex >= 0)
                {
                    foreach (var itemId in selectedItemIds)
                    {
                        if (!CategoryContains(categories[resolvedCategoryIndex], itemId))
                            return false;
                    }

                    return AddDistinct(grantedItemIds, categories[resolvedCategoryIndex]);
                }

                // 分类不唯一(同一物品出现在多个分组): 退化为发放客户端发来的全部合法 id。
                foreach (var itemId in selectedItemIds)
                {
                    if (!AnyCategoryContains(categories, itemId))
                        return false;
                }

                grantedItemIds.AddRange(selectedItemIds);
                return grantedItemIds.Count > 0;
            }

            if (selectedItemIds.Count > boosterSelectionNum)
                return false;

            if (resolvedCategoryIndex >= 0)
            {
                foreach (var itemId in selectedItemIds)
                {
                    if (!CategoryContains(categories[resolvedCategoryIndex], itemId))
                        return false;
                }
            }
            else
            {
                // 无唯一分类(兜底路径): 仅要求每个 id 至少属于某个分类, 且数量已受 selnum 限制。
                foreach (var itemId in selectedItemIds)
                {
                    if (!AnyCategoryContains(categories, itemId))
                        return false;
                }
            }

            grantedItemIds.AddRange(selectedItemIds);
            return grantedItemIds.Count > 0;
        }

        private static int ResolveUniqueCategoryIndex(
            IReadOnlyList<IReadOnlyList<int>> categories,
            IReadOnlyList<int> selectedItemIds)
        {
            var resolved = -1;
            for (var i = 0; i < categories.Count; i++)
            {
                if (!CategoryContainsAll(categories[i], selectedItemIds))
                    continue;

                if (resolved >= 0)
                    return -1;

                resolved = i;
            }

            return resolved;
        }

        private static bool CategoryContainsAll(IReadOnlyList<int> category, IReadOnlyList<int> itemIds)
        {
            foreach (var itemId in itemIds)
            {
                if (!CategoryContains(category, itemId))
                    return false;
            }

            return true;
        }

        private static bool AnyCategoryContains(IReadOnlyList<IReadOnlyList<int>> categories, int itemId)
        {
            for (var i = 0; i < categories.Count; i++)
            {
                if (CategoryContains(categories[i], itemId))
                    return true;
            }

            return false;
        }

        private static bool CategoryContains(IReadOnlyList<int> category, int itemId)
        {
            if (category == null)
                return false;

            for (var i = 0; i < category.Count; i++)
            {
                if (category[i] == itemId)
                    return true;
            }

            return false;
        }

        private static bool AddDistinct(List<int> target, IReadOnlyList<int> source)
        {
            var seen = new HashSet<int>();
            foreach (var itemId in source)
            {
                if (itemId > 0 && seen.Add(itemId))
                    target.Add(itemId);
            }

            return target.Count > 0;
        }
    }
}
