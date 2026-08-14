namespace DfoServer.Game.Inventory
{
    internal static class PetInventoryLayout
    {
        internal const short CreatureEquipSlot = 24;
        internal const short ArtifactRedEquipSlot = 25;
        internal const short ArtifactBlueEquipSlot = 26;
        internal const short ArtifactGreenEquipSlot = 27;
        // 旧库兼容：240 是穿戴宠物映射槽，协议和在线模型都不把它当作真实宠物栏槽位。
        internal const short EquippedStorageSlotOffset = 216;
        internal const short CreatureEquippedStorageSlot = CreatureEquipSlot + EquippedStorageSlotOffset;

        internal static readonly short[] ArtifactEquipSlots =
        {
            ArtifactRedEquipSlot,
            ArtifactBlueEquipSlot,
            ArtifactGreenEquipSlot,
        };

        internal static bool IsPetEquipmentSlot(int slot)
        {
            return slot == CreatureEquipSlot || IsArtifactEquipSlot(slot);
        }

        internal static bool IsServerStorageSlot(int slot)
        {
            return slot == CreatureEquippedStorageSlot || IsArtifactStorageSlot(slot);
        }

        internal static bool IsArtifactEquipSlot(int slot)
        {
            return slot == ArtifactRedEquipSlot
                || slot == ArtifactBlueEquipSlot
                || slot == ArtifactGreenEquipSlot;
        }

        internal static bool IsArtifactStorageSlot(int slot)
        {
            return IsArtifactEquipSlot(slot - EquippedStorageSlotOffset);
        }

        internal static int ToEquipmentStorageSlot(int equipSlot)
        {
            return IsArtifactEquipSlot(equipSlot)
                ? equipSlot + EquippedStorageSlotOffset
                : -1;
        }
    }
}
