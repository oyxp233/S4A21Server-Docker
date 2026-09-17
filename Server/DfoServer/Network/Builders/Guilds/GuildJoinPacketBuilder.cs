using DfoServer.Game.Guilds;
using System.Collections.Generic;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildJoinPacketBuilder
    {
        // 01132600: u32 ID, DSTR name, u16 count, DSTR promotion, u32, u8 tag count.
        internal static byte[] Search(GuildSearchResult result)
        {
            if (result == null) return GuildCreationPacketBuilder.Ack(CmdPacketTypeA21.REQ_GUILD_SERCH_FOR_JOIN, 0x22);
            var w = new GamePacketWriter();
            w.WriteByte(1); w.WriteInt32(result.Guild.Id); w.WriteClientDstr(result.Guild.Name);
            w.WriteUInt16(result.MemberCount); w.WriteClientDstr(result.Guild.Promotion);
            w.WriteUInt32(0); w.WriteByte(0); // No recruitment tags or additional score.
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.REQ_GUILD_SERCH_FOR_JOIN, w.ToArray());
        }

        // 0111C880 reads both DSTRs and both u32s even though only ID/name enter its registry.
        internal static byte[] Applied(GuildRecord guild, string message)
        {
            var w = new GamePacketWriter();
            w.WriteByte(1); w.WriteInt32(guild.Id); w.WriteClientDstr(guild.Name);
            w.WriteClientDstr(message); w.WriteUInt32(0);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.REQUEST_JOIN_GUILD, w.ToArray());
        }

        // 0113F040; 024DB7AB renders the third byte as level, fourth decorates job name.
        // 024DB878 -> 024DB440 compares the last field directly to 86400/31536000
        // and divides by 3600/86400. It is elapsed seconds, NOT a Unix timestamp.
        internal static byte[] Applications(IReadOnlyList<GuildApplication> rows)
        {
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteInt32(rows.Count);
            foreach (var row in rows)
            {
                w.WriteInt32(row.CharacterId); w.WriteClientDstr(row.Name);
                w.WriteByte(row.Job); w.WriteByte(row.GrowType); w.WriteByte(row.Level); w.WriteByte(0);
                w.WriteClientDstr(row.Message); w.WriteUInt32(row.ElapsedSeconds);
            }
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.GUILD_JOIN_LIST, w.ToArray());
        }

        // 0113B490 consumes target ID on success AND after the common error byte on failure.
        internal static byte[] Approved(int characterId, bool success)
        {
            var w = new GamePacketWriter(); w.WriteByte(success ? (byte)1 : (byte)0);
            if (!success) w.WriteByte(0);
            w.WriteInt32(characterId);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.APPROVE_JOIN_GUILD, w.ToArray());
        }

        // 0118EC10 -> 01833DA0 writes actor +5C14; getter 01833DB0 is used by 019EA3F0 permissions.
        internal static byte[] Rank(bool leader) => Rank(leader ? (byte)1 : (byte)4);
        internal static byte[] Rank(byte rank) => GamePacketEnvelopeBuilder.Build(0,
            (ushort)NotiPacketTypeA21.GUILD_MEMBER_INFO, new[] { rank });
    }
}
