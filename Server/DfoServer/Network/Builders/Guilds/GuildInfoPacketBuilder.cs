using DfoServer.Game.Guilds;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildInfoPacketBuilder
    {
        // DNF.exe 1F8244D3...B20B00DD, 011959B0..0119681B. Optional systems
        // (alliance, purchased content, attendance/rewards) remain empty/disabled.
        internal static byte[] Build(GuildRoster roster)
        {
            var w = new GamePacketWriter();
            w.WriteClientDstr(roster.Guild.Name);
            w.WriteByte(1); // Membership-present state, getter 019E9E60.
            w.WriteByte(0);
            w.WriteUInt16(checked((ushort)roster.Members.Count));
            w.WriteByte(1); // Initial guild level; no upgrade feature.
            w.WriteUInt32(0); w.WriteByte(0);
            w.WriteClientDstr(roster.Guild.Announcement);
            w.WriteByte(0); w.WriteByte(0); w.WriteByte(0);
            w.WriteByte(1); // Guild model level, 0119602F -> 019F5070.
            w.WriteUInt32(0);
            w.WriteClientDstr(""); w.WriteClientDstr("");
            w.WriteUInt16(0);
            w.WriteClientDstr(roster.Guild.Promotion);
            // Exactly 6 * (u32 permission mask + 24-byte NUL-terminated GBK label).
            // Bit 1 controls application approval (00775C69, applicant control +7B0).
            for (int rank = 0; rank < 6; rank++)
            {
                w.WriteUInt32(GuildManagementRules.Permissions(rank));
                var name = ClientTextEncoding.GetBytes(GuildManagementRules.RankName(rank));
                w.WriteBytes(name); w.WriteZeroBytes(24 - name.Length);
            }
            w.WriteByte(0); // Purchased-content count, 011962A1.
            w.WriteUInt32(0);
            w.WriteByte(0); // Alliance count, 01196350.
            w.WriteUInt32(0); w.WriteByte(0); w.WriteUInt32(0); w.WriteByte(0);
            for (int i = 0; i < 8; i++) w.WriteUInt32(0); // 0119671F..011967F2.
            return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.GUILD_INFO, w.ToArray());
        }
    }
}
