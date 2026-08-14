using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal enum PassiveObjectDropIntentKind
    {
        Gold = 0,
        Item = 1,
    }

    internal readonly struct PassiveObjectDropIntent
    {
        internal PassiveObjectDropIntent(
            byte objectIndex,
            PassiveObjectDropIntentKind kind,
            int itemId,
            int amount)
        {
            ObjectIndex = objectIndex;
            Kind = kind;
            ItemId = itemId;
            Amount = amount;
        }

        internal byte ObjectIndex { get; }
        internal PassiveObjectDropIntentKind Kind { get; }
        internal int ItemId { get; }
        internal int Amount { get; }
    }

    internal sealed class PassiveObjectDropPlan
    {
        internal static readonly PassiveObjectDropPlan Empty =
            new PassiveObjectDropPlan(
                Array.Empty<PassiveObjectDropIntent>(),
                specificDropCount: 0,
                randomDropCount: 0,
                invalidActionCount: 0,
                unsupportedRandomCategoryCount: 0,
                wasTruncated: false);

        internal PassiveObjectDropPlan(
            IReadOnlyList<PassiveObjectDropIntent> intents,
            int specificDropCount,
            int randomDropCount,
            int invalidActionCount,
            int unsupportedRandomCategoryCount,
            bool wasTruncated)
        {
            var copy = new PassiveObjectDropIntent[intents?.Count ?? 0];
            for (var index = 0; index < copy.Length; index++)
                copy[index] = intents[index];
            Intents = Array.AsReadOnly(copy);
            SpecificDropCount = specificDropCount;
            RandomDropCount = randomDropCount;
            InvalidActionCount = invalidActionCount;
            UnsupportedRandomCategoryCount = unsupportedRandomCategoryCount;
            WasTruncated = wasTruncated;
        }

        internal IReadOnlyList<PassiveObjectDropIntent> Intents { get; }
        internal int SpecificDropCount { get; }
        internal int RandomDropCount { get; }
        internal int InvalidActionCount { get; }
        internal int UnsupportedRandomCategoryCount { get; }
        internal bool WasTruncated { get; }
    }

    internal sealed class PassiveObjectDropPlanningService
    {
        private const int ProbabilityUpperBound = 10000;
        private const int RarityUpperBound = 1000000;
        private const int EquipmentItemType = 2;
        private const int ObjectActorType = 0;
        private const int MaxProjectedDrops = byte.MaxValue;
        private const int MaxGenerationAttempts = 1024;

        private static readonly Lazy<PassiveObjectDropPlanningService> DefaultService =
            new Lazy<PassiveObjectDropPlanningService>(CreateDefault);

        private readonly PassiveObjectRandomDropDefinition _definition;
        private readonly IReadOnlyDictionary<long, List<(int Id, int Weight)>>
            _equipmentPool;
        private readonly Func<int, DnfLcg, int> _goldAmountGenerator;

        internal PassiveObjectDropPlanningService(
            PassiveObjectRandomDropDefinition definition,
            IReadOnlyDictionary<long, List<(int Id, int Weight)>> equipmentPool,
            Func<int, DnfLcg, int> goldAmountGenerator)
        {
            _definition = definition
                ?? PassiveObjectRandomDropDefinition.Disabled(
                    "random object drop definition is unavailable");
            _equipmentPool = equipmentPool
                ?? new Dictionary<long, List<(int Id, int Weight)>>();
            _goldAmountGenerator = goldAmountGenerator
                ?? throw new ArgumentNullException(nameof(goldAmountGenerator));
        }

        internal static PassiveObjectDropPlanningService Default =>
            DefaultService.Value;

        internal static void WarmUp()
        {
            _ = DefaultService.Value;
        }

        internal PassiveObjectDropPlan Plan(
            IReadOnlyList<SpecialPassiveObjectItemGroup> groups,
            IReadOnlyList<SpecialPassiveObjectInfo> objects,
            int dungeonBasisLevel,
            int difficulty,
            DnfLcg lcg)
        {
            if (groups == null
                || groups.Count == 0
                || objects == null
                || objects.Count == 0
                || lcg == null)
            {
                return PassiveObjectDropPlan.Empty;
            }

            var intents = new List<PassiveObjectDropIntent>();
            var specificDropCount = 0;
            var randomDropCount = 0;
            var invalidActionCount = 0;
            var unsupportedRandomCategoryCount = 0;
            var attemptCount = 0;
            var wasTruncated = false;

            for (var objectIndex = 0; objectIndex < objects.Count; objectIndex++)
            {
                var actor = objects[objectIndex];
                if (actor?.Spawns == null || actor.Spawns.Count == 0)
                    continue;
                if (objectIndex > byte.MaxValue)
                {
                    invalidActionCount += actor.Spawns.Count;
                    continue;
                }

                foreach (var spawn in actor.Spawns)
                {
                    if (spawn == null
                        || !string.Equals(
                            spawn.Kind,
                            "[item]",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (spawn.Code < 0
                        || spawn.Code >= groups.Count
                        || groups[spawn.Code] == null
                        || groups[spawn.Code].GroupIndex != spawn.Code
                        || spawn.Level < -1
                        || spawn.Param0 < -1)
                    {
                        invalidActionCount++;
                        continue;
                    }

                    var group = groups[spawn.Code];
                    var specificCount = Math.Max(0, spawn.Level);
                    for (var iteration = 0; iteration < specificCount; iteration++)
                    {
                        if (!TryConsumeAttempt(ref attemptCount))
                        {
                            wasTruncated = true;
                            break;
                        }

                        if (TryChooseSpecificItem(group.Items, lcg, out var itemId)
                            && TryAddIntent(
                                intents,
                                new PassiveObjectDropIntent(
                                    (byte)objectIndex,
                                    PassiveObjectDropIntentKind.Item,
                                    itemId,
                                    amount: 1)))
                        {
                            specificDropCount++;
                        }
                        else if (intents.Count >= MaxProjectedDrops)
                        {
                            wasTruncated = true;
                            break;
                        }
                    }

                    var randomCount = Math.Max(0, spawn.Param0);
                    var randomLevel = group.LevelOverride == -1
                        ? dungeonBasisLevel
                        : group.LevelOverride;
                    for (var iteration = 0; iteration < randomCount; iteration++)
                    {
                        if (!TryConsumeAttempt(ref attemptCount))
                        {
                            wasTruncated = true;
                            break;
                        }

                        GenerateRandomDrops(
                            (byte)objectIndex,
                            randomLevel,
                            difficulty,
                            lcg,
                            intents,
                            ref randomDropCount,
                            ref unsupportedRandomCategoryCount,
                            ref wasTruncated);
                        if (wasTruncated)
                            break;
                    }

                    if (wasTruncated)
                        break;
                }

                if (wasTruncated)
                    break;
            }

            return intents.Count == 0
                && invalidActionCount == 0
                && unsupportedRandomCategoryCount == 0
                && !wasTruncated
                    ? PassiveObjectDropPlan.Empty
                    : new PassiveObjectDropPlan(
                        intents,
                        specificDropCount,
                        randomDropCount,
                        invalidActionCount,
                        unsupportedRandomCategoryCount,
                        wasTruncated);
        }

        private void GenerateRandomDrops(
            byte objectIndex,
            int level,
            int difficulty,
            DnfLcg lcg,
            List<PassiveObjectDropIntent> intents,
            ref int randomDropCount,
            ref int unsupportedRandomCategoryCount,
            ref bool wasTruncated)
        {
            if (!_definition.IsValid || level <= 0)
                return;

            var goldRate = GetScaledRate(level, category: 0, difficulty);
            if (goldRate > 0 && lcg.Next(ProbabilityUpperBound + 1) < goldRate)
            {
                var amount = _goldAmountGenerator(level, lcg);
                if (amount > 0 && TryAddIntent(
                        intents,
                        new PassiveObjectDropIntent(
                            objectIndex,
                            PassiveObjectDropIntentKind.Gold,
                            itemId: 0,
                            amount)))
                {
                    randomDropCount++;
                }
                else if (intents.Count >= MaxProjectedDrops)
                {
                    wasTruncated = true;
                    return;
                }
            }

            for (var itemType = 1;
                 itemType <= PassiveObjectRandomDropDefinition.ItemTypeCount;
                 itemType++)
            {
                var rate = GetScaledRate(level, itemType, difficulty);
                if (rate <= 0
                    || lcg.Next(ProbabilityUpperBound + 1) > rate)
                {
                    continue;
                }

                var rarity = RollRarity(itemType, lcg);
                if (itemType != EquipmentItemType)
                {
                    unsupportedRandomCategoryCount++;
                    continue;
                }

                if (TryChooseEquipment(level, rarity, lcg, out var itemId)
                    && TryAddIntent(
                        intents,
                        new PassiveObjectDropIntent(
                            objectIndex,
                            PassiveObjectDropIntentKind.Item,
                            itemId,
                            amount: 1)))
                {
                    randomDropCount++;
                }
                else if (intents.Count >= MaxProjectedDrops)
                {
                    wasTruncated = true;
                    return;
                }
            }
        }

        private int GetScaledRate(int level, int category, int difficulty)
        {
            var baseRate = _definition.GetBaseRate(level, category);
            if (baseRate <= 0)
                return 0;

            var scaled = baseRate
                * _definition.GetDifficultyRate(category, difficulty)
                * _definition.GetActorTypeRate(category, ObjectActorType);
            if (scaled <= 0.0 || double.IsNaN(scaled))
                return 0;
            if (scaled >= ProbabilityUpperBound)
                return ProbabilityUpperBound;
            return (int)scaled;
        }

        private int RollRarity(int itemType, DnfLcg lcg)
        {
            var roll = lcg.Next(RarityUpperBound + 1);
            const int oldServerRarityCount = 6;
            for (var rarity = 0; rarity < oldServerRarityCount; rarity++)
            {
                if (_definition.GetRarityThreshold(itemType, rarity) >= roll)
                    return rarity;
            }
            return 0;
        }

        private bool TryChooseEquipment(
            int level,
            int rarity,
            DnfLcg lcg,
            out int itemId)
        {
            itemId = 0;
            if (!_definition.TryGetGradeRange(level, out var range))
                return false;

            var candidates = new List<(int Id, int Weight)>();
            for (var offset = -range.Down; offset < range.Up; offset++)
            {
                var grade = level + offset;
                if (grade <= 0 || grade > 200)
                    continue;

                var key = (long)grade * 10 + rarity;
                if (_equipmentPool.TryGetValue(key, out var bucket))
                    candidates.AddRange(bucket);
            }

            long totalWeight = 0;
            for (var index = 0; index < candidates.Count; index++)
                totalWeight += Math.Max(0, candidates[index].Weight);
            if (totalWeight <= 0)
                return false;

            var roll = NextBounded(lcg, totalWeight);
            long cumulative = 0;
            for (var index = 0; index < candidates.Count; index++)
            {
                cumulative += Math.Max(0, candidates[index].Weight);
                if (roll < cumulative)
                {
                    itemId = candidates[index].Id;
                    return itemId > 0;
                }
            }
            return false;
        }

        private static bool TryChooseSpecificItem(
            IReadOnlyList<SpecialPassiveObjectItem> items,
            DnfLcg lcg,
            out int itemId)
        {
            itemId = 0;
            if (items == null || items.Count == 0)
                return false;

            var roll = lcg.Next(ProbabilityUpperBound + 1);
            long cumulative = 0;
            for (var index = 0; index < items.Count; index++)
            {
                cumulative += Math.Max(0, items[index].Weight);
                if (roll < cumulative)
                {
                    itemId = items[index].ItemId;
                    return itemId > 0;
                }
            }
            return false;
        }

        private static long NextBounded(DnfLcg lcg, long upperBoundExclusive)
        {
            if (upperBoundExclusive <= int.MaxValue)
                return lcg.Next((int)upperBoundExclusive);

            var sample = ((ulong)(uint)lcg.Next() << 31)
                | (uint)lcg.Next();
            return (long)(sample % (ulong)upperBoundExclusive);
        }

        private static bool TryAddIntent(
            ICollection<PassiveObjectDropIntent> intents,
            PassiveObjectDropIntent intent)
        {
            if (intents.Count >= MaxProjectedDrops)
                return false;
            intents.Add(intent);
            return true;
        }

        private static bool TryConsumeAttempt(ref int attemptCount)
        {
            if (attemptCount >= MaxGenerationAttempts)
                return false;
            attemptCount++;
            return true;
        }

        private static PassiveObjectDropPlanningService CreateDefault()
        {
            PassiveObjectRandomDropDefinitionCatalog.WarmUp();
            EquipmentDropPoolProvider.WarmUp();
            _ = ExpTableProvider.GetMonsterGold(1, out _);
            return new PassiveObjectDropPlanningService(
                PassiveObjectRandomDropDefinitionCatalog.Current,
                EquipmentDropPoolProvider.GetClearRewardPool(avatar: false),
                GenerateGoldAmount);
        }

        private static int GenerateGoldAmount(int level, DnfLcg lcg)
        {
            var basis = ExpTableProvider.GetMonsterGold(level, out var variancePercent);
            if (basis <= 0)
                return 0;

            var variance = variancePercent > 0
                ? lcg.Next(variancePercent * 2 + 1) - variancePercent
                : 0;
            var amount = basis + (long)basis * variance / 100;
            if (amount <= 0)
                return 1;
            return amount >= int.MaxValue ? int.MaxValue : (int)amount;
        }
    }
}
