using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryPackageRewardResolver
    {
        private const int MagicHammerMaterialItemTemplateId = 10007367;
        private const int MagicHammerBoxItemTemplateId = 10007368;
        private const int MagicHammerBundleMinItemTemplateId = 10007472;
        private const int MagicHammerBundleMaxItemTemplateId = 10007477;

        internal static string ResolveBoosterItemName(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return null;

            var stackable = StackableItemProvider.Load(itemTemplateId);
            return string.IsNullOrWhiteSpace(stackable?.Name)
                ? null
                : stackable.Name.Trim('`', ' ', '\t', '\r', '\n');
        }

        internal static void ResolveNeedMaterial(
            int sourceItemTemplateId,
            PvfLib.StackableItemFile stackable,
            out int materialItemTemplateId,
            out int materialCountPerUse)
        {
            materialItemTemplateId = 0;
            materialCountPerUse = 0;

            if (stackable?.RandomBoxRemovalItems == null)
                return;

            foreach (var item in stackable.RandomBoxRemovalItems)
            {
                if (item == null || item.ItemId <= 0 || item.Count <= 0 || item.ItemId == sourceItemTemplateId)
                    continue;

                materialItemTemplateId = item.ItemId;
                materialCountPerUse = item.Count;
                return;
            }
        }

        internal static List<PvfLib.BoosterRewardEntry> AggregateRewards(IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            if (rewards == null)
                return new List<PvfLib.BoosterRewardEntry>();

            return rewards
                .Where(reward => reward != null && reward.ItemId > 0 && reward.Count > 0)
                .GroupBy(reward => new { reward.ItemId, reward.UsablePeriodDays })
                .Select(group => new PvfLib.BoosterRewardEntry
                {
                    ItemId = group.Key.ItemId,
                    Count = group.Sum(reward => Math.Max(1, reward.Count)),
                    Weight = 10000,
                    UsablePeriodDays = group.Key.UsablePeriodDays,
                })
                .ToList();
        }

        internal static List<PvfLib.BoosterRewardEntry> NormalizeRewardEntries(IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            if (rewards == null)
                return new List<PvfLib.BoosterRewardEntry>();

            return rewards
                .Where(reward => reward != null && reward.ItemId > 0 && reward.Count > 0)
                .Select(CloneRewardEntry)
                .ToList();
        }

        internal static bool TryResolvePackageRewards(
            int sourceItemTemplateId,
            PvfLib.StackableItemFile stackable,
            string stackableType,
            IReadOnlyList<int> selectedItemTemplateIds,
            string characterJobLabel,
            out List<PvfLib.BoosterRewardEntry> rewards)
        {
            rewards = new List<PvfLib.BoosterRewardEntry>();
            if (stackable == null)
                return false;

            if (stackableType.Equals("[booster]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[cera booster]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[booster random]", StringComparison.OrdinalIgnoreCase))
            {
                if (TryResolveMagicHammerBundleRewards(sourceItemTemplateId, stackable, out rewards))
                    return true;

                rewards = RollBoosterRewards(stackable.BoosterRewards, characterJobLabel);
                return rewards.Count > 0;
            }

            if (stackableType.Equals("[cera package]", StringComparison.OrdinalIgnoreCase))
            {
                rewards = stackable.PackageRewards.ToList();
                return rewards.Count > 0;
            }

            if (stackableType.Equals("[usable cera package]", StringComparison.OrdinalIgnoreCase))
                return TryResolveClientSelectedRewards(stackable, selectedItemTemplateIds, out rewards);

            if (stackableType.Equals("[random upgradable legacy]", StringComparison.OrdinalIgnoreCase))
            {
                rewards = RollBoosterRewards(stackable.RandomBoxRewards, characterJobLabel);
                return rewards.Count > 0;
            }

            if (stackableType.Equals("[booster selection]", StringComparison.OrdinalIgnoreCase))
            {
                if (selectedItemTemplateIds != null
                    && selectedItemTemplateIds.Count > 0
                    && TryResolveClientSelectedRewards(stackable, selectedItemTemplateIds, out rewards))
                    return true;

                if (stackable.BoosterSelectionNum <= 0)
                {
                    rewards = stackable.BoosterSelectionRewards.ToList();
                    return rewards.Count > 0;
                }

                return TryResolveClientSelectedRewards(stackable, selectedItemTemplateIds, out rewards);
            }

            return false;
        }

        internal static bool TryResolveClientSelectedRewards(
            PvfLib.StackableItemFile stackable,
            IReadOnlyList<int> selectedItemTemplateIds,
            out List<PvfLib.BoosterRewardEntry> rewards)
        {
            rewards = new List<PvfLib.BoosterRewardEntry>();
            if (stackable == null || selectedItemTemplateIds == null || selectedItemTemplateIds.Count == 0)
                return false;

            var candidates = stackable.BoosterSelectionRewards.Count > 0
                ? stackable.BoosterSelectionRewards
                : stackable.PackageRewards;
            if (candidates.Count == 0)
                return false;

            var rewardByItemId = candidates
                .GroupBy(reward => reward.ItemId)
                .ToDictionary(group => group.Key, group => group.First());
            var maxSelectionCount = stackable.BoosterSelectionNum > 0
                ? stackable.BoosterSelectionNum
                : selectedItemTemplateIds.Count;
            var seen = new HashSet<int>();

            foreach (var itemId in selectedItemTemplateIds.Where(id => id > 0))
            {
                if (!seen.Add(itemId))
                    continue;

                if (!rewardByItemId.TryGetValue(itemId, out var reward))
                    continue;

                rewards.Add(reward);
                if (rewards.Count >= maxSelectionCount)
                    break;
            }

            return rewards.Count > 0;
        }

        internal static bool TryResolveMallAutoOpenRewards(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int itemTemplateId,
            out List<PvfLib.BoosterRewardEntry> rewards)
        {
            rewards = null;
            var stackable = StackableItemProvider.Load(itemTemplateId);
            if (stackable == null)
                return false;

            var stackableType = NormalizeStackableType(stackable.StackableType);
            if (!stackableType.Equals("[cera package]", StringComparison.OrdinalIgnoreCase)
                && !stackableType.Equals("[cera booster]", StringComparison.OrdinalIgnoreCase)
                && !(stackableType.Equals("[booster]", StringComparison.OrdinalIgnoreCase) && IsMagicHammerBundle(itemTemplateId)))
                return false;

            var characterJobLabel = LoadCharacterJobLabel(connection, transaction, characterId);
            return TryResolvePackageRewards(itemTemplateId, stackable, stackableType, null, characterJobLabel, out rewards)
                && rewards.Count > 0;
        }

        internal static string NormalizeStackableType(string stackableType)
            => StackableItemProvider.NormalizeType(stackableType);

        internal static bool IsSupportedPackageType(string stackableType)
        {
            return stackableType.Equals("[booster]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[cera booster]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[booster random]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[cera package]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals(StackableItemProvider.RandomUpgradableLegacyType, StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[usable cera package]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[booster selection]", StringComparison.OrdinalIgnoreCase);
        }

        private static List<PvfLib.BoosterRewardEntry> RollBoosterRewards(
            IEnumerable<PvfLib.BoosterRewardEntry> rewards,
            string characterJobLabel)
        {
            var selected = new List<PvfLib.BoosterRewardEntry>();
            var eligibleRewards = rewards.Where(reward =>
                string.IsNullOrWhiteSpace(reward.CharacterJobLabel)
                || string.Equals(reward.CharacterJobLabel, characterJobLabel, StringComparison.OrdinalIgnoreCase));
            foreach (var group in eligibleRewards.GroupBy(reward => reward.Group))
            {
                var totalWeight = group.Sum(reward => Math.Max(0, reward.Weight));
                if (totalWeight <= 0)
                    continue;

                var drawCount = Math.Max(1, group.Max(reward => reward.DrawCount));
                for (var drawIndex = 0; drawIndex < drawCount; drawIndex++)
                {
                    var roll = ServerRandom.Next(totalWeight);
                    var cumulative = 0;
                    foreach (var reward in group)
                    {
                        cumulative += Math.Max(0, reward.Weight);
                        if (roll >= cumulative)
                            continue;

                        selected.Add(reward);
                        break;
                    }
                }
            }

            return selected;
        }

        private static bool TryResolveMagicHammerBundleRewards(
            int sourceItemTemplateId,
            PvfLib.StackableItemFile stackable,
            out List<PvfLib.BoosterRewardEntry> rewards)
        {
            rewards = new List<PvfLib.BoosterRewardEntry>();
            if (!IsMagicHammerBundle(sourceItemTemplateId) || stackable?.BoosterRewards == null)
                return false;

            var hammer = stackable.BoosterRewards.FirstOrDefault(reward => reward != null && reward.ItemId == MagicHammerMaterialItemTemplateId && reward.Count > 0);
            var box = stackable.BoosterRewards.FirstOrDefault(reward => reward != null && reward.ItemId == MagicHammerBoxItemTemplateId && reward.Count > 0);
            if (hammer == null || box == null)
                return false;

            rewards.Add(hammer);
            rewards.Add(box);
            return true;
        }

        private static bool IsMagicHammerBundle(int itemTemplateId)
        {
            return itemTemplateId >= MagicHammerBundleMinItemTemplateId && itemTemplateId <= MagicHammerBundleMaxItemTemplateId;
        }

        private static PvfLib.BoosterRewardEntry CloneRewardEntry(PvfLib.BoosterRewardEntry reward)
        {
            if (reward == null)
                return null;

            return new PvfLib.BoosterRewardEntry
            {
                ItemId = reward.ItemId,
                Count = Math.Max(1, reward.Count),
                Weight = reward.Weight,
                Group = reward.Group,
                DrawCount = reward.DrawCount,
                CharacterJobLabel = reward.CharacterJobLabel,
                UsablePeriodDays = reward.UsablePeriodDays,
            };
        }

        private static string LoadCharacterJobLabel(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT job FROM characters WHERE character_id = @characterId LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return null;

                var job = Convert.ToInt32(value);
                string[] labels =
                {
                    "swordman", "fighter", "gunner", "mage", "priest",
                    "at gunner", "thief", "at fighter", "at mage",
                    "demonic swordman", "creator mage", "at swordman", "knight",
                };
                return job >= 0 && job < labels.Length ? labels[job] : null;
            }
        }
    }
}
