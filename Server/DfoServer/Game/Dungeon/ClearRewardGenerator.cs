using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct ClearRewardGenerationContext
    {
        internal ClearRewardGenerationContext(
            int dungeonLevel,
            int difficulty,
            int partyMemberCount,
            float rankBonusRate,
            int normalKillCount,
            int championKillCount,
            int bossKillCount,
            int visitedRoomCount,
            int totalRoomCount,
            int standardDropRate = 100,
            double deathDropPenalty = 0.0)
        {
            DungeonLevel = Math.Max(1, dungeonLevel);
            Difficulty = difficulty;
            PartyMemberCount = Math.Max(1, Math.Min(4, partyMemberCount));
            RankBonusRate = Math.Max(0.0f, rankBonusRate);
            NormalKillCount = Math.Max(0, normalKillCount);
            ChampionKillCount = Math.Max(0, championKillCount);
            BossKillCount = Math.Max(0, bossKillCount);
            VisitedRoomCount = Math.Max(0, visitedRoomCount);
            TotalRoomCount = Math.Max(1, totalRoomCount);
            StandardDropRate = Math.Max(0, standardDropRate);
            DeathDropPenalty = Math.Max(0.0, Math.Min(1.0, deathDropPenalty));
        }

        internal int DungeonLevel { get; }
        internal int Difficulty { get; }
        internal int PartyMemberCount { get; }
        internal float RankBonusRate { get; }
        internal int NormalKillCount { get; }
        internal int ChampionKillCount { get; }
        internal int BossKillCount { get; }
        internal int VisitedRoomCount { get; }
        internal int TotalRoomCount { get; }
        internal int StandardDropRate { get; }
        internal double DeathDropPenalty { get; }
    }

    public static class ClearRewardGenerator
    {
        public struct CardReward
        {
            public bool IsGold;
            public int GoldAmount;
            public int ItemId;
            public int StackCount;
            public bool IsEquipment;
            public ushort Durability;
        }

        internal static void WarmUp()
        {
            ClearRewardDefinitionCatalog.WarmUp();
            EquipmentDropPoolProvider.WarmUp();
        }

        internal static int GetPaidCardCost(int dungeonLevel) =>
            ClearRewardDefinitionCatalog.Current.GetPaidCardCost(dungeonLevel);

        internal static CardReward GenerateFreeGoldCard(
            ClearRewardGenerationContext context,
            DnfLcg lcg)
        {
            if (lcg == null)
                throw new ArgumentNullException(nameof(lcg));

            var definition = ClearRewardDefinitionCatalog.Current;
            var weightedKills = (long)context.NormalKillCount
                + 2L * context.ChampionKillCount
                + 4L * context.BossKillCount;
            if (weightedKills <= 0)
                return new CardReward { IsGold = true, GoldAmount = 0 };

            var monsterGold = ExpTableProvider.GetMonsterGold(
                context.DungeonLevel,
                out var variancePercent);
            var baseGold = monsterGold * 175L / 1000L;
            var difficultyPartyRate =
                definition.GetDifficultyGoldRate(context.Difficulty)
                * definition.GetPartyMemberRate(context.PartyMemberCount);
            var rankCombinedRate =
                (1.0 + context.RankBonusRate) * difficultyPartyRate;
            var intermediate = ToNonnegativeInt(
                baseGold * weightedKills / 2.0 * rankCombinedRate);
            var memberGold = ToNonnegativeInt(
                intermediate
                / 2.0
                / context.PartyMemberCount
                * rankCombinedRate);

            if (variancePercent > 0 && memberGold > 0)
            {
                var variance = lcg.Next(2 * variancePercent + 1)
                    - variancePercent;
                memberGold = ToNonnegativeInt(
                    memberGold + memberGold * variance / 100.0);
            }

            return new CardReward
            {
                IsGold = true,
                GoldAmount = memberGold,
            };
        }

        internal static CardReward GenerateFreeItemCard(
            ClearRewardGenerationContext context,
            DnfLcg lcg)
        {
            var definition = ClearRewardDefinitionCatalog.Current;
            var mapRate = definition.GetExplorationItemRate(
                context.TotalRoomCount,
                context.VisitedRoomCount);
            return GenerateConfiguredItemCard(
                context,
                ClearRewardDropProfile.Default,
                mapRate,
                lcg);
        }

        internal static CardReward GeneratePaidItemCard(
            ClearRewardGenerationContext context,
            DnfLcg lcg)
        {
            return GenerateConfiguredItemCard(
                context,
                ClearRewardDropProfile.Event,
                ClearRewardDefinitionCatalog.Current.PaidCardCreateRate,
                lcg);
        }

        // Legacy tower callers need a reward candidate, not the ordinary card
        // appearance roll. The item itself still comes from the clear-reward pool.
        public static CardReward GenerateItemCard(
            int dungeonLevel,
            int difficulty,
            DnfLcg lcg)
        {
            var context = CreateLegacyContext(dungeonLevel, difficulty);
            return GenerateConfiguredItem(context, lcg);
        }

        public static CardReward GenerateEquipmentCard(
            int dungeonLevel,
            int difficulty,
            DnfLcg lcg)
        {
            var context = CreateLegacyContext(dungeonLevel, difficulty);
            var rarity = RollRarity(ClearRewardDefinitionCatalog.Current, lcg);
            return GenerateEquipment(context, itemType: 2, rarity, lcg);
        }

        // Kept for callers outside the ordinary settlement pipeline. Ordinary
        // card gold must use GenerateFreeGoldCard so kill facts are included.
        public static CardReward GenerateGoldCard(
            int dungeonLevel,
            int difficulty,
            DnfLcg lcg)
        {
            var monsterGold = ExpTableProvider.GetMonsterGold(
                Math.Max(1, dungeonLevel),
                out var variancePercent);
            var amount = ToNonnegativeInt(
                monsterGold
                * 175L
                / 1000.0
                * ClearRewardDefinitionCatalog.Current
                    .GetDifficultyGoldRate(difficulty));
            if (variancePercent > 0 && amount > 0)
            {
                var variance = lcg.Next(2 * variancePercent + 1)
                    - variancePercent;
                amount = ToNonnegativeInt(amount + amount * variance / 100.0);
            }
            return new CardReward { IsGold = true, GoldAmount = amount };
        }

        private static CardReward GenerateConfiguredItemCard(
            ClearRewardGenerationContext context,
            ClearRewardDropProfile profile,
            double mapRate,
            DnfLcg lcg)
        {
            if (lcg == null)
                throw new ArgumentNullException(nameof(lcg));

            var definition = ClearRewardDefinitionCatalog.Current;
            var baseProbability = definition.GetDropProbability(
                profile,
                context.DungeonLevel);
            var threshold = context.StandardDropRate
                / 100.0
                * (baseProbability * Math.Max(0.0, mapRate)
                    + definition.GetDifficultyItemBonus(context.Difficulty))
                * (1.0 - context.DeathDropPenalty);
            threshold = Math.Max(0.0, Math.Min(10000.0, threshold));
            if (lcg.Next(10000) >= threshold)
                return default;

            return GenerateConfiguredItem(context, lcg);
        }

        private static CardReward GenerateConfiguredItem(
            ClearRewardGenerationContext context,
            DnfLcg lcg)
        {
            var definition = ClearRewardDefinitionCatalog.Current;
            var itemType = RollItemType(definition, lcg);
            var rarity = RollRarity(definition, lcg);
            return GenerateEquipment(context, itemType, rarity, lcg);
        }

        private static CardReward GenerateEquipment(
            ClearRewardGenerationContext context,
            int itemType,
            int rarity,
            DnfLcg lcg)
        {
            // The active A12 table enables only type 2 (ordinary equipment)
            // and type 4 (avatar). Types 1/3 stay fail-closed until their
            // distinct stackable dictionary semantics are required by PVF.
            if (itemType != 2 && itemType != 4)
                return default;

            var definition = ClearRewardDefinitionCatalog.Current;
            var range = definition.GetGradeRange(context.DungeonLevel);
            var pool = EquipmentDropPoolProvider.GetClearRewardPool(
                avatar: itemType == 4);
            var candidates = new List<(int Id, int Weight)>();
            for (var offset = -range.Down; offset < range.Up; offset++)
            {
                var grade = context.DungeonLevel + offset;
                if (grade < 1 || grade > 200)
                    continue;
                var key = (long)grade * 10 + rarity;
                if (pool.TryGetValue(key, out var items))
                    candidates.AddRange(items);
            }

            if (candidates.Count == 0)
                return default;

            var totalWeight = 0L;
            foreach (var candidate in candidates)
                totalWeight += Math.Max(0, candidate.Weight);
            if (totalWeight <= 0)
                return default;

            var rollRange = (int)Math.Min(int.MaxValue, totalWeight);
            var roll = lcg.Next(rollRange);
            var cumulative = 0L;
            var itemId = 0;
            foreach (var candidate in candidates)
            {
                cumulative += Math.Max(0, candidate.Weight);
                if (roll < cumulative)
                {
                    itemId = candidate.Id;
                    break;
                }
            }

            if (itemId <= 0)
                itemId = candidates[candidates.Count - 1].Id;
            if (itemId <= 0)
                return default;

            var durability = ItemMetadataResolver.Resolve(itemId).Durability;
            return new CardReward
            {
                IsGold = false,
                ItemId = itemId,
                StackCount = 1,
                IsEquipment = true,
                Durability = (ushort)Math.Max(
                    0,
                    Math.Min(ushort.MaxValue, (int)durability)),
            };
        }

        private static int RollItemType(
            ClearRewardDefinition definition,
            DnfLcg lcg)
        {
            var total = definition.GetItemTypeWeightTotal();
            if (total <= 0)
                return 0;

            var roll = lcg.Next(total);
            var cumulative = 0;
            for (var itemType = 1; itemType <= definition.ItemTypeCount; itemType++)
            {
                cumulative += definition.GetItemTypeWeight(itemType);
                if (roll < cumulative)
                    return itemType;
            }

            return 0;
        }

        private static int RollRarity(
            ClearRewardDefinition definition,
            DnfLcg lcg)
        {
            var roll = lcg.Next(1000000) + 1;
            for (var rarity = 0; rarity < definition.RarityThresholdCount; rarity++)
            {
                if (roll <= definition.GetRarityThreshold(rarity))
                    return rarity;
            }

            return 0;
        }

        private static ClearRewardGenerationContext CreateLegacyContext(
            int dungeonLevel,
            int difficulty)
        {
            return new ClearRewardGenerationContext(
                dungeonLevel,
                difficulty,
                partyMemberCount: 1,
                rankBonusRate: 0.0f,
                normalKillCount: 0,
                championKillCount: 0,
                bossKillCount: 0,
                visitedRoomCount: 1,
                totalRoomCount: 1);
        }

        private static int ToNonnegativeInt(double value)
        {
            if (value <= 0.0 || double.IsNaN(value))
                return 0;
            if (value >= int.MaxValue)
                return int.MaxValue;
            return (int)value;
        }
    }
}
