using System;

namespace DfoServer.GameWorld
{
    // Version-owned immutable rates. DungeonInstance freezes this definition
    // through DungeonExperienceDefinition; runtime code only consumes it.
    internal sealed class DungeonClearExperienceBonusDefinition
    {
        internal static DungeonClearExperienceBonusDefinition A14 { get; } =
            new DungeonClearExperienceBonusDefinition(
                soloAvatarRate: 0.02,
                partyAvatarRate: 0.05,
                equippedCreatureRate: 0.05);

        internal DungeonClearExperienceBonusDefinition(
            double soloAvatarRate,
            double partyAvatarRate,
            double equippedCreatureRate)
        {
            SoloAvatarRate = RequireRate(soloAvatarRate, nameof(soloAvatarRate));
            PartyAvatarRate = RequireRate(partyAvatarRate, nameof(partyAvatarRate));
            EquippedCreatureRate = RequireRate(
                equippedCreatureRate,
                nameof(equippedCreatureRate));
        }

        internal double SoloAvatarRate { get; }
        internal double PartyAvatarRate { get; }
        internal double EquippedCreatureRate { get; }

        internal double ResolveAvatarRate(
            int partyMemberCount,
            bool partyHasEquippedAvatar)
        {
            if (!partyHasEquippedAvatar)
                return 0.0;
            return partyMemberCount > 1 ? PartyAvatarRate : SoloAvatarRate;
        }

        internal double ResolveCreatureRate(bool hasEquippedCreature) =>
            hasEquippedCreature ? EquippedCreatureRate : 0.0;

        private static double RequireRate(double value, string parameterName)
        {
            if (value < 0.0 || double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);
            return value;
        }
    }
}
