using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.Game.DailyReset
{
    internal enum DailyRefillMode
    {
        RefillToTarget = 0,
        AddUpToStackLimit = 1,
    }

    internal sealed class DailyRefillItemRule
    {
        public int ItemId { get; init; }
        public int Quantity { get; init; }
        public DateTime ExpirationBeijing { get; init; }
        public DailyRefillMode Mode { get; init; }
    }

    internal static class DailyRefillItemPolicy
    {
        internal static int CalculateGrant(DailyRefillItemRule rule, int currentCount, int stackLimit)
        {
            if (rule == null || rule.Quantity <= 0 || stackLimit <= 0)
                return 0;

            currentCount = Math.Max(0, currentCount);
            switch (rule.Mode)
            {
                case DailyRefillMode.RefillToTarget:
                    return Math.Max(0, Math.Min(rule.Quantity, stackLimit) - currentCount);
                case DailyRefillMode.AddUpToStackLimit:
                    return Math.Min(rule.Quantity, Math.Max(0, stackLimit - currentCount));
                default:
                    return 0;
            }
        }
    }

    internal static class PvfDailyRefillItemProvider
    {
        private const string PvfPath = "etc/chn_server_limititemusageinfo.etc";
        private static readonly Lazy<IReadOnlyList<DailyRefillItemRule>> Rules =
            new Lazy<IReadOnlyList<DailyRefillItemRule>>(Load, true);

        internal static IReadOnlyList<DailyRefillItemRule> Current => Rules.Value;

        private static IReadOnlyList<DailyRefillItemRule> Load()
        {
            var parsed = Parse(PvfArchiveAccessor.ReadText(PvfPath), DateTime.UtcNow.AddHours(8));
            FileLogger.Log($"[DailyRefillItem] PVF loaded rules={parsed.Count} path={PvfPath}");
            return parsed;
        }

        internal static IReadOnlyList<DailyRefillItemRule> Parse(string text, DateTime nowBeijing)
        {
            var section = ReadSection(text, "refill item");
            var tokens = new List<string>();
            foreach (Match match in Regex.Matches(section, "`([^`]*)`|-?\\d+"))
                tokens.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Value);

            if (tokens.Count % 4 != 0)
                throw new FormatException($"PVF [{PvfPath}] [refill item] token count {tokens.Count} is not divisible by 4.");

            var result = new List<DailyRefillItemRule>();
            for (var index = 0; index < tokens.Count; index += 4)
            {
                if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
                    || !int.TryParse(tokens[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity)
                    || !DateTime.TryParseExact(
                        tokens[index + 2],
                        "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var expiration)
                    || !int.TryParse(tokens[index + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var modeValue))
                {
                    FileLogger.Log($"[DailyRefillItem] ignored malformed record index={index / 4}");
                    continue;
                }

                if (itemId <= 0 || quantity <= 0 || expiration <= nowBeijing
                    || !Enum.IsDefined(typeof(DailyRefillMode), modeValue))
                    continue;

                result.Add(new DailyRefillItemRule
                {
                    ItemId = itemId,
                    Quantity = quantity,
                    ExpirationBeijing = expiration,
                    Mode = (DailyRefillMode)modeValue,
                });
            }

            return result;
        }

        private static string ReadSection(string text, string name)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var match = Regex.Match(
                text,
                $@"\[{Regex.Escape(name)}\](.*?)\[/{Regex.Escape(name)}\]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
    }
}
