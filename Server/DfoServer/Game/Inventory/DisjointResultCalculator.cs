using PvfLib;
using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Inventory
{
    internal static class DisjointResultCalculator
    {
        private static readonly object ConfigLogLock = new object();
        private static bool LoggedConfigSummary;

        public static List<DisjointMaterialResult> Calculate(ItemMetadata metadata)
        {
            var config = DisjointConfigProvider.LoadSystemDisjoint();
            LogConfigSummaryOnce(config);
            var result = new List<DisjointMaterialResult>();

            AddMaterial(result, config.GetNoElementCubeItemId(), CalculateCubeCount(config, metadata));
            AddAdditionalResult(config, metadata, result);
            AddExpandResult(config, metadata, result);

            return result;
        }

        internal static List<DisjointMaterialResult> CalculateEquipmentSoulResults(
            ItemMetadata metadata)
        {
            var config = DisjointConfigProvider.LoadSystemDisjoint();
            LogConfigSummaryOnce(config);
            var result = new List<DisjointMaterialResult>();

            // Only [additional result expand] equipment souls are shared by
            // system and player disjoint machines.
            AddExpandResult(config, metadata, result);
            return result;
        }

        private static void LogConfigSummaryOnce(DisjointFile config)
        {
            if (LoggedConfigSummary || config == null)
                return;

            lock (ConfigLogLock)
            {
                if (LoggedConfigSummary)
                    return;

                var additionalRows = new List<string>();
                for (var i = 0; i < config.AdditionalResults.Count; i++)
                    additionalRows.Add(i + ":" + string.Join(",", config.AdditionalResults[i]));

                var expandRows = new List<string>();
                for (var rarity = 0; rarity <= 6; rarity++)
                {
                    var expand = config.GetExpandResult(rarity);
                    if (expand != null)
                        expandRows.Add(rarity + ":" + expand.ItemTemplateId + "/" + expand.LevelDivisor + "/" + expand.GreatChancePercent + "/" + expand.NormalChancePercent);
                }

                FileLogger.Log($"[DisjointConfig] cubeBase={config.CubeCreationBase} cubeMul=[{string.Join(",", config.CubeCreationMultipliers)}] additionalRows={config.AdditionalResults.Count} additional=[{string.Join("; ", additionalRows)}] additionalConstRows={config.AdditionalResultConsts.Count} expand=[{string.Join("; ", expandRows)}]");
                LoggedConfigSummary = true;
            }
        }

        private static int CalculateCubeCount(DisjointFile config, ItemMetadata metadata)
        {
            var baseValue = config.CubeCreationBase > 0 ? config.CubeCreationBase : 150;
            var sellGold = Math.Max(1, metadata?.SellGold ?? 1);
            var multiplier = config.GetCubeCreationMultiplier(metadata?.Rarity ?? 0);
            var count = (int)Math.Floor(sellGold * multiplier / baseValue);
            return Math.Max(1, count);
        }

        private static void AddAdditionalResult(DisjointFile config, ItemMetadata metadata, List<DisjointMaterialResult> result)
        {
            var candidates = config.GetAdditionalItems(metadata?.Rarity ?? 0);
            if (candidates.Count == 0)
                return;

            var itemId = candidates.Count == 1
                ? candidates[0]
                : candidates[ServerRandom.Next(candidates.Count)];
            if (itemId <= 0)
                return;

            var count = CalculateAdditionalCount(config.GetAdditionalConst(metadata?.Rarity ?? 0), metadata?.MinimumLevel ?? 0);
            AddMaterial(result, itemId, count);
        }

        private static int CalculateAdditionalCount(DisjointAdditionalResultConst config, int level)
        {
            if (config == null)
                return 1;

            var greatChance = ClampPercent(config.GreatChancePercent);
            var roll = RollPercent();
            var divisor = roll < greatChance && config.GreatCountDivisor > 0
                ? config.GreatCountDivisor
                : config.NormalCountDivisor;
            if (divisor <= 0)
                return 1;

            return CalculateLevelDivisorCount(level, divisor);
        }

        private static void AddExpandResult(DisjointFile config, ItemMetadata metadata, List<DisjointMaterialResult> result)
        {
            var expand = config.GetExpandResult(metadata?.Rarity ?? 0);
            if (expand == null || !expand.Enabled || expand.ItemTemplateId <= 0)
                return;

            if (RollPercent() >= ClampPercent(expand.NormalChancePercent))
                return;

            var count = CalculateLevelDivisorCount(metadata?.MinimumLevel ?? 0, expand.LevelDivisor);
            if (RollPercent() < ClampPercent(expand.GreatChancePercent))
                count++;

            AddMaterial(result, expand.ItemTemplateId, count);
        }

        private static int CalculateLevelDivisorCount(int level, double divisor)
        {
            if (divisor <= 0)
                return 1;

            var value = Math.Max(1, level) / divisor;
            var count = (int)Math.Floor(value);
            var fraction = value - count;
            if (RollUnit() < fraction)
                count++;

            return Math.Max(1, count);
        }

        private static double RollPercent()
        {
            return ServerRandom.Next(1_000_000) / 10000.0;
        }

        private static double RollUnit()
        {
            return ServerRandom.Next(1_000_000) / 1_000_000.0;
        }

        private static double ClampPercent(double value)
        {
            if (value < 0)
                return 0;
            if (value > 100)
                return 100;
            return value;
        }

        private static void AddMaterial(List<DisjointMaterialResult> result, int itemId, int count)
        {
            if (itemId <= 0 || count <= 0)
                return;

            foreach (var existing in result)
            {
                if (existing.ItemTemplateId == itemId)
                {
                    existing.Count += count;
                    return;
                }
            }

            result.Add(new DisjointMaterialResult
            {
                SlotIndex = -1,
                ItemTemplateId = itemId,
                Count = count,
            });
        }
    }
}
