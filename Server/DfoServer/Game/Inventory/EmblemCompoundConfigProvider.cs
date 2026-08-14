using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal static class EmblemCompoundConfigProvider
    {
        private const string PvfPath = "etc/emblemcompound.etc";
        private static readonly Regex IntegerToken = new Regex(@"[-+]?\d+", RegexOptions.Compiled);
        private static readonly Lazy<Dictionary<(int Grade, int Count), int>> BoosterMappings =
            new Lazy<Dictionary<(int Grade, int Count), int>>(LoadMappings);

        internal static bool TryRollReward(int grade, int inputCount, out int boosterItemTemplateId, out int rewardItemTemplateId, out int rewardCount)
        {
            boosterItemTemplateId = 0;
            rewardItemTemplateId = 0;
            rewardCount = 0;

            if (!BoosterMappings.Value.TryGetValue((grade, inputCount), out boosterItemTemplateId))
                return false;

            var booster = StackableItemProvider.Load(boosterItemTemplateId);
            if (booster?.BoosterRewards == null || booster.BoosterRewards.Count == 0)
                return false;

            var group = booster.BoosterRewards
                .Where(entry => entry != null && entry.ItemId > 0 && entry.Count > 0 && entry.Weight > 0)
                .GroupBy(entry => entry.Group)
                .OrderBy(entries => entries.Key)
                .FirstOrDefault();
            if (group == null)
                return false;

            long totalWeight = group.Sum(entry => (long)Math.Max(0, entry.Weight));
            if (totalWeight <= 0)
                return false;

            var roll = Random.Shared.NextInt64(totalWeight);
            foreach (var entry in group)
            {
                roll -= Math.Max(0, entry.Weight);
                if (roll >= 0)
                    continue;

                rewardItemTemplateId = entry.ItemId;
                rewardCount = Math.Max(1, entry.Count);
                return true;
            }

            return false;
        }

        internal static Dictionary<(int Grade, int Count), int> ParseMappings(string content)
        {
            var mappings = new Dictionary<(int Grade, int Count), int>();
            var section = Regex.Match(content ?? string.Empty,
                @"\[emblem compound info\](.*?)\[/emblem compound info\]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!section.Success)
                return mappings;

            var values = new List<int>();
            foreach (Match token in IntegerToken.Matches(section.Groups[1].Value))
            {
                if (int.TryParse(token.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }

            var offset = 0;
            while (offset + 2 <= values.Count)
            {
                var grade = values[offset++];
                var maxInputCount = values[offset++];
                if (grade <= 0 || maxInputCount < 2 || offset + maxInputCount - 1 > values.Count)
                    break;

                for (var inputCount = 2; inputCount <= maxInputCount; inputCount++)
                {
                    var boosterItemTemplateId = values[offset++];
                    if (boosterItemTemplateId > 0)
                        mappings[(grade, inputCount)] = boosterItemTemplateId;
                }
            }

            return mappings;
        }

        private static Dictionary<(int Grade, int Count), int> LoadMappings()
        {
            try
            {
                var mappings = ParseMappings(PvfArchiveAccessor.ReadText(PvfPath));
                FileLogger.Log($"[EmblemCompoundConfig] path={PvfPath} mappings={mappings.Count} " +
                    $"[{string.Join(",", mappings.OrderBy(pair => pair.Key.Grade).ThenBy(pair => pair.Key.Count).Select(pair => $"g{pair.Key.Grade}x{pair.Key.Count}=0x{pair.Value:X8}"))}]");
                return mappings;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[EmblemCompoundConfig] load failed: {ex.Message}");
                return new Dictionary<(int Grade, int Count), int>();
            }
        }
    }
}
