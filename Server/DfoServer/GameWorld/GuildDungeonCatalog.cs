using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal sealed class GuildDungeonCatalog
    {
        private static readonly Lazy<GuildDungeonCatalog> CurrentValue = new(Load);
        internal static GuildDungeonCatalog Current => CurrentValue.Value;
        internal int StyleId { get; }
        internal IReadOnlyDictionary<int, GuildDungeonDefinition> Dungeons { get; }
        internal GuildDungeonCatalog(int styleId, GuildDungeonFile file)
        {
            StyleId = styleId;
            Dungeons = new System.Collections.ObjectModel.ReadOnlyDictionary<int, GuildDungeonDefinition>(
                file.Dungeons.ToDictionary(d => d.Id));
        }
        private static GuildDungeonCatalog Load()
        {
            const string directory = "etc/guild/guilddungeon/";
            var text = PvfArchiveAccessor.ReadText(directory + "guilddungeonstyle.lst");
            var matches = Regex.Matches(text, @"(?m)^\s*(\d+)\s+`([^`]+)`");
            if (matches.Count != 1) throw new InvalidOperationException("Select an unambiguous PVF guild dungeon style before enabling it.");
            var m = matches[0];
            var path = m.Groups[2].Value.Replace('\\', '/');
            if (path.Contains("..") || path.StartsWith('/')) throw new FormatException("Invalid guild dungeon style path.");
            return new GuildDungeonCatalog(int.Parse(m.Groups[1].Value),
                new GuildDungeonFile(PvfArchiveAccessor.ReadText(directory + path)));
        }
    }
}
