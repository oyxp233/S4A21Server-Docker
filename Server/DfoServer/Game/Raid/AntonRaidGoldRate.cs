using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Inventory;
using PvfLib;

namespace DfoServer.Game.Raid;

internal static class AntonRaidGoldRate
{
    internal const double TargetGoldProbability = 1.0;

    private static readonly ConcurrentDictionary<int, int> PoolClasses = new ConcurrentDictionary<int, int>();

    internal static double[] BoostWeights(double[] weights, bool[] gold)
    {
        if (weights.Length != gold.Length || weights.Any((double w) => !double.IsFinite(w) || w < 0.0))
        {
            throw new ArgumentException("Invalid raid reward weights");
        }
        double total = weights.Sum();
        double goldTotal = weights.Where((double w, int i) => gold[i]).Sum();
        if (goldTotal == 0.0 || goldTotal == total)
        {
            return (double[])weights.Clone();
        }
        double target = total * 1.0;
        return weights.Select((double w, int i) => w * (gold[i] ? (target / goldTotal) : ((total - target) / (total - goldTotal)))).ToArray();
    }

    internal static double[] GetWeights(uint phase, string type, IReadOnlyList<RaidStateReward> rewards)
    {
        double[] array = ((IEnumerable<RaidStateReward>)rewards).Select((Func<RaidStateReward, double>)((RaidStateReward r) => Math.Max(0, r.Weight))).ToArray();
        if (phase != 1 || !string.Equals(type, "squad_item", StringComparison.OrdinalIgnoreCase))
        {
            return array;
        }
        int[] source = rewards.Select((RaidStateReward r) => PoolClasses.GetOrAdd(r.ItemId, ClassifyPool)).ToArray();
        if (source.Any((int c) => c < 0))
        {
            return array;
        }
        return BoostWeights(array, source.Select((int c) => c == 1).ToArray());
    }

    private static int ClassifyPool(int id)
    {
        int[] array = (StackableItemProvider.Load(id)?.UpgradableLegacyRewards?.Where((BoosterRewardEntry e) => e.ItemId >= 0 && e.Weight > 0).ToArray())?.Select((Func<BoosterRewardEntry, int>)((BoosterRewardEntry e) => AntonRaidGoldRewards.GetDisplayFlag((uint)e.ItemId, ItemMetadataResolver.Resolve(e.ItemId)))).Distinct().ToArray();
        if (array != null && array.Length == 1)
        {
            return array[0];
        }
        FileLogger.Log($"[AntonRaidGoldRate] pool={id} mixed/missing: retaining original weights");
        return -1;
    }

    internal static int Select(double[] weights, double unitRoll)
    {
        if (unitRoll < 0.0 || unitRoll >= 1.0 || !double.IsFinite(unitRoll))
        {
            throw new ArgumentOutOfRangeException("unitRoll");
        }
        double num = unitRoll * weights.Sum();
        int result = -1;
        for (int i = 0; i < weights.Length; i++)
        {
            if (!(weights[i] <= 0.0))
            {
                result = i;
                num -= weights[i];
                if (num < 0.0)
                {
                    return i;
                }
            }
        }
        return result;
    }
}
