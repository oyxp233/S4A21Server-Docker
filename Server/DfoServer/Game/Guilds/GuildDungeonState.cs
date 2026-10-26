using System;
using System.Collections.Generic;

namespace DfoServer.Game.Guilds
{
    internal sealed record GuildDungeonPeriod(long StartUnix, long EndUnix)
    {
        // Local-server policy: monthly reset at 06:00 Beijing. Recomputed on each
        // read/admission, never based on process uptime or a timer firing.
        internal static GuildDungeonPeriod At(DateTimeOffset now)
        {
            var beijing = now.ToOffset(TimeSpan.FromHours(8));
            var start = new DateTimeOffset(beijing.Year, beijing.Month, 1, 6, 0, 0, beijing.Offset);
            if (beijing < start) start = start.AddMonths(-1);
            return new(start.ToUnixTimeSeconds(), start.AddMonths(1).ToUnixTimeSeconds());
        }
    }
    internal sealed record GuildDungeonSnapshot(int GuildId, int StyleId, GuildDungeonPeriod Period,
        IReadOnlyDictionary<int, int> ClearCounts);
    internal sealed record GuildDungeonAdmission(int GuildId, GuildDungeonPeriod Period, int StyleId);
    internal sealed record GuildDungeonClearResult(bool Applied, long MailId, int ClearCount);
}
