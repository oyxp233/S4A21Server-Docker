using DfoServer.GameWorld;
using System;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct DungeonMonsterExperienceContext
    {
        internal DungeonMonsterExperienceContext(
            int characterLevel,
            int monsterLevel,
            int difficulty,
            int monsterKind,
            bool isNamedMonster,
            int partyMemberCount,
            double partyEventBonusRate = 0.0,
            double memberPenaltyRate = 1.0)
        {
            CharacterLevel = characterLevel;
            MonsterLevel = monsterLevel;
            Difficulty = difficulty;
            MonsterKind = monsterKind;
            IsNamedMonster = isNamedMonster;
            PartyMemberCount = Math.Max(1, partyMemberCount);
            PartyEventBonusRate = NormalizeNonnegative(partyEventBonusRate);
            MemberPenaltyRate = NormalizeNonnegative(memberPenaltyRate);
        }

        internal int CharacterLevel { get; }
        internal int MonsterLevel { get; }
        internal int Difficulty { get; }
        internal int MonsterKind { get; }
        internal bool IsNamedMonster { get; }
        internal int PartyMemberCount { get; }
        internal double PartyEventBonusRate { get; }
        internal double MemberPenaltyRate { get; }

        private static double NormalizeNonnegative(double value)
            => value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value)
                ? value
                : 0.0;
    }

    internal readonly struct DungeonClearExperienceContext
    {
        internal DungeonClearExperienceContext(
            int characterLevel,
            int difficulty,
            int totalKilledMonsterCount,
            int partyMemberCount,
            double partyEventBonusRate = 0.0,
            double memberPenaltyRate = 1.0)
        {
            CharacterLevel = characterLevel;
            Difficulty = difficulty;
            TotalKilledMonsterCount = Math.Max(0, totalKilledMonsterCount);
            PartyMemberCount = Math.Max(1, partyMemberCount);
            PartyEventBonusRate = NormalizeNonnegative(partyEventBonusRate);
            MemberPenaltyRate = NormalizeNonnegative(memberPenaltyRate);
        }

        internal int CharacterLevel { get; }
        internal int Difficulty { get; }
        internal int TotalKilledMonsterCount { get; }
        internal int PartyMemberCount { get; }
        internal double PartyEventBonusRate { get; }
        internal double MemberPenaltyRate { get; }

        private static double NormalizeNonnegative(double value)
            => value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value)
                ? value
                : 0.0;
    }

    internal readonly struct DungeonBaseExperienceResult
    {
        internal DungeonBaseExperienceResult(
            uint sharedBaseExperience,
            uint participantBaseExperience)
        {
            SharedBaseExperience = sharedBaseExperience;
            ParticipantBaseExperience = participantBaseExperience;
        }

        internal uint SharedBaseExperience { get; }
        internal uint ParticipantBaseExperience { get; }
    }

    internal static class DungeonExperienceCalculator
    {
        internal static DungeonBaseExperienceResult CalculateStandardMonster(
            DungeonExperienceDefinition definition,
            DungeonMonsterExperienceContext context)
        {
            if (definition == null || !definition.UsesStandardFormula)
                return default;

            var mobReward = MonsterRewardTable.GetMobReward(context.MonsterLevel);
            if (mobReward <= 0)
                return default;

            var partyRate = definition.GetPartyMemberRate(
                    context.PartyMemberCount)
                + context.PartyEventBonusRate;
            var namedRate = context.IsNamedMonster ? 3.0 : 1.0;
            var sharedBase = FloorToUInt32(
                mobReward
                / 2.0
                * partyRate
                * definition.GetDifficultyRate(context.Difficulty)
                * definition.ExperienceWeight
                * definition.GetMonsterKindRate(context.MonsterKind)
                * namedRate);
            var participantBase = FloorToUInt32(
                sharedBase
                * GetLevelPenalty(context.CharacterLevel, context.MonsterLevel)
                * context.MemberPenaltyRate
                / context.PartyMemberCount);
            return new DungeonBaseExperienceResult(sharedBase, participantBase);
        }

        // Risk/tower/altar definitions need separate reverse-engineered rules.
        // Until those are closed, preserve their pre-existing base calculation
        // without allowing it back into the standard model.
        internal static DungeonBaseExperienceResult
            CalculateNonStandardCompatibilityMonster(
                DungeonExperienceDefinition definition,
                DungeonMonsterExperienceContext context)
        {
            if (definition == null
                || !definition.IsAvailable
                || definition.Kind == DungeonExperienceDefinitionKind.Standard
                || definition.Kind == DungeonExperienceDefinitionKind.Unavailable)
            {
                return default;
            }

            var mobReward = MonsterRewardTable.GetMobReward(context.MonsterLevel);
            if (mobReward <= 0)
                return default;

            var namedRate = context.IsNamedMonster ? 3.0 : 1.0;
            var weightedMobReward = FloorToUInt32(
                mobReward * definition.ExperienceWeight);
            var sharedBase = FloorToUInt32(
                weightedMobReward
                * definition.GetDifficultyRate(context.Difficulty)
                * definition.LegacyMonsterOverallRate
                * namedRate);
            var participantBase = FloorToUInt32(
                sharedBase
                * GetLevelPenalty(context.CharacterLevel, context.MonsterLevel));
            return new DungeonBaseExperienceResult(sharedBase, participantBase);
        }

        internal static DungeonBaseExperienceResult CalculateStandardClear(
            DungeonExperienceDefinition definition,
            DungeonClearExperienceContext context)
        {
            if (definition == null
                || !definition.UsesStandardFormula
                || context.TotalKilledMonsterCount <= 0)
            {
                return default;
            }

            var mobReward = MonsterRewardTable.GetMobReward(context.CharacterLevel);
            if (mobReward <= 0)
                return default;

            var partyRate = definition.GetPartyMemberRate(
                    context.PartyMemberCount)
                + context.PartyEventBonusRate;
            var sharedBase = FloorToUInt32(
                mobReward
                * (double)context.TotalKilledMonsterCount
                / 2.0
                * partyRate
                * definition.GetDifficultyRate(context.Difficulty)
                * definition.ExperienceWeight);
            var participantBase = FloorToUInt32(
                sharedBase
                * GetLevelPenalty(
                    context.CharacterLevel,
                    definition.StandardLevel)
                * context.MemberPenaltyRate
                / context.PartyMemberCount);
            return new DungeonBaseExperienceResult(sharedBase, participantBase);
        }

        // The A14 clear-result packet presents part of the already-granted
        // participant base as a party contribution. It is a display breakdown,
        // not an additional experience grant.
        internal static uint CalculatePartyClearBreakdown(
            DungeonExperienceDefinition definition,
            uint participantBaseExperience,
            int partyMemberCount,
            double partyEventBonusRate = 0.0)
        {
            if (definition == null
                || !definition.UsesStandardFormula
                || participantBaseExperience == 0
                || partyMemberCount <= 1
                || partyEventBonusRate < 0.0
                || double.IsNaN(partyEventBonusRate)
                || double.IsInfinity(partyEventBonusRate))
            {
                return 0;
            }

            var partyRate = definition.GetPartyMemberRate(partyMemberCount)
                + partyEventBonusRate;
            if (partyRate <= 1.0
                || double.IsNaN(partyRate)
                || double.IsInfinity(partyRate))
            {
                return 0;
            }

            var nonPartyShare = FloorToUInt32(
                participantBaseExperience / partyRate);
            return nonPartyShare >= participantBaseExperience
                ? 0
                : participantBaseExperience - nonPartyShare;
        }

        internal static DungeonClearParticipantBonusResult
            CalculateClearParticipantBonuses(
                DungeonExperienceDefinition definition,
                uint clearBaseExperience,
                DungeonParticipantExperienceBonusSnapshot snapshot)
        {
            if (definition == null
                || !definition.IsAvailable
                || clearBaseExperience == 0
                || !snapshot.IsCaptured)
            {
                return default;
            }

            var bonusDefinition = definition.ClearBonusDefinition;
            if (bonusDefinition == null)
                return default;

            var avatarBonus = FloorBonusAtLeastOne(
                clearBaseExperience,
                bonusDefinition.ResolveAvatarRate(
                    snapshot.PartyMemberCount,
                    snapshot.PartyHasEquippedAvatar));
            var creatureBonus = FloorBonusAtLeastOne(
                clearBaseExperience,
                bonusDefinition.ResolveCreatureRate(
                    snapshot.HasEquippedCreature));
            return new DungeonClearParticipantBonusResult(
                avatarBonus,
                creatureBonus);
        }

        // df_game_r CParty::getClearRewardBonusExp: parameter[8] is the
        // channel rate and is calculated from the frozen clear base.
        internal static uint CalculateChannelClearBonus(
            uint clearBaseExperience,
            DungeonParticipantExperienceBonusSnapshot snapshot)
        {
            if (clearBaseExperience == 0
                || !snapshot.IsCaptured
                || snapshot.ChannelExperienceBonusRate <= 0.0
                || snapshot.ChannelExperienceBonusRate > 1.0
                || double.IsNaN(snapshot.ChannelExperienceBonusRate)
                || double.IsInfinity(snapshot.ChannelExperienceBonusRate))
            {
                return 0;
            }

            return FloorToUInt32(
                clearBaseExperience * snapshot.ChannelExperienceBonusRate);
        }

        // Channel experience is an independent kill-side component. It is
        // calculated from the frozen monster base and never compounds with
        // growth-contract or other participant bonuses.
        internal static uint CalculateChannelMonsterBonus(
            uint monsterBaseExperience,
            DungeonParticipantExperienceBonusSnapshot snapshot)
        {
            if (monsterBaseExperience == 0
                || !snapshot.IsCaptured
                || snapshot.ChannelExperienceBonusRate <= 0.0
                || snapshot.ChannelExperienceBonusRate > 1.0
                || double.IsNaN(snapshot.ChannelExperienceBonusRate)
                || double.IsInfinity(snapshot.ChannelExperienceBonusRate))
            {
                return 0;
            }

            return FloorToUInt32(
                monsterBaseExperience * snapshot.ChannelExperienceBonusRate);
        }

        // df_game_r CDataManager::BaseExpPenalty @ 0x08360914.
        internal static double GetLevelPenalty(
            int characterLevel,
            int targetLevel)
        {
            var difference = targetLevel - characterLevel;
            if (difference <= -7)
                return 0.05;
            return difference switch
            {
                -6 => 0.20,
                -5 => 0.50,
                -4 => 0.75,
                -3 or -2 or -1 or 0 => 1.00,
                1 or 2 or 3 => 1.12,
                4 or 5 => 1.00,
                6 => 0.75,
                7 => 0.70,
                8 => 0.60,
                9 => 0.50,
                _ => 0.05,
            };
        }

        internal static uint FloorToUInt32(double value)
        {
            if (value <= 0.0 || double.IsNaN(value))
                return 0;
            if (double.IsPositiveInfinity(value) || value >= uint.MaxValue)
                return uint.MaxValue;
            return (uint)Math.Floor(value);
        }

        private static uint FloorBonusAtLeastOne(uint baseExperience, double rate)
        {
            if (baseExperience == 0
                || rate <= 0.0
                || double.IsNaN(rate)
                || double.IsInfinity(rate))
            {
                return 0;
            }

            var value = FloorToUInt32(baseExperience * rate);
            return value == 0 ? 1u : value;
        }
    }
}
