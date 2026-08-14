using System;
using System.IO;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class UserInfoSubtype1Builder
    {
        public static byte[] BuildFromSnapshot(UserInfoAdditionSnapshot a, SkillInfoSnapshot skills)
        {
            if (a == null)
                throw new ArgumentNullException(nameof(a));

            var w = new GamePacketWriter();

            w.WriteUInt32(a.CharacExp);

            w.WriteInt32(83);
            w.WriteUInt32(a.StatHpMax);
            w.WriteUInt32(a.StatMpMax);
            w.WriteInt16(a.StatPhysicalAttack);
            w.WriteInt16(a.StatPhysicalDefense);
            w.WriteInt16(a.StatMagicalAttack);
            w.WriteInt16(a.StatMagicalDefense);
            w.WriteInt16(a.StatFireResistance);
            w.WriteInt16(a.StatWaterResistance);
            w.WriteInt16(a.StatDarkResistance);
            w.WriteInt16(a.StatLightResistance);

            for (int i = 0; i < 17; i++)
                w.WriteUInt16(0);
            w.WriteUInt32(a.StatInventoryLimit);
            w.WriteUInt16(a.StatHpRegenSpeed);
            w.WriteUInt16(a.StatMpRegenSpeed);
            w.WriteUInt32(a.StatMoveSpeed);
            w.WriteUInt16(a.StatAttackSpeed);
            w.WriteUInt16(a.StatCastSpeed);
            w.WriteUInt16(a.StatHitRecovery);
            w.WriteUInt16(a.StatJumpPower);
            w.WriteUInt32(a.StatWeight);
            w.WriteByte(a.StatLevel);

            w.WriteByte(a.ExEquipSlotStat);

            w.WriteByte((byte)a.EquippedEntries.Count);
            foreach (var e in a.EquippedEntries)
            {
                var core = e.Core;
                if (core == null)
                    throw new InvalidDataException(
                        $"[UserInfoSubtype1Builder] slot {e.Slot}: ItemCore 未初始化，不能写入 subtype1 装备 entry。");

                ItemListProtocolWriter.WriteNoti2EquippedEntry(
                    w,
                    e.Slot,
                    core,
                    a.GetAvatarDetail(core),
                    a.GetCreatureDetail(core));
            }

            w.WriteUInt32(a.CloneTitleItemId);

            w.WriteUInt32(a.NameTagItemId);
            w.WriteUInt32(a.NameTagExpireTime);

            w.WriteByte(a.SkillTreeIndex);
            WriteSkillPage(w, skills, 0);
            WriteSkillPage(w, skills, 1);

            w.WriteByte(a.EquippedCreatureLevel);

            w.WriteByte((byte)a.Dimensions.Count);
            foreach (var d in a.Dimensions)
            {
                w.WriteUInt32(d.Key);
                w.WriteByte(d.Val1);
                w.WriteByte(d.Val2);
            }
            w.WriteByte(a.DimFlag1);
            w.WriteByte(a.DimFlag2);
            w.WriteByte(a.DimFlag3);
            w.WriteByte(a.DimFlag4);

            w.WriteByte((byte)a.PvpResults.Count);
            foreach (var p in a.PvpResults)
            {
                w.WriteUInt32(p.Value32);
                w.WriteUInt16(p.Value16A);
                w.WriteUInt16(p.Value16B);
            }

            w.WriteByte(a.ManageLevel);

            w.WriteUInt32((uint)a.SpecialRewardQuestIds.Count);
            foreach (var questId in a.SpecialRewardQuestIds)
                w.WriteUInt32(questId);

            w.WriteByte(a.FlagByte);

            w.WriteUInt32(a.GuildPowerWar);
            w.WriteUInt32(a.ServerTimestamp);
            w.WriteUInt16(a.QuestShopCount);
            w.WriteUInt32(a.Progress1);
            w.WriteUInt32(a.Progress2);

            return w.ToArray();
        }

        private static void WriteSkillPage(GamePacketWriter w, SkillInfoSnapshot skills, int pageIndex)
        {
            if (skills == null || pageIndex >= skills.Pages.Count)
            {
                w.WriteByte(0);
                return;
            }

            var page = skills.Pages[pageIndex];
            int count = 0;
            foreach (var e in page.Entries)
            {
                if (e.Level > 0)
                    count++;
            }

            w.WriteByte((byte)count);
            foreach (var e in page.Entries)
            {
                if (e.Level <= 0)
                    continue;

                w.WriteUInt16(e.SkillId);
                w.WriteByte(e.Level);
            }
        }
    }
}
