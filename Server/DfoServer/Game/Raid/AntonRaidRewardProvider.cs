using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Raid
{
    internal static class AntonRaidRewardProvider
    {
        private const string ConfigPath = "etc/raid/anton.etc";
        private const string SituationConfigPath = "etc/raid/anton.etc.buff";
        private const uint StartDelayFallbackSeconds = 3;
        private const uint PhaseBreakFallbackSeconds = 600;
        private const uint NormalShieldChargeFallbackRate = 17;
        private const uint InfectionShieldChargeFallbackRate = 34;
        private static readonly uint[] HatcheryFallbackDungeonIds = { 221, 222, 223, 224 };
        private const uint HatcheryFallbackOpenCount = 3;
        private static readonly uint[] ExceptCheatFallbackDungeonIds = { 220 };
        private const uint PartyCardFallbackItemId = 10094735;
        private const uint GoldFallbackItemId = 10094732;
        private const uint GoldRankBFallbackItemId = 10094734;
        private const uint GoldRankCFallbackItemId = 10094786;
        private const uint SquadCommonFallbackItemId = 10094731;
        private const uint SquadRareFallbackItemId = 10094737;
        private const uint SquadSpecialFallbackItemId = 10094784;
        private const uint PhaseTwoPartyCardCommonFallbackItemId = 10094785;
        private const uint PhaseTwoPartyCardRareFallbackItemId = 10094737;
        private const uint PhaseTwoGoldFallbackItemId = 10094789;
        private const uint PhaseTwoGoldRankBFallbackItemId = 10094788;
        private const uint PhaseTwoGoldRankCFallbackItemId = 10094787;
        private static readonly uint[] PhaseTwoSquadFallbackItemIds =
        {
            10094739,
            10096324,
            10094738,
            10096325,
        };
        private static readonly int[] PhaseTwoSquadFallbackWeights = { 86, 7, 5, 2 };

        private static readonly Lazy<RaidEtcFile> Configuration =
            new Lazy<RaidEtcFile>(LoadConfiguration);
        private static readonly Lazy<RaidBuffFile> SituationConfiguration =
            new Lazy<RaidBuffFile>(LoadSituationConfiguration);

        internal static IReadOnlyList<RaidBuffDefinition> GetRaidBuffDefinitions()
        {
            var configured = SituationConfiguration.Value?.Buffs;
            return configured != null && configured.Count > 0 ? configured : Array.Empty<RaidBuffDefinition>();
        }

        internal static IReadOnlyList<RaidMonsterDefinition> GetRaidMonsterDefinitions()
        {
            var configured = SituationConfiguration.Value?.Monsters;
            return configured != null && configured.Count > 0 ? configured : Array.Empty<RaidMonsterDefinition>();
        }

        internal static uint GetPhaseRank(uint deathCount)
        {
            return GetPhaseRank(0, deathCount);
        }

        internal static uint[] GetHatcheryDungeonIds()
        {
            var config = Configuration.Value;
            if (config != null
                && config.HatcheryTotalCount > 0
                && config.HatcheryDungeonIds.Count >= config.HatcheryTotalCount)
            {
                var configured = config.HatcheryDungeonIds
                    .Take(config.HatcheryTotalCount)
                    .Where(dungeonId => dungeonId > 0)
                    .Select(dungeonId => checked((uint)dungeonId))
                    .ToArray();
                if (configured.Length == config.HatcheryTotalCount)
                    return configured;
            }

            return (uint[])HatcheryFallbackDungeonIds.Clone();
        }

        internal static uint GetHatcheryOpenCount()
        {
            var configured = Configuration.Value?.HatcheryOpenCount ?? 0;
            var hatcheryCount = GetHatcheryDungeonIds().Length;
            return configured > 0 && configured <= hatcheryCount
                ? checked((uint)configured)
                : HatcheryFallbackOpenCount;
        }

        internal static int GetHatcheryIndex(uint dungeonId)
        {
            return Array.IndexOf(GetHatcheryDungeonIds(), dungeonId);
        }

        internal static bool IsExceptCheatDungeon(uint dungeonId)
        {
            var configured = Configuration.Value?.ExceptCheatDungeonIds;
            if (configured != null && configured.Count > 0)
                return configured.Contains(checked((int)dungeonId));
            return ExceptCheatFallbackDungeonIds.Contains(dungeonId);
        }

        internal static uint GetStartDelaySeconds()
        {
            var configured = Configuration.Value?.StartDelaySeconds ?? 0;
            return configured > 0 ? checked((uint)configured) : StartDelayFallbackSeconds;
        }
        internal static uint GetPhaseBreakSeconds()
        {
            var configured = Configuration.Value?.PhaseBreakSeconds ?? 0;
            return configured > 0 ? checked((uint)configured) : PhaseBreakFallbackSeconds;
        }


        internal static uint GetShieldChargeRate(bool infectionActive)
        {
            var index = infectionActive ? 1 : 0;
            var rates = Configuration.Value?.ShieldChargeRates;
            if (rates != null && rates.Count > index && rates[index] > 0)
                return checked((uint)rates[index]);
            return infectionActive
                ? InfectionShieldChargeFallbackRate
                : NormalShieldChargeFallbackRate;
        }

        internal static uint GetPhaseRank(uint phaseIndex, uint deathCount)
        {
            var phase = Configuration.Value?.GetPhase(checked((int)phaseIndex));
            if (phase != null && phase.RankConditions.Count >= 4)
            {
                var configuredRank = phase.ResolveRank(checked((int)Math.Min(deathCount, int.MaxValue)));
                if (configuredRank >= 0)
                    return checked((uint)configuredRank);
            }

            if (deathCount <= 1)
                return 0;
            if (deathCount <= 5)
                return 1;
            if (deathCount <= 9)
                return 2;
            return 3;
        }

        internal static uint RollRewardContainer(string rewardType, uint rank)
        {
            return RollRewardContainer(0, rewardType, rank);
        }

        internal static uint RollRewardContainer(uint phaseIndex, string rewardType, uint rank)
        {
            var phase = Configuration.Value?.GetPhase(checked((int)phaseIndex));
            if (phase != null)
            {
                var state = checked((int)rank);
                var totalWeight = phase.GetRewardWeight(rewardType, state);
                if (totalWeight > 0
                    && phase.TrySelectReward(
                        rewardType,
                        state,
                        Random.Shared.Next(totalWeight),
                        out var reward)
                    && reward.ItemId > 0)
                    return checked((uint)reward.ItemId);
            }

            return SelectFallbackContainer(phaseIndex, rewardType, rank);
        }

        internal static uint SelectFallbackContainer(string rewardType, uint rank)
        {
            return SelectFallbackContainer(0, rewardType, rank);
        }

        internal static uint SelectFallbackContainer(uint phaseIndex, string rewardType, uint rank)
        {
            if (phaseIndex == 1)
            {
                if (string.Equals(rewardType, "party_card", StringComparison.OrdinalIgnoreCase))
                    return Random.Shared.Next(1000) < 955
                        ? PhaseTwoPartyCardCommonFallbackItemId
                        : PhaseTwoPartyCardRareFallbackItemId;
                if (string.Equals(rewardType, "gold", StringComparison.OrdinalIgnoreCase))
                {
                    if (rank == 2)
                        return PhaseTwoGoldRankBFallbackItemId;
                    if (rank >= 3)
                        return PhaseTwoGoldRankCFallbackItemId;
                    return PhaseTwoGoldFallbackItemId;
                }
                if (string.Equals(rewardType, "squad_item", StringComparison.OrdinalIgnoreCase))
                {
                    var roll = Random.Shared.Next(100);
                    for (var index = 0; index < PhaseTwoSquadFallbackWeights.Length; index++)
                    {
                        roll -= PhaseTwoSquadFallbackWeights[index];
                        if (roll < 0)
                            return PhaseTwoSquadFallbackItemIds[index];
                    }
                }
            }

            if (string.Equals(rewardType, "party_card", StringComparison.OrdinalIgnoreCase))
                return PartyCardFallbackItemId;
            if (string.Equals(rewardType, "gold", StringComparison.OrdinalIgnoreCase))
            {
                if (rank == 2)
                    return GoldRankBFallbackItemId;
                if (rank >= 3)
                    return GoldRankCFallbackItemId;
                return GoldFallbackItemId;
            }
            if (string.Equals(rewardType, "squad_item", StringComparison.OrdinalIgnoreCase))
            {
                var roll = Random.Shared.Next(100);
                if (roll < 92)
                    return SquadCommonFallbackItemId;
                if (roll < 95)
                    return SquadRareFallbackItemId;
                return SquadSpecialFallbackItemId;
            }

            throw new InvalidOperationException($"Unsupported Anton raid reward type: {rewardType}");
        }
        private static RaidBuffFile LoadSituationConfiguration()
        {
            try
            {
                var config = RaidBuffFile.Parse(PvfArchiveAccessor.ReadText(SituationConfigPath));
                FileLogger.Log($"[AntonRaidRewardProvider] loaded situation buffs={config.Buffs.Count} monsters={config.Monsters.Count}");
                return config;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AntonRaidRewardProvider] situation load failed: {ex.Message}");
                return BuildSituationFallback();
            }
        }

        private static RaidBuffFile BuildSituationFallback()
        {
            var file = new RaidBuffFile();
            file.Buffs.Add(CreateBuff("ATTACK BONUS", "RAID", 420, 30, 15));
            file.Buffs.Add(CreateBuff("INVINCIBLE", "PARTY", 360, 30, 0));
            file.Buffs.Add(CreateBuff("RESTORE", "PARTY", 240, 60, 7));
            file.Buffs.Add(CreateBuff("INCREASE TIME", "RAID", 7200, 10, 300));
            file.Buffs.Add(CreateBuff("INCREASE COIN", "PARTY", 300, 10, 4));

            file.Monsters.Add(CreateMonster(210, 65664, new[] { 58529 }, Array.Empty<int>()));
            file.Monsters.Add(CreateMonster(211, 65664, new[] { 58529 }, Array.Empty<int>()));
            file.Monsters.Add(CreateMonster(212, 0, new[] { 59055, 56635 }, Array.Empty<int>()));
            file.Monsters.Add(CreateMonster(214, 0, new[] { 59055, 56635 }, Array.Empty<int>()));
            file.Monsters.Add(CreateMonster(213, 64062, new[] { 56636, 58020 }, Array.Empty<int>()));
            file.Monsters.Add(CreateMonster(215, 64062, new[] { 56636, 58020 }, Array.Empty<int>()));
            file.Monsters.Add(CreateMonster(218, 69311, Array.Empty<int>(), Array.Empty<int>()));
            file.Monsters.Add(CreateMonster(221, 0, new[] { 58531 }, new[] { 58533, 58532 }));
            file.Monsters.Add(CreateMonster(222, 0, new[] { 58027 }, new[] { 58533, 58532 }));
            file.Monsters.Add(CreateMonster(223, 0, new[] { 56638 }, new[] { 58533, 58532 }));
            file.Monsters.Add(CreateMonster(224, 0, new[] { 56764 }, new[] { 58533, 58532 }));
            file.Monsters.Add(CreateMonster(219, 64061, new[] { 59058, 58024, 58021 }, Array.Empty<int>()));
            return file;
        }

        private static RaidBuffDefinition CreateBuff(
            string type,
            string target,
            int cooldownSeconds,
            int durationSeconds,
            int effectValue)
        {
            var definition = new RaidBuffDefinition { TypeName = type };
            definition.Entries.Add(new RaidBuffEntry
            {
                Target = target,
                CooldownSeconds = cooldownSeconds,
                DurationSeconds = durationSeconds,
                EffectValue = effectValue,
            });
            return definition;
        }

        private static RaidMonsterDefinition CreateMonster(int dungeonId, int bossId, int[] namedIds, int[] infectIds)
        {
            var definition = new RaidMonsterDefinition { DungeonId = dungeonId, BossMonsterId = bossId };
            definition.NamedMonsterIds.AddRange(namedIds);
            definition.InfectMonsterIds.AddRange(infectIds);
            return definition;
        }

        private static RaidEtcFile LoadConfiguration()
        {
            try
            {
                var config = RaidEtcFile.Parse(PvfArchiveAccessor.ReadText(ConfigPath));
                FileLogger.Log(
                    $"[AntonRaidRewardProvider] loaded ranks={config.RankConditions.Count} " +
                    $"phases={config.Phases.Count} rewards={config.Phases.Sum(phase => phase.StateRewards.Count)}");
                return config;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AntonRaidRewardProvider] load failed, using fallback: {ex.Message}");
                return null;
            }
        }
    }
}