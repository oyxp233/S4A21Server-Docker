using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal sealed class MonsterCardBindEntry
    {
        public int ItemId { get; init; }
        public int Rarity { get; init; }
        public int Weight { get; init; }
    }

    internal sealed class MonsterCardBindConfig
    {
        public IReadOnlyDictionary<int, int> MixProbability { get; init; }
        public IReadOnlyDictionary<int, int> BinderRates { get; init; }
        public IReadOnlyList<MonsterCardBindEntry> BindList { get; init; }

        internal const int ProbabilityDenominator = 100000;

        internal bool TryCalculateSuccessWeight(int firstRarity, int secondRarity, int bindType, out int weight)
        {
            weight = 0;
            if (firstRarity < 0 || firstRarity > 3 || secondRarity < 0 || secondRarity > 3
                || !BinderRates.TryGetValue(bindType, out var binderRate))
                return false;

            var low = Math.Min(firstRarity, secondRarity);
            var high = Math.Max(firstRarity, secondRarity);
            long value = ProbabilityDenominator;
            var end = low == high ? low + 1 : high;
            for (var rarity = low; rarity < end; rarity++)
            {
                if (!MixProbability.TryGetValue(rarity, out var step))
                    return false;
                value = value * Math.Max(0, step) / ProbabilityDenominator;
            }

            weight = (int)Math.Min(ProbabilityDenominator, value * Math.Max(0, binderRate) / 100);
            return true;
        }

        internal bool TrySelectResult(int rarity, Func<int, int> next, out MonsterCardBindEntry selected)
        {
            selected = null;
            if (next == null)
                return false;
            var candidates = BindList.Where(x => x.Rarity == rarity && x.Weight > 0).ToList();
            var total = candidates.Sum(x => (long)x.Weight);
            if (total <= 0 || total > int.MaxValue)
                return false;
            var roll = next((int)total);
            foreach (var entry in candidates)
            {
                if (roll < entry.Weight)
                {
                    selected = entry;
                    return true;
                }
                roll -= entry.Weight;
            }
            return false;
        }
    }

    internal static class MonsterCardBindConfigProvider
    {
        private static readonly Lazy<MonsterCardBindConfig> Config =
            new Lazy<MonsterCardBindConfig>(Load, true);

        internal static MonsterCardBindConfig Current => Config.Value;

        private static MonsterCardBindConfig Load()
        {
            var expertText = ReadFirst(
                "character/expertjob/enchanter.exj",
                "enchanter.exj",
                "etc/enchanter.exj",
                "etc/expertjob/enchanter.exj",
                "etc/expert_job/enchanter.exj");
            var eventText = ReadFirst(
                "event/chn_event/chn_composecard.evt",
                "Event/chn_event/chn_composecard.evt");

            var config = new MonsterCardBindConfig
            {
                MixProbability = ParseMixProbability(eventText),
                BinderRates = ParseBinderRates(expertText),
                BindList = ParseBindList(expertText),
            };

            FileLogger.Log(
                $"[MonsterCardBindConfig] loaded mix={config.MixProbability.Count} " +
                $"binder={config.BinderRates.Count} cards={config.BindList.Count}");
            return config;
        }

        private static string ReadFirst(params string[] paths)
        {
            Exception last = null;
            foreach (var path in paths)
            {
                try
                {
                    return PvfArchiveAccessor.ReadText(path);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw new InvalidOperationException(
                $"Required monster-card PVF file was not found: {string.Join(", ", paths)}", last);
        }

        private static Dictionary<int, int> ParseMixProbability(string text)
        {
            var values = ParseNumbers(ReadSection(text, "mix probability"));
            var result = new Dictionary<int, int>();
            for (var i = 0; i + 1 < values.Count; i += 2)
                result[values[i]] = Math.Max(0, values[i + 1]);
            return result;
        }

        private static Dictionary<int, int> ParseBinderRates(string text)
        {
            var values = ParseNumbers(ReadSection(text, "monstercard bind info"));
            var result = new Dictionary<int, int>();
            for (var i = 0; i + 2 < values.Count; i += 3)
                result[values[i]] = Math.Max(0, values[i + 1]);
            return result;
        }

        private static List<MonsterCardBindEntry> ParseBindList(string text)
        {
            var values = ParseNumbers(ReadSection(text, "monstercard bind list"));
            var result = new List<MonsterCardBindEntry>();
            for (var i = 0; i + 2 < values.Count; i += 3)
            {
                if (values[i] <= 0 || values[i + 1] < 0 || values[i + 2] < 0)
                    continue;

                result.Add(new MonsterCardBindEntry
                {
                    ItemId = values[i],
                    Rarity = values[i + 1],
                    Weight = values[i + 2],
                });
            }
            return result;
        }

        private static string ReadSection(string text, string name)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var pattern = $@"\[{Regex.Escape(name)}\](.*?)\[/{Regex.Escape(name)}\]";
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static List<int> ParseNumbers(string text)
        {
            var result = new List<int>();
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"-?\d+"))
            {
                if (int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    result.Add(value);
            }
            return result;
        }
    }
}
