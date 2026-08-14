using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using System.IO;

namespace DfoServer.Network.Builders
{
    /// <summary>
    /// Builds the USERINFO subtype 3 body consumed by the inspect-player window.
    /// </summary>
    public static class UserInfoSubtype3Builder
    {
        public const int AdornmentBlockLength = 12;
        public const int ContextBlockLength = 16;

        public static byte[] BuildNotificationBody(
            ushort targetUserId,
            UserInfoAdditionSnapshot addition,
            SkillInfoSnapshot skills,
            CharacterRecord characterRecord)
        {
            if (addition == null)
                throw new InvalidDataException(
                    "USERINFO subtype 3 requires UserInfoAddition");

            var writer = new GamePacketWriter();
            writer.WriteByte(3);
            writer.WriteUInt16(1);
            writer.WriteUInt16(targetUserId);

            WriteCharacterStats(writer, addition);
            WriteEquipment(writer, addition);

            // The client consumes these three u32 values before both skill pages.
            writer.WriteUInt32(addition.CloneTitleItemId);
            writer.WriteUInt32(addition.NameTagItemId);
            writer.WriteUInt32(addition.NameTagExpireTime);

            writer.WriteByte(addition.SkillTreeIndex);
            WriteSkillPage(writer, skills, 0);
            WriteSkillPage(writer, skills, 1);
            writer.WriteByte(addition.EquippedCreatureLevel);

            // Inspect/PvP context: 3*u32 + 4*u8. Unknown persisted counters use
            // their neutral wire value instead of shifting the following trailer.
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);

            WritePostContextFields(
                writer,
                addition,
                characterRecord?.Subtype0Tail);
            return writer.ToArray();
        }

        private static void WritePostContextFields(
            GamePacketWriter writer,
            UserInfoAdditionSnapshot addition,
            UserInfoMinimumTailSnapshot minimumTail)
        {
            writer.WriteUInt32(0); // help/abuse ratio
            writer.WriteUInt16(0); // personal power-war point
            writer.WriteUInt32(addition.GuildPowerWar);
            writer.WriteDstr(minimumTail?.GuildNameBytes);
            writer.WriteByte(minimumTail?.GuildLevel ?? 0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(addition.ManageLevel);
            writer.WriteByte(addition.FlagByte);
            writer.WriteUInt32(
                (uint)addition.SpecialRewardQuestIds.Count);
            foreach (var questId in addition.SpecialRewardQuestIds)
                writer.WriteUInt32(questId);
            writer.WriteUInt16(addition.QuestShopCount);
            writer.WriteByte(0); // optional inspection record count
        }

        private static void WriteCharacterStats(
            GamePacketWriter writer,
            UserInfoAdditionSnapshot addition)
        {
            writer.WriteUInt32(addition.CharacExp);
            writer.WriteInt32(83);
            writer.WriteUInt32(addition.StatHpMax);
            writer.WriteUInt32(addition.StatMpMax);
            writer.WriteInt16(addition.StatPhysicalAttack);
            writer.WriteInt16(addition.StatPhysicalDefense);
            writer.WriteInt16(addition.StatMagicalAttack);
            writer.WriteInt16(addition.StatMagicalDefense);
            writer.WriteInt16(addition.StatFireResistance);
            writer.WriteInt16(addition.StatWaterResistance);
            writer.WriteInt16(addition.StatDarkResistance);
            writer.WriteInt16(addition.StatLightResistance);
            for (var index = 0; index < 17; index++)
                writer.WriteUInt16(0);
            writer.WriteUInt32(addition.StatInventoryLimit);
            writer.WriteUInt16(addition.StatHpRegenSpeed);
            writer.WriteUInt16(addition.StatMpRegenSpeed);
            writer.WriteUInt32(addition.StatMoveSpeed);
            writer.WriteUInt16(addition.StatAttackSpeed);
            writer.WriteUInt16(addition.StatCastSpeed);
            writer.WriteUInt16(addition.StatHitRecovery);
            writer.WriteUInt16(addition.StatJumpPower);
            writer.WriteUInt32(addition.StatWeight);
            writer.WriteByte(addition.StatLevel);
            writer.WriteByte(addition.ExEquipSlotStat);
        }

        private static void WriteEquipment(
            GamePacketWriter writer,
            UserInfoAdditionSnapshot addition)
        {
            writer.WriteByte((byte)addition.EquippedEntries.Count);
            foreach (var entry in addition.EquippedEntries)
            {
                if (entry.Core == null)
                {
                    throw new InvalidDataException(
                        $"USERINFO subtype 3 slot {entry.Slot}: " +
                        "ItemCore is unavailable");
                }

                ItemListProtocolWriter.WriteNoti2EquippedEntry(
                    writer,
                    entry.Slot,
                    entry.Core,
                    addition.GetAvatarDetail(entry.Core),
                    addition.GetCreatureDetail(entry.Core));
            }
        }

        private static void WriteSkillPage(
            GamePacketWriter writer,
            SkillInfoSnapshot skills,
            int pageIndex)
        {
            if (skills == null || pageIndex >= skills.Pages.Count)
            {
                writer.WriteByte(0);
                return;
            }

            var page = skills.Pages[pageIndex];
            var count = 0;
            foreach (var entry in page.Entries)
            {
                if (entry.Level > 0)
                    count++;
            }

            writer.WriteByte((byte)count);
            foreach (var entry in page.Entries)
            {
                if (entry.Level == 0)
                    continue;
                writer.WriteUInt16(entry.SkillId);
                writer.WriteByte(entry.Level);
            }
        }
    }
}
