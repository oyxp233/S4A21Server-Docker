using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;

namespace DfoServer.Game.Guilds
{
    internal sealed record GuildContentDefinition(int Id, byte Type, int Value, int Days, int Price);
    internal sealed record GuildContent(byte Type, int Id, int Value, long ExpiresAt)
    {
        internal bool IsActive(long now) => ExpiresAt == 0 || ExpiresAt > now;
        internal uint ClientExpiresAt => ExpiresAt == 0 ? int.MaxValue : checked((uint)ExpiresAt);
    }

    internal static class GuildContentRules
    {
        private static readonly Lazy<IReadOnlyDictionary<int, GuildContentDefinition>> Catalog = new(Load);
        internal static IReadOnlyDictionary<int, GuildContentDefinition> Current => Catalog.Value;
        // Current PVF content types: 1 = monster EXP percent, 3 = temporary
        // four-stat passive 167, 5 = permanent four-stat passive 169.
        internal static bool Supported(GuildContentDefinition d) => d != null && d.Price > 0
            && ((d.Type == 1 && d.Value == 10 && d.Days is 1 or 7 or 30)
                || (d.Type == 3 && d.Value == 167 && d.Days is 1 or 7 or 30)
                || (d.Type == 5 && d.Value == 169 && d.Days == -1));
        internal static int StatBonus(IEnumerable<GuildContent> contents, long now)
            => contents.Where(c => c.IsActive(now) && ((c.Type == 3 && c.Value == 167)
                || (c.Type == 5 && c.Value == 169))).Sum(_ => 60);
        internal static int ExperiencePercent(IEnumerable<GuildContent> contents, long now)
            => contents.Where(c => c.Type == 1 && c.IsActive(now)).Select(c => c.Value).DefaultIfEmpty().Max();
        internal static uint ExperienceBonus(uint experience, int percent)
            => percent <= 0 ? 0 : (uint)Math.Min(uint.MaxValue, (ulong)experience * (uint)percent / 100);

        private static IReadOnlyDictionary<int, GuildContentDefinition> Load()
        {
            var text = PvfArchiveAccessor.ReadText("etc/(r)guild.etc");
            var section = Regex.Match(text, @"\[guild contents list\](.*?)\[/guild contents list\]", RegexOptions.Singleline);
            if (!section.Success) throw new InvalidOperationException("PVF guild contents list is missing.");
            var result = new Dictionary<int, GuildContentDefinition>();
            foreach (Match row in Regex.Matches(section.Groups[1].Value,
                @"(\d+)\s+(\d+)\s+(-?\d+)\s+(-?\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+`[^`]*`"))
            {
                int Number(int n) => int.Parse(row.Groups[n].Value, CultureInfo.InvariantCulture);
                var d = new GuildContentDefinition(Number(1), checked((byte)Number(2)), Number(3), Number(4), Number(8));
                if (Supported(d)) result.Add(d.Id, d);
            }
            if (result.Count != 7) throw new InvalidOperationException("Current PVF guild stat/EXP catalog changed; verify its effects before enabling purchases.");
            var buffs = PvfArchiveAccessor.ReadText("etc/variousbufflist.etc");
            foreach (int id in new[] {167,169})
            {
                var buff = Regex.Match(buffs, @"\[index\]\s*" + id + @"\b(.*?)\[/index\]", RegexOptions.Singleline).Groups[1].Value;
                foreach (string stat in new[] {"physical_attack","physical_defence","magical_attack","magical_defence"})
                    if (!Regex.IsMatch(buff, @"\[name\]\s*`" + stat + @"`\s*\[param\]\s*60\s+0\s*\[/param\]"))
                        throw new InvalidOperationException("PVF guild four-stat buff changed; verify before enabling purchases.");
                if (!Regex.IsMatch(buff, @"\[type\]\s*`expire_time`\s*\[param\]\s*0\s+0\s*\[/param\]"))
                    throw new InvalidOperationException("PVF guild buff expiry policy changed.");
            }
            return result;
        }
    }
}
