using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class UserInfoSubtype1Builder
    {
        public static byte[] BuildFromSnapshot(
            UserInfoAdditionSnapshot addition,
            SkillInfoSnapshot skills)
            => BuildFromSnapshot(addition, skills, appearance: null);

        public static byte[] BuildFromSnapshot(
            UserInfoAdditionSnapshot addition,
            SkillInfoSnapshot skills,
            CharacterAppearanceEntry[] appearance)
        {
            if (addition == null)
                throw new ArgumentNullException(nameof(addition));

            var equipped = MergeA21FashionEntries(
                addition.EquippedEntries,
                appearance);
            var writer = new GamePacketWriter();

            writer.WriteUInt32(addition.CharacExp);
            writer.WriteInt32(CombatStatBlobWriter.BlobLength);
            CombatStatBlobWriter.Write(writer, addition);
            writer.WriteByte(addition.ExEquipSlotStat);

            if (equipped.Count > byte.MaxValue)
                throw new InvalidDataException("USERINFO1 equipment count exceeds its u8 field.");
            writer.WriteByte((byte)equipped.Count);
            foreach (var entry in equipped)
            {
                var core = entry?.Core;
                if (core == null)
                    throw new InvalidDataException(
                        $"[UserInfoSubtype1Builder] slot {entry?.Slot}: ItemCore 未初始化，不能写入 subtype1 装备 entry。");

                ItemListProtocolWriter.WriteNoti2EquippedEntry(
                    writer,
                    entry.Slot,
                    core,
                    addition.GetAvatarDetail(core),
                    addition.GetCreatureDetail(core),
                    addition.GetBoundAuroraDetail(core));
            }

            writer.WriteUInt32(addition.CloneTitleItemId);
            writer.WriteUInt32(addition.NameTagItemId);
            writer.WriteUInt32(addition.NameTagExpireTime);
            writer.WriteByte(addition.SkillTreeIndex);
            WriteSkillPage(writer, skills, 0);
            WriteSkillPage(writer, skills, 1);
            writer.WriteByte(addition.EquippedCreatureLevel);
            // A21 13E6370 calls 13E1D60 immediately after the creature level.
            WriteA21DimensionTail(writer, addition);
            return writer.ToArray();
        }

        internal static IList<EquippedEntrySnapshot> MergeA21FashionEntries(
            IList<EquippedEntrySnapshot> equipped,
            CharacterAppearanceEntry[] appearance)
        {
            var result = new List<EquippedEntrySnapshot>();
            var slots = new HashSet<short>();
            if (equipped != null)
            {
                foreach (var entry in equipped)
                {
                    if (entry == null)
                        continue;
                    result.Add(entry);
                    slots.Add(entry.Slot);
                }
            }

            if (appearance != null)
            {
                foreach (var entry in appearance)
                {
                    if (entry == null || entry.DisplayItemId <= 0 || entry.Slot > 7)
                        continue;
                    if (!slots.Add(entry.Slot))
                        continue;

                    result.Add(new EquippedEntrySnapshot
                    {
                        Slot = entry.Slot,
                        Core = ItemCore.Create(ItemCore.KindAvatar, entry.DisplayItemId),
                    });
                }
            }

            return result;
        }

        private static readonly byte[] A21AfterDimensionPrefix =
        {
            0x02, 0x00, 0x05, 0x00, 0x6F,
        };

        private static void WriteA21DimensionTail(
            GamePacketWriter writer,
            UserInfoAdditionSnapshot addition)
        {
            var stored = new Dictionary<uint, DimensionEntrySnapshot>();
            if (addition.Dimensions != null)
            {
                foreach (var entry in addition.Dimensions)
                    stored[entry.Key] = entry;
            }

            writer.WriteByte((byte)SpecialDungeonEntryLimitDefaults.Entries.Length);
            foreach (var defaultEntry in SpecialDungeonEntryLimitDefaults.Entries)
            {
                var key = (uint)defaultEntry.DungeonId;
                byte value1 = 0;
                var value2 = defaultEntry.CurrentCount;
                if (stored.TryGetValue(key, out var saved))
                {
                    value2 = saved.Val2;
                }

                writer.WriteUInt32(key);
                writer.WriteByte(value1);
                writer.WriteByte(value2);
            }

            // Completed special-reward quest ids are resolved locally by A21.
            writer.WriteBytes(A21AfterDimensionPrefix);
            writer.WriteUInt32((uint)addition.SpecialRewardQuestIds.Count);
            foreach (var questId in addition.SpecialRewardQuestIds)
                writer.WriteUInt32(questId);
            writer.WriteByte(addition.ManageLevel);
            writer.WriteUInt32(unchecked((uint)addition.ManagePoint));
        }

        private static void WriteSkillPage(
            GamePacketWriter writer,
            SkillInfoSnapshot skills,
            int pageIndex)
        {
            if (skills == null || pageIndex >= skills.Pages.Count || skills.Pages[pageIndex] == null)
            {
                writer.WriteByte(0);
                return;
            }

            var page = skills.Pages[pageIndex];
            var count = 0;
            foreach (var entry in page.Entries)
            {
                if (entry != null && entry.Level > 0)
                    count++;
            }

            if (count > byte.MaxValue)
                throw new InvalidDataException("USERINFO1 skill count exceeds its u8 field.");
            // 13E2540 clears and reads both remote skill pages independently.
            // An empty second page does not mean a copy of the first page.
            writer.WriteByte((byte)count);
            foreach (var entry in page.Entries)
            {
                if (entry == null || entry.Level <= 0)
                    continue;
                writer.WriteUInt16(entry.SkillId);
                writer.WriteByte(entry.Level);
            }
        }

    }
}
