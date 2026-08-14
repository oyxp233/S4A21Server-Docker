using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal static class AvatarDisjointConfigProvider
    {
        private const string PvfPath = "etc/avatardisjoint";
        private static readonly Regex ResultSection = new Regex(
            @"\[result info\](.*?)\[/result info\]",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex IntegerToken = new Regex(@"[-+]?\d+", RegexOptions.Compiled);
        private static readonly Lazy<List<AvatarDisjointGradeTable>> Tables =
            new Lazy<List<AvatarDisjointGradeTable>>(Load);

        internal static List<DisjointMaterialResult> Calculate(int grade)
        {
            var tables = Tables.Value;
            if (grade < 0 || grade >= tables.Count)
                return new List<DisjointMaterialResult>();

            var result = new List<DisjointMaterialResult>();
            foreach (var pool in tables[grade].Pools)
            {
                for (var i = 0; i < pool.PickCount; i++)
                {
                    var reward = PickWeighted(pool.Rewards);
                    if (reward == null)
                        continue;
                    AddOrMerge(result, reward.ItemTemplateId, reward.Count);
                }
            }
            return result;
        }

        private static List<AvatarDisjointGradeTable> Load()
        {
            var result = new List<AvatarDisjointGradeTable>();
            try
            {
                foreach (var content in PvfArchiveAccessor.ReadAllText(PvfPath))
                {
                    var table = Parse(content);
                    if (table.Pools.Count > 0)
                        result.Add(table);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AvatarDisjointConfig] load failed: {ex.Message}");
            }

            var summary = new List<string>();
            for (var grade = 0; grade < result.Count; grade++)
                summary.Add($"g{grade}:{result[grade].MinValue}-{result[grade].MaxValue}/pools={result[grade].Pools.Count}");
            FileLogger.Log($"[AvatarDisjointConfig] path={PvfPath} tables={result.Count} [{string.Join(", ", summary)}]");
            return result;
        }

        internal static AvatarDisjointGradeTable Parse(string content)
        {
            var table = new AvatarDisjointGradeTable();
            var range = Regex.Match(content ?? string.Empty,
                @"\[min max value\]\s*([-+]?\d+)\s+([-+]?\d+)", RegexOptions.IgnoreCase);
            if (range.Success)
            {
                int.TryParse(range.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var min);
                int.TryParse(range.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max);
                table.MinValue = min;
                table.MaxValue = max;
            }

            foreach (Match section in ResultSection.Matches(content ?? string.Empty))
            {
                var values = new List<int>();
                foreach (Match token in IntegerToken.Matches(section.Groups[1].Value))
                    if (int.TryParse(token.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                        values.Add(value);
                if (values.Count < 5 || values[0] <= 0)
                    continue;

                var pool = new AvatarDisjointRewardPool { PickCount = values[0] };
                for (var i = 1; i + 3 < values.Count; i += 4)
                {
                    if (values[i] <= 0 || values[i + 1] <= 0 || values[i + 2] <= 0)
                        continue;
                    pool.Rewards.Add(new AvatarDisjointReward
                    {
                        ItemTemplateId = values[i],
                        Weight = values[i + 1],
                        Count = values[i + 2],
                        Special = values[i + 3] != 0,
                    });
                }
                if (pool.Rewards.Count > 0)
                    table.Pools.Add(pool);
            }
            return table;
        }

        private static AvatarDisjointReward PickWeighted(List<AvatarDisjointReward> rewards)
        {
            long total = 0;
            foreach (var reward in rewards)
                total += Math.Max(0, reward.Weight);
            if (total <= 0)
                return null;

            var roll = NextLong(total);
            foreach (var reward in rewards)
            {
                roll -= Math.Max(0, reward.Weight);
                if (roll < 0)
                    return reward;
            }
            return rewards[rewards.Count - 1];
        }

        private static long NextLong(long maxValue)
        {
            if (maxValue <= 0)
                return 0;
            if (maxValue <= int.MaxValue)
                return ServerRandom.Next((int)maxValue);

            var high = (long)ServerRandom.Next();
            var low = (uint)ServerRandom.Next();
            return ((high << 31) | low) % maxValue;
        }

        private static void AddOrMerge(List<DisjointMaterialResult> result, int itemId, int count)
        {
            foreach (var existing in result)
            {
                if (existing.ItemTemplateId != itemId)
                    continue;
                existing.Count += count;
                return;
            }
            result.Add(new DisjointMaterialResult { SlotIndex = -1, ItemTemplateId = itemId, Count = count });
        }
    }

    internal sealed class AvatarDisjointGradeTable
    {
        internal int MinValue { get; set; }
        internal int MaxValue { get; set; }
        internal List<AvatarDisjointRewardPool> Pools { get; } = new List<AvatarDisjointRewardPool>();
    }

    internal sealed class AvatarDisjointRewardPool
    {
        internal int PickCount { get; set; }
        internal List<AvatarDisjointReward> Rewards { get; } = new List<AvatarDisjointReward>();
    }

    internal sealed class AvatarDisjointReward
    {
        internal int ItemTemplateId { get; set; }
        internal int Weight { get; set; }
        internal int Count { get; set; }
        internal bool Special { get; set; }
    }
}
