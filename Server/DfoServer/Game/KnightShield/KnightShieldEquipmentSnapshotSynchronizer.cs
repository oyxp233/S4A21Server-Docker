using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Game.KnightShield
{
    public static class KnightShieldEquipmentSnapshotSynchronizer
    {
        public const byte SupportWeaponSlot = (byte)EquipmentType.SupportWeapon;

        public static void Apply(
            byte job,
            int growType,
            UserInfoAdditionSnapshot addition,
            KnightShieldDeckSnapshot deck)
        {
            if (addition == null
                || deck == null
                || !KnightShieldDataProvider.IsEligibleCharacter(job))
                return;

            addition.EquippedEntries.RemoveAll(entry => entry.Slot == SupportWeaponSlot);

            var shieldItemId = deck.MainShieldItemId;
            if (shieldItemId == 0)
                return;
            if (!KnightShieldDataProvider.IsCatalogShield(growType, shieldItemId))
            {
                FileLogger.Log(
                    $"[KnightShield] skip invalid persisted main shield in subtype1: "
                    + $"item={shieldItemId} grow={growType}");
                return;
            }

            addition.EquippedEntries.Add(new EquippedEntrySnapshot
            {
                Slot = SupportWeaponSlot,
                Core = ItemCore.Create(ItemCore.KindEquipment, shieldItemId),
            });
            addition.EquippedEntries.Sort((left, right) => left.Slot.CompareTo(right.Slot));
        }
    }
}
