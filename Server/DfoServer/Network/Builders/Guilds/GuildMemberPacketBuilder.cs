using DfoServer.Game.Guilds;
using System;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildMemberPacketBuilder
    {
        // Current DNF.exe 1F8244D3...B20B00DD, reader 01130B60.
        // No pagination cursor on GUILD_MEMER_LIST (unlike ALL_MEMBER_LIST).
        internal static byte[] Build(GuildRoster roster, Func<int, byte?> findOnlineChannel)
        {
            ArgumentNullException.ThrowIfNull(roster);
            ArgumentNullException.ThrowIfNull(findOnlineChannel);
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(roster.Guild.Id);
            writer.WriteClientDstr(roster.LeaderName);
            writer.WriteUInt32(0); // Guild progression is not enabled.
            writer.WriteUInt16(checked((ushort)roster.Members.Count));
            writer.WriteUInt16(checked((ushort)roster.Members.Count));
            foreach (var member in roster.Members)
            {
                byte? channel = findOnlineChannel(member.CharacterId);
                if (channel == byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(findOnlineChannel));
                writer.WriteInt32(member.CharacterId);
                writer.WriteClientDstr(member.Name);
                writer.WriteClientDstr(member.Memo);
                writer.WriteUInt16(member.Level);
                writer.WriteByte(member.Job);
                writer.WriteByte(member.GrowType);
                writer.WriteByte(GameNetworkConfig.ChannelServerIndex); // +34, server/channel lookup 0079CB3C.
                writer.WriteByte(channel ?? byte.MaxValue); // +38, FF means offline (01130F6E).
                writer.WriteByte(0); // Optional job-name decoration (+30, 01861380).
                writer.WriteByte(0); // No hidden-member mode (+144, 0079C532).
                writer.WriteByte(member.Rank); // +148, 019EB1C2 / 019EA460.
                writer.WriteUInt32(channel.HasValue ? 0 : member.OfflineSeconds); // +158, 00774D4D seconds-to-days display.
                writer.WriteInt32(member.AccountId); // +15C groups characters in 019F0AC0.
                writer.WriteByte(0); // No representative-character designation.
                writer.WriteClientDstr(""); // No account display alias (+164).
            }
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.GUILD_MEMER_LIST, writer.ToArray());
        }
    }
}
