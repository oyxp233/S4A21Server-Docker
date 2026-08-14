using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    public static class IndependentDropSystem
    {
        private const int DifficultyTierCount = 5;
        private const int MaxPartyMemberCount = 4;
        private const int DropCountCapIndex = 4;
        private const int StandardProbabilityDenominator = 1_000_000;
        private const int ExternalPoolProbabilityDenominator = 100_000_000;
        private const int MaxTraceItems = 24;

        public static List<DropInfo> GenerateDrops(
            int monsterCode,
            int difficulty,
            int dungeonLevel,
            int partyMemberCount,
            int chronicleDropJobGroup,
            DnfLcg lcg,
            ref ushort slotCounter)
        {
            var result = new List<DropInfo>();
            if (!IndependentDropDefinitionCatalog.TryGetMonsterEntries(
                    monsterCode,
                    out var entries))
            {
                return result;
            }

            var difficultyIndex = Math.Max(
                0,
                Math.Min(difficulty, DifficultyTierCount - 1));
            var partyCount = Math.Max(
                1,
                Math.Min(partyMemberCount, MaxPartyMemberCount));
            var partyIndex = partyCount - 1;
            var matchedEntries = 0;
            var unresolvedPoolEntries = 0;
            var totalAttempts = 0;
            var successfulRolls = 0;
            var finalRolls = 0;
            var emittedTrace = new List<string>();
            var unresolvedPoolTrace = new List<string>();

            for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                var entry = entries[entryIndex];

                if (entry.LevelMin > 0
                    && entry.LevelMax > 0
                    && (dungeonLevel < entry.LevelMin
                        || dungeonLevel > entry.LevelMax))
                {
                    continue;
                }

                if (entry.Difficulty >= 0
                    && entry.Difficulty != difficulty)
                {
                    continue;
                }

                IndependentDropWeightedPoolDefinition itemPool = null;
                if (entry.HasItemPool
                    && !entry.TryResolvePool(
                        chronicleDropJobGroup,
                        out itemPool))
                {
                    unresolvedPoolEntries++;
                    if (unresolvedPoolTrace.Count < MaxTraceItems)
                    {
                        unresolvedPoolTrace.Add(
                            string.Join(",", entry.PoolIndexes));
                    }
                    continue;
                }

                var probability = entry.GetProbability(difficultyIndex);
                var attempts = entry.GetCount(partyIndex);
                // ETC count columns 0..3 select party size; column 4 is the cap.
                var cap = entry.GetCount(DropCountCapIndex);
                if (probability <= 0 || attempts <= 0 || cap <= 0)
                    continue;

                matchedEntries++;
                totalAttempts += attempts;

                var dropCount = 0;
                if (entry.ItemId != 0
                    || (itemPool != null && itemPool.TotalWeight > 0))
                {
                    var denominator = GetProbabilityDenominator(
                        entry.PoolKind);
                    for (var attempt = 0; attempt < attempts; attempt++)
                    {
                        if (IsProbabilityHit(
                                entry.PoolKind,
                                probability,
                                lcg.Next(denominator)))
                        {
                            dropCount++;
                        }
                    }
                }
                else
                {
                    dropCount = attempts;
                }

                successfulRolls += dropCount;
                dropCount = Math.Min(dropCount, cap);
                finalRolls += dropCount;

                if (dropCount <= 0)
                    continue;

                if (itemPool != null && itemPool.TotalWeight > 0)
                {
                    for (var dropIndex = 0;
                        dropIndex < dropCount;
                        dropIndex++)
                    {
                        var roll = lcg.Next(itemPool.TotalWeight);
                        if (!itemPool.TrySelect(roll, out var selected))
                            continue;

                        AddDrop(result, selected.ItemId, ref slotCounter);
                        if (emittedTrace.Count < MaxTraceItems)
                        {
                            emittedTrace.Add(
                                $"{selected.PoolIndex}:{selected.ItemId}");
                        }
                    }
                }
                else if (entry.ItemId > 0)
                {
                    for (var dropIndex = 0;
                        dropIndex < dropCount;
                        dropIndex++)
                    {
                        AddDrop(result, entry.ItemId, ref slotCounter);
                        if (emittedTrace.Count < MaxTraceItems)
                            emittedTrace.Add($"direct:{entry.ItemId}");
                    }
                }
            }

            if (matchedEntries > 0 || unresolvedPoolEntries > 0)
            {
                FileLogger.Log(
                    $"[IndependentDrop] monster={monsterCode} " +
                    $"difficulty={difficulty} party={partyCount} " +
                    $"jobGroup={chronicleDropJobGroup} " +
                    $"entries={matchedEntries} " +
                    $"unresolvedPools={unresolvedPoolEntries} " +
                    $"attempts={totalAttempts} successes={successfulRolls} " +
                    $"capped={finalRolls} emitted={result.Count} " +
                    $"poolItems={FormatTrace(emittedTrace)} " +
                    $"missingPoolIndexes={FormatTrace(unresolvedPoolTrace)}");
            }

            return result;
        }

        internal static bool TryGetDirectItemProbability(
            int monsterCode,
            int difficulty,
            int itemId,
            out int probability)
        {
            probability = 0;
            if (itemId <= 0
                || !IndependentDropDefinitionCatalog.TryGetMonsterEntries(
                    monsterCode,
                    out var entries))
            {
                return false;
            }

            var difficultyIndex = Math.Max(
                0,
                Math.Min(difficulty, DifficultyTierCount - 1));
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry.ItemId != itemId
                    || entry.HasItemPool
                    || (entry.Difficulty >= 0
                        && entry.Difficulty != difficulty))
                {
                    continue;
                }

                probability = Math.Max(
                    probability,
                    entry.GetProbability(difficultyIndex));
            }

            return probability > 0;
        }

        internal static int GetProbabilityDenominator(
            IndependentDropPoolKind poolKind)
            => poolKind == IndependentDropPoolKind.External
                ? ExternalPoolProbabilityDenominator
                : StandardProbabilityDenominator;

        internal static bool IsProbabilityHit(
            IndependentDropPoolKind poolKind,
            int probability,
            int roll)
        {
            var denominator = GetProbabilityDenominator(poolKind);
            return probability > 0
                && roll >= 0
                && roll < denominator
                && probability > roll;
        }

        // Some dungeon mechanisms scale a configured item template instead of
        // rolling it at monster-death time. Resolve one active direct template;
        // list pools or multiple different candidates fail closed.
        internal static bool TryResolveSingleFixedDropTemplate(
            int monsterCode,
            int difficulty,
            int dungeonLevel,
            int partyMemberCount,
            out int itemId,
            out int count)
        {
            itemId = 0;
            count = 0;
            if (!IndependentDropDefinitionCatalog.TryGetMonsterEntries(
                    monsterCode,
                    out var entries))
            {
                return false;
            }

            var difficultyIndex = Math.Max(
                0,
                Math.Min(difficulty, DifficultyTierCount - 1));
            var partyIndex = Math.Max(
                0,
                Math.Min(partyMemberCount, MaxPartyMemberCount) - 1);
            foreach (var entry in entries)
            {
                if (entry.ItemId <= 0
                    || entry.HasItemPool
                    || entry.GetProbability(difficultyIndex) <= 0
                    || entry.GetCount(partyIndex) <= 0
                    || entry.GetCount(DropCountCapIndex) <= 0
                    || (entry.LevelMin > 0
                        && entry.LevelMax > 0
                        && (dungeonLevel < entry.LevelMin
                            || dungeonLevel > entry.LevelMax))
                    || (entry.Difficulty >= 0
                        && entry.Difficulty != difficulty))
                {
                    continue;
                }

                var candidateItemId = entry.ItemId;
                var candidateCount = Math.Min(
                    entry.GetCount(partyIndex),
                    entry.GetCount(DropCountCapIndex));
                if (itemId != 0
                    && (itemId != candidateItemId
                        || count != candidateCount))
                {
                    itemId = 0;
                    count = 0;
                    return false;
                }

                itemId = candidateItemId;
                count = candidateCount;
            }

            return itemId > 0 && count > 0;
        }

        private static void AddDrop(
            List<DropInfo> drops,
            int itemId,
            ref ushort slotCounter)
        {
            slotCounter++;
            drops.Add(DropInfo.CreateItem(slotCounter, itemId, 1));
        }

        private static string FormatTrace(IReadOnlyList<string> values)
            => values == null || values.Count == 0
                ? "none"
                : string.Join(",", values);
    }
}
