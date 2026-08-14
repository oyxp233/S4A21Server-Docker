using DfoServer.Game.Accounts;
using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
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
            var slotLimit = CharacterSlotPolicy.ResolveSlotLimit(template?.GateOrCount1, template?.GateOrCount2);
            writer.WriteByte(2);
            writer.WriteUInt16(slotLimit);
            writer.WriteUInt16(template != null ? template.GateOrCount2 : slotLimit);
            writer.WriteByte(adventureGroup.ManageLevel);
            writer.WriteInt32(adventureGroup.TotalPoint);
            writer.WriteUInt16(template != null ? template.Unknown16 : (ushort)0);
            writer.WriteInt32(template != null ? template.Unknown32 : 0);
            writer.WriteUInt16((ushort)Math.Min(ushort.MaxValue, characters.Count));

            for (var i = 0; i < characters.Count && i < ushort.MaxValue; i++)
            {
                var ch = characters[i];
                if (ch == null)
                    continue;

                writer.WriteUInt16((ushort)ch.SlotIndex);
                writer.WriteDstr(ch.Name);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteByte(ch.Job);
                writer.WriteByte(ch.GrowType);
                writer.WriteByte(ch.Level);
                WriteHonorRosterFields(writer, honorLevel);

                var appearances = Array.Empty<CharacterAppearanceEntry>();
                if (ch.CharacterId > 0)
                    rosterAppearances.TryGetValue(ch.CharacterId, out appearances);
                appearances = appearances ?? Array.Empty<CharacterAppearanceEntry>();
                writer.WriteByte((byte)Math.Min(byte.MaxValue, appearances.Length));
                for (var j = 0; j < appearances.Length && j < byte.MaxValue; j++)
                    UserInfoSubtype0Builder.WriteAppearanceEntry(writer, appearances[j]);

                var cloneTitleItemId = AppearanceService.LoadCloneTitleItemId(
                    ch.CharacterId,
                    database);
                UserInfoType2RosterTailBuilder.Write(writer, cloneTitleItemId > 0 ? (uint)cloneTitleItemId : 0);
            }

            return writer.ToArray();
        }

        private static void WriteHonorRosterFields(GamePacketWriter writer, HonorLevelSummary honorLevel)
        {
            writer.WriteUInt32(honorLevel?.HonorLevel ?? 0);
            writer.WriteUInt32(honorLevel?.HonorExp ?? 0);
            writer.WriteUInt16(0);
        }
    }
}
