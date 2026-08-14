using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.SelectCharacter
{
    public sealed class UserInfoAdditionSnapshot
    {
        
        public uint CharacExp { get; set; }

        
        public uint StatHpMax { get; set; }
        public uint StatMpMax { get; set; }
        public short StatPhysicalAttack { get; set; }
        public short StatPhysicalDefense { get; set; }
        public short StatMagicalAttack { get; set; }
        public short StatMagicalDefense { get; set; }
        public short StatFireResistance { get; set; }
        public short StatWaterResistance { get; set; }
        public short StatDarkResistance { get; set; }
        public short StatLightResistance { get; set; }
        
        
        public uint StatInventoryLimit { get; set; }
        public ushort StatHpRegenSpeed { get; set; }
        public ushort StatMpRegenSpeed { get; set; }
        public uint StatMoveSpeed { get; set; }
        public ushort StatAttackSpeed { get; set; }
        public ushort StatCastSpeed { get; set; }
        public ushort StatHitRecovery { get; set; }
        public ushort StatJumpPower { get; set; }
        public uint StatWeight { get; set; }
        public byte StatLevel { get; set; }

        
        public byte ExEquipSlotStat { get; set; }

        
        public List<EquippedEntrySnapshot> EquippedEntries { get; } = new List<EquippedEntrySnapshot>();

        internal Dictionary<int, AvatarDetail> AvatarDetails { get; } = new Dictionary<int, AvatarDetail>();

        internal Dictionary<int, CreatureDetail> CreatureDetails { get; } = new Dictionary<int, CreatureDetail>();

        internal void SetAvatarDetail(int itemUid, AvatarDetail detail)
        {
            if (itemUid <= 0 || detail == null)
                return;

            AvatarDetails[itemUid] = detail;
        }

        internal AvatarDetail GetAvatarDetail(ItemCore core)
        {
            if (core == null || core.ItemKind != ItemCore.KindAvatar || core.Value <= 0)
                return null;

            AvatarDetails.TryGetValue(core.Value, out var detail);
            return detail;
        }

        internal void SetCreatureDetail(int creatureKey, CreatureDetail detail)
        {
            if (creatureKey <= 0 || detail == null)
                return;

            CreatureDetails[creatureKey] = detail;
        }

        internal CreatureDetail GetCreatureDetail(ItemCore core)
        {
            if (core == null || core.ItemKind != ItemCore.KindCreature || core.Value <= 0)
                return null;

            CreatureDetails.TryGetValue(core.Value, out var detail);
            return detail;
        }
        public uint CloneTitleItemId { get; set; }

        
        public uint NameTagItemId { get; set; }
        public uint NameTagExpireTime { get; set; }

        
        public byte SkillTreeIndex { get; set; } = Skills.SkillTreeExpansionState.LockedWireValue;

        
        public byte EquippedCreatureLevel { get; set; }

        
        public List<DimensionEntrySnapshot> Dimensions { get; } = new List<DimensionEntrySnapshot>();
        public byte DimFlag1 { get; set; }
        public byte DimFlag2 { get; set; }
        public byte DimFlag3 { get; set; }
        public byte DimFlag4 { get; set; }

        
        public List<PvpResultEntrySnapshot> PvpResults { get; } = new List<PvpResultEntrySnapshot>();

        
        public byte ManageLevel { get; set; }
        // Historical field name was "AbuseValues". On the client this slot is
        // a u32 count followed by quest ids used to rebuild QST effects.
        public List<uint> SpecialRewardQuestIds { get; } = new List<uint>();
        public byte FlagByte { get; set; }
        public uint GuildPowerWar { get; set; }
        public uint ServerTimestamp { get; set; }
        public ushort QuestShopCount { get; set; }
        public uint Progress1 { get; set; }
        public uint Progress2 { get; set; }
    }

    public sealed class EquippedEntrySnapshot
    {
        public short Slot { get; set; }

        internal ItemCore Core { get; set; }
    }

    public sealed class DimensionEntrySnapshot
    {
        public uint Key { get; set; }
        public byte Val1 { get; set; }
        public byte Val2 { get; set; }
    }

    public sealed class PvpResultEntrySnapshot
    {
        public uint Value32 { get; set; }
        public ushort Value16A { get; set; }
        public ushort Value16B { get; set; }
    }
}
