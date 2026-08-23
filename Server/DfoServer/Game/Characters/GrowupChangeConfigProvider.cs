using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Characters
{
    internal sealed class GrowupChangeConfig
    {
        public int MinLevel { get; set; }

        public int MaxLevel { get; set; }

        public List<int> GoldCosts { get; } = new List<int>();

        public bool IsValid => MinLevel > 0
            && MaxLevel >= MinLevel
            && GoldCosts.Count > 0;

        public bool AllowsLevel(int level)
        {
            return level >= MinLevel && level <= MaxLevel;
        }

        public int ResolveGoldCost(int changeCount)
        {
            if (GoldCosts.Count == 0)
                return int.MaxValue;

            var index = Math.Max(0, changeCount);
            if (index >= GoldCosts.Count)
                index = GoldCosts.Count - 1;

            return Math.Max(0, GoldCosts[index]);
        }
    }

    internal static class GrowupChangeConfigProvider
    {
        private const string ConfigPath = "character/growup.etc";

        private static readonly Lazy<GrowupChangeConfig> Current =
            new Lazy<GrowupChangeConfig>(Load);

        internal static GrowupChangeConfig Get()
        {
            return Current.Value;
        }

        internal static GrowupChangeConfig Parse(string content)
        {
            var config = new GrowupChangeConfig();
            if (string.IsNullOrWhiteSpace(content))
                return config;

            var root = new ScriptParser().Parse(content);
            foreach (var node in root.Children)
            {
                var tag = (node.Tag ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                switch (tag)
                {
                    case "grow up change lv":
                        var levels = ReadIntegers(node, content);
                        if (levels.Count >= 2)
                        {
                            config.MinLevel = Math.Max(1, levels[0]);
                            config.MaxLevel = Math.Max(config.MinLevel, levels[1]);
                        }
                        break;
                    case "grow up change gold":
                        config.GoldCosts.Clear();
                        foreach (var value in ReadIntegers(node, content))
                            config.GoldCosts.Add(Math.Max(0, value));
                        break;
                }
            }

            return config;
        }

        private static GrowupChangeConfig Load()
        {
            try
            {
                return Parse(PvfArchiveAccessor.ReadText(ConfigPath));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GrowupChangeConfig] load failed path={ConfigPath}: {ex.Message}");
                return new GrowupChangeConfig();
            }
        }

        private static List<int> ReadIntegers(ScriptNode node, string content)
        {
            var result = new List<int>();
            if (node?.DataItems == null)
                return result;

            foreach (var item in node.DataItems)
            {
                var raw = item.GetContent(content) ?? string.Empty;
                foreach (Match match in Regex.Matches(raw, @"-?\d+"))
                {
                    if (int.TryParse(match.Value, out var value))
                        result.Add(value);
                }
            }

            return result;
        }
    }
}
