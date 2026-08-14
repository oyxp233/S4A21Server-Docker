namespace DfoServer.Game.SelectCharacter
{
    public static class CharacterSlotPolicy
    {
        // Mirrors the previous roster fallback when no get_userinfo_template row exists.
        public const ushort DefaultSlotLimit = 17;

        public static ushort ResolveSlotLimit(ushort? primaryLimit, ushort? secondaryLimit)
        {
            if (primaryLimit.HasValue && primaryLimit.Value > 0)
                return primaryLimit.Value;

            if (secondaryLimit.HasValue && secondaryLimit.Value > 0)
                return secondaryLimit.Value;

            return DefaultSlotLimit;
        }

        public static bool HasAvailableSlot(int currentCharacterCount, ushort? primaryLimit, ushort? secondaryLimit)
        {
            if (currentCharacterCount < 0)
                return false;

            return currentCharacterCount < ResolveSlotLimit(primaryLimit, secondaryLimit);
        }
    }
}
