using DfoServer.Game.Characters;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Accounts
{
    public sealed class AdventureGroupSummary
    {
        public int TotalPoint { get; set; }
        public byte ManageLevel { get; set; }
        public byte ExpBonusPercent { get; set; }
        public byte GoldBonusPercent { get; set; }
        public int ManageOption { get; set; }
    }

    public static class AdventureGroupDataProvider
    {
        private static readonly object Sync = new object();
        private static AdventureGroupTables _tables;

        public static void Warmup()
        {
            EnsureLoaded();
        }

        public static AdventureGroupSummary Calculate(IEnumerable<CharacterRecord> characters)
        {
            EnsureLoaded();

            var totalPoint = 0;
            if (characters != null)
            {
                foreach (var character in characters)
                {
                    if (character == null || character.Deleted)
                        continue;

                    totalPoint += ResolveCharacterPoint(character.Level);
                }
            }

            var level = ResolveManageLevel(totalPoint);
            return new AdventureGroupSummary
            {
                TotalPoint = totalPoint,
                ManageLevel = (byte)Math.Max(0, Math.Min(byte.MaxValue, level)),
                ExpBonusPercent = ResolvePercent(_tables.ExpBonusByLevel, level),
                GoldBonusPercent = ResolvePercent(_tables.GoldBonusByLevel, level),
                ManageOption = ResolveInt(_tables.ManageOptionByLevel, level),
            };
        }

        private static int ResolveCharacterPoint(byte level)
        {
            // PVF [point bonus] 是逐级贡献，需要累加每个符合等级。
            var totalPoint = 0;
            var maxLevel = (int)level;
            foreach (var pair in _tables.PointByLevel)
            {
                if (pair.Key > maxLevel)
                    continue;

                totalPoint += pair.Value;
            }

            return totalPoint;
        }

        private static int ResolveManageLevel(int totalPoint)
        {
            var level = 0;
            for (var i = 0; i < _tables.ManageLevelThresholds.Count; i++)
            {
                if (totalPoint >= _tables.ManageLevelThresholds[i])
                    level = i + 1;
                else
                    break;
            }

            return _tables.ManageLevelMax > 0 ? Math.Min(level, _tables.ManageLevelMax) : level;
        }

        private static byte ResolvePercent(Dictionary<int, int> values, int level)
        {
            return (byte)Math.Max(0, Math.Min(byte.MaxValue, ResolveInt(values, level)));
        }

        private static int ResolveInt(Dictionary<int, int> values, int level)
        {
            if (level <= 0 || values == null)
                return 0;

            return values.TryGetValue(level, out var value) ? value : 0;
        }

        private static void EnsureLoaded()
        {
            if (_tables != null)
                return;

            lock (Sync)
            {
                if (_tables != null)
                    return;

                _tables = Parse(PvfArchiveAccessor.ReadText("etc/linksystem/charactermanage.etc"));
            }
        }

        private static AdventureGroupTables Parse(string text)
        {
            var tables = new AdventureGroupTables();
            ParsePointBonus(ExtractSection(text, "point bonus"), tables.PointByLevel);
            ParseNumberList(ExtractSection(text, "manage level point"), tables.ManageLevelThresholds);
            tables.ManageLevelMax = ParseFirstInt(ExtractSection(text, "manage level max"));
            ParsePairTable(ExtractSection(text, "exp bonus"), tables.ExpBonusByLevel);
            ParsePairTable(ExtractSection(text, "gold bonus"), tables.GoldBonusByLevel);
            ParsePairTable(ExtractSection(text, "manage option"), tables.ManageOptionByLevel);
            return tables;
        }

        private static void ParsePointBonus(string section, Dictionary<int, int> output)
        {
            var numbers = ParseInts(section);
            for (var i = 0; i + 2 < numbers.Count; i += 3)
            {
                var minLevel = numbers[i];
                var maxLevel = numbers[i + 1];
                var point = numbers[i + 2];
                for (var level = minLevel; level <= maxLevel; level++)
                    output[level] = point;
            }
        }

        private static void ParsePairTable(string section, Dictionary<int, int> output)
        {
            var numbers = ParseInts(section);
            for (var i = 0; i + 1 < numbers.Count; i += 2)
                output[numbers[i]] = numbers[i + 1];
        }

        private static void ParseNumberList(string section, List<int> output)
        {
            output.AddRange(ParseInts(section));
        }

        private static int ParseFirstInt(string section)
        {
            var numbers = ParseInts(section);
            return numbers.Count > 0 ? numbers[0] : 0;
        }

        private static List<int> ParseInts(string text)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (Match match in Regex.Matches(text, @"[-]?\d+"))
            {
                if (int.TryParse(match.Value, out var value))
                    result.Add(value);
            }

            return result;
        }

        private static string ExtractSection(string text, string tag)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var startTag = "[" + tag + "]";
            var endTag = "[/" + tag + "]";
            var start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;
            start += startTag.Length;

            var end = text.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                end = text.Length;

            return text.Substring(start, end - start);
        }

        private sealed class AdventureGroupTables
        {
            public Dictionary<int, int> PointByLevel { get; } = new Dictionary<int, int>();
            public List<int> ManageLevelThresholds { get; } = new List<int>();
            public int ManageLevelMax { get; set; }
            public Dictionary<int, int> ExpBonusByLevel { get; } = new Dictionary<int, int>();
            public Dictionary<int, int> GoldBonusByLevel { get; } = new Dictionary<int, int>();
            public Dictionary<int, int> ManageOptionByLevel { get; } = new Dictionary<int, int>();
        }
    }
}
