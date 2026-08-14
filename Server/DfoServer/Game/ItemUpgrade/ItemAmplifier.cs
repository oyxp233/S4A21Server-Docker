using PvfLib;

namespace DfoServer.Game.ItemUpgrade
{
    public enum AmplifyAttributeType
    {
        None = 0,
        Vitality = 1,
        Spirit = 2,
        Strength = 3,
        Intelligence = 4,
    }

    public static class ItemAmplifier
    {
        public static ushort CalculateInitialAttributeValue(int rarity, AmplifyAttributeType attributeType)
        {
            if (attributeType == AmplifyAttributeType.None)
                return 0;

            return (ushort)ItemUpgradeTableProvider.CalculateInitialAmplifyValue(rarity, ToOptionType(attributeType));
        }

        public static int GetUpgradeAttributeBonus(int currentUpgradeLevel)
        {
            return ItemUpgradeTableProvider.CalculateAmplifyBonusConst(currentUpgradeLevel);
        }

        private static AmplifyOptionType ToOptionType(AmplifyAttributeType attributeType)
        {
            switch (attributeType)
            {
                case AmplifyAttributeType.Vitality:
                    return AmplifyOptionType.PhysicalDefense;
                case AmplifyAttributeType.Spirit:
                    return AmplifyOptionType.MagicalDefense;
                case AmplifyAttributeType.Strength:
                    return AmplifyOptionType.PhysicalAttack;
                case AmplifyAttributeType.Intelligence:
                    return AmplifyOptionType.MagicalAttack;
                default:
                    return AmplifyOptionType.None;
            }
        }
    }
}
