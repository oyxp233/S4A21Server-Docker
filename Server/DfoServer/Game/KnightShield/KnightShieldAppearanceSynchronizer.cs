using System.Collections.Generic;
using DfoServer.Game.Characters;

namespace DfoServer.Game.KnightShield
{
    internal static class KnightShieldAppearanceSynchronizer
    {
        internal static CharacterAppearanceEntry[] Apply(
            IEnumerable<CharacterAppearanceEntry> appearanceEntries,
            byte job,
            int growType,
            KnightShieldDeckSnapshot deck)
        {
            var result = appearanceEntries != null
                ? new List<CharacterAppearanceEntry>(appearanceEntries)
                : new List<CharacterAppearanceEntry>();
            if (deck == null || !KnightShieldDataProvider.IsEligibleCharacter(job))
                return result.ToArray();

            var supportWeaponSlot = KnightShieldEquipmentSnapshotSynchronizer.SupportWeaponSlot;
            result.RemoveAll(entry => entry != null && entry.Slot == supportWeaponSlot);

            var shieldItemId = deck.MainShieldItemId;
            if (shieldItemId == 0
                || !KnightShieldDataProvider.IsCatalogShield(growType, shieldItemId))
                return result.ToArray();

            result.Add(new CharacterAppearanceEntry(
                supportWeaponSlot,
                shieldItemId,
                4,
                new byte[4],
                0,
                0,
                0u,
                0));
            result.Sort((left, right) => left.Slot.CompareTo(right.Slot));
            return result.ToArray();
        }
    }
}
