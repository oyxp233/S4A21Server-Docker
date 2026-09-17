using DfoServer.Infrastructure;

namespace DfoServer.Game.Guilds
{
    internal enum GuildTextField { Announcement, Promotion, Memo }

    internal static class GuildManagementRules
    {
        // Conservative server policies within the current readers' buffers.
        internal static int MaximumBytes(GuildTextField field) => field == GuildTextField.Announcement ? 120 : field == GuildTextField.Memo ? 60 : 80;
        internal static bool IsValidText(GuildTextField field, string text)
        {
            if (text == null || text.Length > MaximumBytes(field) / 2 || text.IndexOf('\0') >= 0
                || ClientTextEncoding.GetBytes(text).Length > MaximumBytes(field)) return false;
            if (field == GuildTextField.Promotion) return GuildCreationRules.IsValidPromotion(text);
            foreach (char ch in text) if (char.IsControl(ch)) return false;
            return true;
        }

        // Management remains leader-owned. Other grades are persistent titles;
        // they cannot grant themselves approval, expulsion or leadership rights.
        // Client 025A2580 checks bit 10 before adding the member-removal
        // entry, and also requires the actor's rank to outrank the target.
        internal static uint Permissions(int rank) => rank == 1
            ? (1u << 1) | (1u << 2) | (1u << 4) | (1u << 5) | (1u << 10) | (1u << 11) : 0;
        internal static string RankName(int rank) => rank switch
        {
            1 => "会长", 2 => "副会长", 3 => "优秀成员", 4 => "普通成员", 5 => "精英成员", _ => ""
        };
    }
}
