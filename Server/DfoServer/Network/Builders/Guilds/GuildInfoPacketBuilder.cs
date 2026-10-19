using DfoServer.Game.Guilds;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildInfoPacketBuilder
    {
        // DNF.exe 1F8244D3...B20B00DD, 011959B0..0119681B. Optional systems
        // (alliance, purchased content, attendance/rewards) remain empty/disabled.
        // 2026-10-16 panel probe: 102 is 公会积分, 104/105 are guild cumulative and
        // monthly, 106/107 are the viewer's cumulative and monthly.
        internal static byte[] Build(GuildRoster roster, int viewerCharacterId)
        {
            uint guild = checked((uint)roster.Guild.Contribution);
            uint mine = 0;
            foreach (var member in roster.Members)
                if (member.CharacterId == viewerCharacterId) { mine = checked((uint)member.Contribution); break; }
            var w = new GamePacketWriter();
            w.WriteClientDstr(roster.Guild.Name);
            w.WriteByte(1); // Membership-present state, getter 019E9E60.
            w.WriteByte(0);
            w.WriteUInt16(checked((ushort)roster.Members.Count));
            w.WriteByte(1); // Initial guild level; no upgrade feature.
            w.WriteUInt32(0); w.WriteByte(0); // Not 公会积分; live panel stayed 0.
            w.WriteClientDstr(roster.Guild.Announcement);
            w.WriteByte(0);
            // 01195D38 compares this byte with 0 and skips 019F5140 when it is 0.
            // 019F5140 allocates the agit subobject and sets its level dword to 1.
            // The Seria loader then reads that dword: below 2 links town 8 area 0.
            w.WriteByte(1);
            w.WriteByte(0);
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
            w.WriteUInt32(0); // Live panel stayed 0 when this carried contribution.
            w.WriteByte(0); // Alliance count, 01196350.
            w.WriteUInt32(guild); w.WriteByte(0); // 公会积分 (GM).
            w.WriteUInt32(0); w.WriteByte(0);
            w.WriteUInt32(guild); // 公会累积贡献度.
            w.WriteUInt32(guild); // 本月公会当前；没有单独的月计数.
            w.WriteUInt32(mine); // 我的累积贡献度.
            w.WriteUInt32(mine); // 本月我的当前.
            w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0);
            return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.GUILD_INFO, w.ToArray());
        }

        // Success byte keeps the existing donate completion dialog. The two totals
        // follow it so a reader that updates 公会积分 from this ACK can see them.
        internal static byte[] Donated(int guildContribution, int memberContribution)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt32(checked((uint)guildContribution));
            writer.WriteUInt32(checked((uint)memberContribution));
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.DONATE_GUILD_FUND, writer.ToArray());
        }
    }
}
