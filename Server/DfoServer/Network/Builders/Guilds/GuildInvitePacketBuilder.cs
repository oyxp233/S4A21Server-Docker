using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildInvitePacketBuilder
    {
        // 2026-10-16 live popup: a leading guild id of 1 was consumed as a DSTR
        // length, so the name became one byte and the next u16 (0x0000) showed
        // as 0 members. The window then displays name, member count, weekly
        // activity, promotion, and an empty content list.
        internal static byte[] Invited(string guildName, ushort onlineCount, string promotion)
        {
            var writer = new GamePacketWriter();
            writer.WriteClientDstr(guildName);
            writer.WriteUInt16(onlineCount);
            writer.WriteUInt32(0);
            writer.WriteClientDstr(promotion ?? string.Empty);
            writer.WriteByte(0);
            return GamePacketEnvelopeBuilder.Build(0,
                (ushort)NotiPacketTypeA21.CALL_GUILD_INVITE_TO_INVITED, writer.ToArray());
        }

        internal static byte[] ReplyToCaller(bool accepted)
            => GamePacketEnvelopeBuilder.Build(0,
                (ushort)NotiPacketTypeA21.REPLY_GUILD_INVITE_TO_CALLER,
                new[] { accepted ? (byte)1 : (byte)0 });
    }
}
