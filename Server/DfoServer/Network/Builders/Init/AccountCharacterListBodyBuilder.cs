using DfoServer.Game.Accounts;
using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class AccountCharacterListBodyBuilder
    {
        public static byte[] Build(
            IReadOnlyList<CharacterRecord> characters,
            GetUserInfoTemplate template,
            out AdventureGroupSummary adventureGroup,
            HonorLevelSummary honorLevel = null,
            int accountId = 0,
            IGameDatabase database = null)
        {
            characters = characters ?? Array.Empty<CharacterRecord>();
            adventureGroup = AdventureGroupDataProvider.Calculate(characters);
            honorLevel = honorLevel ?? HonorLevelDataProvider.CalculateFromHonorExp(0, characters);
            var rosterAppearances = accountId > 0
                ? AppearanceService.LoadRosterAppearancesFromDb(
                    accountId,
                    characters,
                    database)
                : new Dictionary<int, CharacterAppearanceEntry[]>();

            var writer = new GamePacketWriter();
            var slotLimit = CharacterSlotPolicy.ResolveSlotLimit(
                template?.GateOrCount1,
                template?.GateOrCount2);
            writer.WriteByte(2);
            writer.WriteUInt16(slotLimit);
            writer.WriteUInt16(template != null ? template.GateOrCount2 : slotLimit);
            // A21 type=2 头部不承载冒险团数值；冒险团状态由 01BA 主动通知。
            writer.WriteByte(0);
            writer.WriteInt32(0);
            writer.WriteUInt16(template != null ? template.Unknown16 : (ushort)0);
            writer.WriteInt32(template != null ? template.Unknown32 : 0);

            var characterCount = 0;
            for (var i = 0; i < characters.Count && characterCount < ushort.MaxValue; i++)
            {
                if (characters[i] != null)
                    characterCount++;
            }
            writer.WriteUInt16((ushort)characterCount);

            var written = 0;
            for (var i = 0; i < characters.Count && written < characterCount; i++)
            {
                var character = characters[i];
                if (character == null)
                    continue;

                // characters.slot_index is already zero-based in the current
                // SQLite baseline and in CREATE/CHANGE_CHARAC_SLOT requests.
                // Do not subtract one here: that collapses slots 0 and 1
                // into the same A21 roster slot and hides the old character.
                var wireSlot = character.SlotIndex;
                WriteA21RosterCharacter(
                    writer,
                    character,
                    wireSlot,
                    ResolveA21RosterAppearances(character, rosterAppearances),
                    database);
                written++;
            }

            return writer.ToArray();
        }

        private static void WriteA21RosterCharacter(
            GamePacketWriter writer,
            CharacterRecord character,
            int wireSlot,
            CharacterAppearanceEntry[] appearances,
            IGameDatabase database)
        {
            appearances = FilterA21RosterAppearances(appearances);
            var appearanceCount = Math.Min(byte.MaxValue, appearances.Length);

            writer.WriteUInt16((ushort)wireSlot);
            writer.WriteDstr(character.Name);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte((byte)(character.Job & 0x0F));
            writer.WriteByte(character.GrowType);
            writer.WriteByte(character.Level);
            writer.WriteByte(0x0B);
            writer.WriteByte(0x01);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte((byte)appearanceCount);
            for (var i = 0; i < appearanceCount; i++)
                WriteA21AppearanceEntry(writer, appearances[i]);

            var cloneTitleItemId = AppearanceService.LoadCloneTitleItemId(
                character.CharacterId,
                database);
            UserInfoType2RosterTailBuilder.WriteA21(
                writer,
                cloneTitleItemId > 0 ? (uint)cloneTitleItemId : 0);
        }

        private static CharacterAppearanceEntry[] ResolveA21RosterAppearances(
            CharacterRecord character,
            IReadOnlyDictionary<int, CharacterAppearanceEntry[]> rosterAppearances)
        {
            CharacterAppearanceEntry[] source = null;
            if (character != null
                && character.CharacterId > 0
                && rosterAppearances != null)
            {
                rosterAppearances.TryGetValue(character.CharacterId, out source);
            }

            if ((source == null || source.Length == 0) && character?.Appearance != null)
                source = character.Appearance;

            if (source == null || source.Length == 0)
                return Array.Empty<CharacterAppearanceEntry>();

            var mapped = new List<CharacterAppearanceEntry>(source.Length);
            foreach (var entry in source)
            {
                if (entry == null || entry.DisplayItemId <= 0)
                    continue;

                var slot = EquipmentTypeInfo.ToA21AppearanceSlot(entry.Slot);
                mapped.Add(new CharacterAppearanceEntry(
                    (byte)slot,
                    entry.DisplayItemId,
                    entry.ExpansionLen,
                    entry.ExpansionData,
                    entry.State,
                    entry.LinkItemId,
                    entry.EnchantValue,
                    entry.Flag20));
            }

            return mapped.ToArray();
        }

        private static CharacterAppearanceEntry[] FilterA21RosterAppearances(
            CharacterAppearanceEntry[] source)
        {
            if (source == null || source.Length == 0)
                return Array.Empty<CharacterAppearanceEntry>();

            var result = new List<CharacterAppearanceEntry>(source.Length);
            foreach (var entry in source)
            {
                if (entry == null)
                    continue;
                if (EquipmentTypeInfo.IsA21RosterAppearanceSlot(entry.Slot))
                {
                    result.Add(entry);
                }
            }

            return result.ToArray();
        }

        private static void WriteA21AppearanceEntry(
            GamePacketWriter writer,
            CharacterAppearanceEntry entry)
        {
            writer.WriteByte(entry.Slot);
            writer.WriteInt32(entry.DisplayItemId);
            writer.WriteInt32(4);
            writer.WriteBytes(entry.ExpansionData != null && entry.ExpansionData.Length == 4
                ? entry.ExpansionData
                : new byte[4]);
            writer.WriteByte(entry.State);
            writer.WriteInt32(entry.LinkItemId);
            writer.WriteUInt32(entry.EnchantValue);
            writer.WriteByte(entry.Flag20);
        }
    }
}
