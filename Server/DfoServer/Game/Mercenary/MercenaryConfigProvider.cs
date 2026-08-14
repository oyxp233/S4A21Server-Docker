using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Mercenary
{
    public static class MercenaryConfigProvider
    {
        private static readonly Lazy<MercenaryConfig> CurrentConfig =
            new Lazy<MercenaryConfig>(() => Parse(PvfArchiveAccessor.ReadText("etc/mercenary.etc")));

        private static readonly Regex TokenPattern = new Regex(
            @"\[[^\]]+\]|`[^`]*`|[-+]?\d+(?:\.\d+)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static MercenaryConfig Current => CurrentConfig.Value;

        public static void Warmup()
        {
            _ = Current;
        }

        internal static MercenaryConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("etc/mercenary.etc is empty");

            var tokens = TokenPattern.Matches(text)
                .Cast<Match>()
                .Select(match => match.Value)
                .ToList();
            var config = new MercenaryConfig();
            var position = 0;

            while (position < tokens.Count)
            {
                var tag = NormalizeTag(tokens[position++]);
                switch (tag)
                {
                    case "[base time unit]":
                        config.BaseTimeUnitSeconds = ReadInt(tokens, ref position, tag);
                        break;

                    case "[level base golditem prob]":
                        config.LevelRewards.Add(new MercenaryLevelReward
                        {
                            MinimumLevel = ReadInt(tokens, ref position, tag),
                            BaseGoldPerHour = ReadInt(tokens, ref position, tag),
                            ItemProbabilityPerHour = ReadInt(tokens, ref position, tag),
                        });
                        break;

                    case "[mercenary default droprate per hour]":
                        config.DefaultDropRatePerHour = ReadInt(tokens, ref position, tag);
                        break;

                    case "[mercenary reward per period]":
                        ParsePeriodSection(tokens, ref position, config);
                        break;

                    case "[mercenary reward avatar bonus]":
                        ParseAvatarSection(tokens, ref position, config);
                        break;

                    case "[mercenary reward critical]":
                        ParseCriticalSection(tokens, ref position, config);
                        break;

                    case "[competition area]":
                        config.Areas.Add(ParseArea(tokens, ref position, config.Areas.Count));
                        break;
                }
            }

            Validate(config);
            return config;
        }

        private static void ParsePeriodSection(
            IReadOnlyList<string> tokens,
            ref int position,
            MercenaryConfig config)
        {
            byte index = 0;
            while (position < tokens.Count && NormalizeTag(tokens[position]) != "[/mercenary reward per period]")
            {
                var hours = ReadInt(tokens, ref position, "[mercenary reward per period]");
                var multiplier = ReadDouble(tokens, ref position, "[mercenary reward per period]");
                config.Periods.Add(new MercenaryPeriodOption
                {
                    Index = index++,
                    Hours = hours,
                    BonusMultiplier = multiplier,
                });
            }
            SkipClosingTag(tokens, ref position, "[/mercenary reward per period]");
        }

        private static void ParseAvatarSection(
            IReadOnlyList<string> tokens,
            ref int position,
            MercenaryConfig config)
        {
            while (position < tokens.Count && NormalizeTag(tokens[position]) != "[/mercenary reward avatar bonus]")
            {
                var tier = ReadInt(tokens, ref position, "[mercenary reward avatar bonus]");
                var multiplier = ReadDouble(tokens, ref position, "[mercenary reward avatar bonus]");
                config.AvatarBonuses[tier] = multiplier;
            }
            SkipClosingTag(tokens, ref position, "[/mercenary reward avatar bonus]");
        }

        private static void ParseCriticalSection(
            IReadOnlyList<string> tokens,
            ref int position,
            MercenaryConfig config)
        {
            while (position < tokens.Count && NormalizeTag(tokens[position]) != "[/mercenary reward critical]")
            {
                config.CriticalOptions.Add(new MercenaryCriticalOption
                {
                    Weight = ReadInt(tokens, ref position, "[mercenary reward critical]"),
                    Multiplier = ReadDouble(tokens, ref position, "[mercenary reward critical]"),
                });
            }
            SkipClosingTag(tokens, ref position, "[/mercenary reward critical]");
        }

        private static MercenaryCompetitionArea ParseArea(
            IReadOnlyList<string> tokens,
            ref int position,
            int areaIndex)
        {
            if (areaIndex > byte.MaxValue)
                throw new InvalidOperationException("mercenary area count exceeds byte range");

            var area = new MercenaryCompetitionArea { Index = (byte)areaIndex };
            while (position < tokens.Count)
            {
                var tag = NormalizeTag(tokens[position++]);
                switch (tag)
                {
                    case "[/competition area]":
                        return area;
                    case "[world map]":
                        area.WorldMapId = ReadInt(tokens, ref position, tag);
                        break;
                    case "[visible]":
                        area.Visible = ReadInt(tokens, ref position, tag) != 0;
                        break;
                    case "[area level]":
                        area.MinimumLevel = ReadInt(tokens, ref position, tag);
                        break;
                    case "[reward group]":
                        area.RewardGroups.Add(ParseRewardGroup(tokens, ref position));
                        break;
                }
            }

            throw new InvalidOperationException("unterminated [competition area]");
        }

        private static MercenaryRewardGroup ParseRewardGroup(
            IReadOnlyList<string> tokens,
            ref int position)
        {
            var group = new MercenaryRewardGroup
            {
                Weight = ReadInt(tokens, ref position, "[reward group]"),
                MessageKey = ReadString(tokens, ref position, "[reward group]"),
            };

            while (position < tokens.Count)
            {
                var tag = NormalizeTag(tokens[position++]);
                switch (tag)
                {
                    case "[/reward group]":
                        return group;
                    case "[item index]":
                        ParseWeightedEntries(tokens, ref position, "[/item index]", group.Items);
                        break;
                    case "[mob index]":
                        ParseWeightedEntries(tokens, ref position, "[/mob index]", group.Monsters);
                        break;
                }
            }

            throw new InvalidOperationException("unterminated [reward group]");
        }

        private static void ParseWeightedEntries(
            IReadOnlyList<string> tokens,
            ref int position,
            string closingTag,
            ICollection<MercenaryWeightedEntry> target)
        {
            while (position < tokens.Count && NormalizeTag(tokens[position]) != closingTag)
            {
                target.Add(new MercenaryWeightedEntry
                {
                    Value = ReadInt(tokens, ref position, closingTag),
                    Weight = ReadInt(tokens, ref position, closingTag),
                });
            }
            SkipClosingTag(tokens, ref position, closingTag);
        }

        private static void Validate(MercenaryConfig config)
        {
            config.LevelRewards.Sort((left, right) => left.MinimumLevel.CompareTo(right.MinimumLevel));

            if (config.BaseTimeUnitSeconds <= 0)
                throw new InvalidOperationException("mercenary base time unit must be positive");
            if (config.LevelRewards.Count == 0
                || config.LevelRewards.Any(entry => entry.MinimumLevel <= 0
                    || entry.BaseGoldPerHour < 0
                    || entry.ItemProbabilityPerHour < 0))
                throw new InvalidOperationException("mercenary level reward table is invalid");
            if (config.Periods.Count == 0
                || config.Periods.Any(entry => entry.Hours <= 0 || entry.BonusMultiplier <= 0))
                throw new InvalidOperationException("mercenary period table is invalid");
            if (config.AvatarBonuses.Count == 0 || !config.AvatarBonuses.ContainsKey(0))
                throw new InvalidOperationException("mercenary avatar bonus table requires tier 0");
            if (config.Areas.Count == 0 || !config.Areas.Any(area => area.IsRandom))
                throw new InvalidOperationException("mercenary area table requires a random-area entry");
            if (!config.Areas.Any(area => !area.IsRandom && area.MinimumLevel > 0 && area.RewardGroups.Count > 0))
                throw new InvalidOperationException("mercenary area table has no real reward area");
            if (config.Areas.Any(area => !area.IsRandom
                && area.RewardGroups.Any(group => group.Weight <= 0
                    || (group.Items.Count == 0 && group.Monsters.Count == 0))))
                throw new InvalidOperationException("mercenary reward group is invalid");

            if (config.CriticalOptions.Count == 0)
            {
                config.CriticalOptions.Add(new MercenaryCriticalOption
                {
                    Weight = 10000,
                    Multiplier = 1.0,
                });
            }
            if (config.CriticalOptions.Any(entry => entry.Weight <= 0 || entry.Multiplier < 0))
                throw new InvalidOperationException("mercenary critical table is invalid");
        }

        private static int ReadInt(IReadOnlyList<string> tokens, ref int position, string context)
        {
            if (position >= tokens.Count
                || !int.TryParse(tokens[position], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException($"expected integer after {context}");
            position++;
            return value;
        }

        private static double ReadDouble(IReadOnlyList<string> tokens, ref int position, string context)
        {
            if (position >= tokens.Count
                || !double.TryParse(tokens[position], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException($"expected number after {context}");
            position++;
            return value;
        }

        private static string ReadString(IReadOnlyList<string> tokens, ref int position, string context)
        {
            if (position >= tokens.Count || !tokens[position].StartsWith("`", StringComparison.Ordinal))
                throw new InvalidOperationException($"expected string after {context}");
            return tokens[position++].Trim('`');
        }

        private static void SkipClosingTag(
            IReadOnlyList<string> tokens,
            ref int position,
            string expected)
        {
            if (position >= tokens.Count || NormalizeTag(tokens[position]) != expected)
                throw new InvalidOperationException($"missing {expected}");
            position++;
        }

        private static string NormalizeTag(string token)
            => token != null && token.StartsWith("[", StringComparison.Ordinal)
                ? token.Trim().ToLowerInvariant()
                : string.Empty;
    }
}
