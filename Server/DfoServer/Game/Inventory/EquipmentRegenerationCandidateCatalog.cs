using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal static class EquipmentRegenerationCandidateCatalog
    {
        private static readonly Lazy<Dictionary<long, IReadOnlyList<EquipmentDefinition>>> Buckets
            = new Lazy<Dictionary<long, IReadOnlyList<EquipmentDefinition>>>(Load);

        internal static void Warmup()
            => _ = Buckets.Value;

        internal static IReadOnlyList<EquipmentDefinition> GetCandidates(int rarity, int minimumLevel)
            => Buckets.Value.TryGetValue(CreateKey(rarity, minimumLevel), out var candidates)
                ? candidates
                : Array.Empty<EquipmentDefinition>();

        internal static (int BucketCount, int CandidateCount) GetStatistics()
        {
            var buckets = Buckets.Value;
            var candidateCount = 0;
            foreach (var bucket in buckets.Values)
                candidateCount += bucket.Count;
            return (buckets.Count, candidateCount);
        }

        private static Dictionary<long, IReadOnlyList<EquipmentDefinition>> Load()
        {
            var mutable = new Dictionary<long, List<EquipmentDefinition>>();
            var loaded = 0;
            var skipped = 0;
            var errors = 0;

            foreach (var equipment in EquipmentDefinitionCatalog.GetAll())
            {
                if (equipment.Rarity < 0
                    || equipment.MinimumLevel < 0
                    || string.IsNullOrWhiteSpace(equipment.ItemGroupName)
                    || IsGeneratedModifiedOption(equipment.Rarity, equipment.CreationRate, equipment.ForceResultItemRule))
                {
                    skipped++;
                    continue;
                }

                var key = CreateKey(equipment.Rarity, equipment.MinimumLevel);
                if (!mutable.TryGetValue(key, out var bucket))
                {
                    bucket = new List<EquipmentDefinition>();
                    mutable[key] = bucket;
                }
                bucket.Add(equipment);
                loaded++;
            }

            var result = new Dictionary<long, IReadOnlyList<EquipmentDefinition>>(mutable.Count);
            foreach (var pair in mutable)
                result[pair.Key] = pair.Value.AsReadOnly();

            FileLogger.Log(
                $"[EquipmentRegenerationCatalog] loaded={loaded} skipped={skipped} errors={errors} buckets={result.Count}");
            return result;
        }

        private static long CreateKey(int rarity, int minimumLevel)
            => ((long)rarity << 32) | (uint)minimumLevel;

        internal static bool IsGeneratedModifiedOption(int rarity, int creationRate, int[] forceResultItemRule)
            => rarity == 3
                && creationRate == 0
                && forceResultItemRule != null
                && forceResultItemRule.Length >= 2
                && forceResultItemRule[0] == 1
                && forceResultItemRule[1] == 0;
    }
}
