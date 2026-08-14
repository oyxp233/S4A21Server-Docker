using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using System;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct DungeonParticipantEquipmentBonusFacts
    {
        internal DungeonParticipantEquipmentBonusFacts(
            bool hasEquippedAvatar,
            bool hasEquippedCreature)
        {
            HasEquippedAvatar = hasEquippedAvatar;
            HasEquippedCreature = hasEquippedCreature;
        }

        internal bool HasEquippedAvatar { get; }
        internal bool HasEquippedCreature { get; }
    }

    internal readonly struct DungeonParticipantExperienceBonusSnapshot
    {
        internal DungeonParticipantExperienceBonusSnapshot(
            int partyMemberCount,
            bool partyHasEquippedAvatar,
            bool hasEquippedCreature,
            int channelId = 0,
            int channelType = 0,
            double channelExperienceBonusRate = 0.0)
        {
            IsCaptured = true;
            PartyMemberCount = Math.Max(1, Math.Min(4, partyMemberCount));
            PartyHasEquippedAvatar = partyHasEquippedAvatar;
            HasEquippedCreature = hasEquippedCreature;
            ChannelId = channelId;
            ChannelType = channelType;
            ChannelExperienceBonusRate = NormalizeRate(
                channelExperienceBonusRate);
        }

        internal static DungeonParticipantExperienceBonusSnapshot None =>
            new DungeonParticipantExperienceBonusSnapshot(
                partyMemberCount: 1,
                partyHasEquippedAvatar: false,
                hasEquippedCreature: false);

        internal bool IsCaptured { get; }
        internal int PartyMemberCount { get; }
        internal bool PartyHasEquippedAvatar { get; }
        internal bool HasEquippedCreature { get; }
        internal int ChannelId { get; }
        internal int ChannelType { get; }
        internal double ChannelExperienceBonusRate { get; }

        private static double NormalizeRate(double value)
            => value > 0.0 && value <= 1.0
                && !double.IsNaN(value)
                && !double.IsInfinity(value)
                ? value
                : 0.0;
    }

    internal readonly struct DungeonClearParticipantBonusResult
    {
        internal DungeonClearParticipantBonusResult(
            uint avatarBonusExperience,
            uint creatureBonusExperience)
        {
            AvatarBonusExperience = avatarBonusExperience;
            CreatureBonusExperience = creatureBonusExperience;
        }

        internal uint AvatarBonusExperience { get; }
        internal uint CreatureBonusExperience { get; }
        internal uint TotalBonusExperience => AddSaturating(
            AvatarBonusExperience,
            CreatureBonusExperience);

        private static uint AddSaturating(uint left, uint right)
        {
            var total = (ulong)left + right;
            return total >= uint.MaxValue ? uint.MaxValue : (uint)total;
        }
    }

    internal static class DungeonParticipantExperienceBonusSnapshotCapture
    {
        internal static bool TryCaptureOwned(
            Guid sessionId,
            int characterId,
            out DungeonParticipantEquipmentBonusFacts facts)
        {
            facts = default;
            if (!InventoryContext.TryGetOwnedLease(
                    sessionId,
                    characterId,
                    out var lease))
            {
                return false;
            }

            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        sessionId,
                        characterId))
                {
                    return false;
                }

                facts = CaptureInventory(lease.Inventory);
                return true;
            }
        }

        internal static DungeonParticipantEquipmentBonusFacts CaptureInventory(
            InventoryService inventory)
        {
            if (inventory == null)
                return default;

            var hasEquippedAvatar = false;
            for (var slot = (short)EquipmentType.HatAvatar;
                 slot <= (short)EquipmentType.WeaponAvatar;
                 slot++)
            {
                var item = inventory.GetItem(
                    InventoryListType.Equipment,
                    slot);
                if (item?.ItemKind == ItemCore.KindAvatar)
                {
                    hasEquippedAvatar = true;
                    break;
                }
            }

            var hasEquippedCreature = PetInventoryAccessor.TryGetEquippedCreature(
                inventory,
                out _,
                out _);
            return new DungeonParticipantEquipmentBonusFacts(
                hasEquippedAvatar,
                hasEquippedCreature);
        }
    }
}
