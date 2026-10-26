using System;
using System.Globalization;
using System.Text.RegularExpressions;
using DfoServer.Game.ItemUpgrade;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    internal static class ItemUpgradePotionBonus
    {
        private const int WeightScale = 100000;
        private const string ReinforceType = "[item upgrad buff usual upgrade]";
        private const string AmplifyType = "[item upgrad buff amplify upgrade]";
        private const string LegacyTownPotionType = "[expert town potion]";
        private const string LegacyDefaultPhrase = "提升强化成功率";
        private const string LegacyExplicitPercentPhrase = "强化成功率增加";
        private const int LegacyDefaultWeight = 1000;

        internal static int ResolveActiveAddWeight(
            InventoryService inventory,
            ItemUpgradeMode mode,
            ItemUpgradeScene scene,
            long nowUnixSeconds)
        {
            if (inventory == null
                || (scene != ItemUpgradeScene.Npc && scene != ItemUpgradeScene.Portable))
                return 0;

            var secretPotionWeight = 0;
            var crystalWeight = 0;
            foreach (var state in inventory.ItemStates.BuildActiveSnapshots(ItemStateKinds.Effect, nowUnixSeconds))
            {
                var definition = StackableItemProvider.Load(state.ItemId);
                if (!TryGetBonus(definition, mode, out var weight, out var crystal))
                    continue;

                // Variants of the same secret potion do not multiply the bonus.
                if (crystal)
                    crystalWeight = Math.Max(crystalWeight, weight);
                else
                    secretPotionWeight = Math.Max(secretPotionWeight, weight);
            }

            return (int)Math.Min(WeightScale, (long)secretPotionWeight + crystalWeight);
        }

        internal static bool TryGetBonus(
            StackableItemFile definition,
            ItemUpgradeMode mode,
            out int weight,
            out bool crystal)
        {
            weight = 0;
            crystal = false;
            if (definition == null
                || !definition.HasEffectMaintenance
                || definition.StatChangeDurationMilliseconds <= 0)
                return false;

            var type = StackableItemProvider.NormalizeType(definition.StackableType);
            if (mode == ItemUpgradeMode.Reinforce
                && string.Equals(type, LegacyTownPotionType, StringComparison.OrdinalIgnoreCase))
                return TryGetLegacyReinforceBonus(definition.Explain, out weight, out crystal);

            var expectedType = mode == ItemUpgradeMode.Reinforce ? ReinforceType
                : mode == ItemUpgradeMode.Amplify ? AmplifyType : string.Empty;
            if (expectedType.Length == 0
                || !string.Equals(type, expectedType, StringComparison.OrdinalIgnoreCase))
                return false;

            // PVF: `[item upgrad buff ... upgrade]` 1000 (scale 100000).
            var raw = definition.StackableType ?? string.Empty;
            var closingBracket = raw.LastIndexOf(']');
            if (closingBracket < 0)
                return false;
            var amount = raw.Substring(closingBracket + 1).Trim().Trim('`').Trim();
            return int.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out weight)
                && weight > 0 && weight <= WeightScale;
        }

        // Legacy `[expert town potion]` definitions have no numeric probability field.
        // The explicit percentage lives only in the tooltip; items that merely mention
        // a random event buff (e.g. 10008220) or fatigue recovery must not match.
        private static bool TryGetLegacyReinforceBonus(string explain, out int weight, out bool crystal)
        {
            weight = 0;
            crystal = false;
            if (string.IsNullOrWhiteSpace(explain))
                return false;

            // "10%%" is the escaped form written by the PVF tooltip text.
            var normalized = explain.Replace("%%", "%");
            var explicitPhrases = Regex.Matches(normalized, LegacyExplicitPercentPhrase);
            if (explicitPhrases.Count > 0)
            {
                // Reject multiple percentages and ambiguity with durations or other numbers.
                if (explicitPhrases.Count != 1 || CountPercentSigns(normalized) != 1)
                    return false;

                var match = Regex.Match(normalized,
                    LegacyExplicitPercentPhrase + @"\s*(?<percent>\d+)\s*%");
                if (!match.Success
                    || !int.TryParse(match.Groups["percent"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var percent)
                    || percent < 1 || percent > 100)
                    return false;

                weight = percent * 1000;
                crystal = true;
                return true;
            }

            // 2600025/2749959: the PVF only states that the potion raises the success
            // rate, so 1 percentage point is the local compatibility default.
            if (explain.Contains(LegacyDefaultPhrase, StringComparison.Ordinal))
            {
                weight = LegacyDefaultWeight;
                return true;
            }

            return false;
        }

        private static int CountPercentSigns(string text)
        {
            var count = 0;
            foreach (var ch in text)
                if (ch == '%')
                    count++;
            return count;
        }
    }
}
