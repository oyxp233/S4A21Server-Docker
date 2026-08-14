using System;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryCargoUpgradeRule
    {
        private static readonly ushort[] PersonalCargoCapacityTiers =
        {
            24, 40, 56, 72, 88, 104, 120, 136, 152
        };

        private static readonly Regex PersonalCargoUpgradePathRegex = new Regex(
            @"(?:^|/)safe_upgradekit(?<tier>\d*)\.stk$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex PersonalCargoCapacityTextRegex = new Regex(
            @"(?<capacity>\d+)\s*\u683c",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static bool TryResolvePersonalCargoUpgradeTarget(int itemTemplateId, out ushort targetCapacity)
        {
            targetCapacity = 0;
            if (itemTemplateId <= 0)
                return false;

            var entry = ItemMetadataResolver.GetStackableEntry(itemTemplateId);
            var path = NormalizeStackablePath(entry?.FilePath);
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (path.StartsWith("cash/chn_account_cargo/", StringComparison.OrdinalIgnoreCase))
                return false;

            if (TryResolvePersonalCargoUpgradeTargetFromPath(path, out targetCapacity))
                return true;

            var stackable = StackableItemProvider.Load(itemTemplateId);
            if (!LooksLikePersonalCargoUpgradeTool(stackable?.Name, stackable?.Explain))
                return false;

            return TryResolvePersonalCargoUpgradeTargetFromText(stackable.Explain, out targetCapacity);
        }

        internal static bool IsAccountCargoUpgradeToolItem(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return false;

            var entry = ItemMetadataResolver.GetStackableEntry(itemTemplateId);
            var path = entry?.FilePath;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return path.Replace('\\', '/')
                .StartsWith("cash/chn_account_cargo/account_cargo", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsPersonalCargoCapacityTier(ushort capacity)
        {
            foreach (var tier in PersonalCargoCapacityTiers)
            {
                if (tier == capacity)
                    return true;
            }

            return false;
        }

        internal static ushort NormalizePersonalCargoCapacity(ushort capacity)
        {
            return capacity == 0 ? CargoModel.DefaultCapacity : capacity;
        }

        private static bool TryResolvePersonalCargoUpgradeTargetFromPath(string path, out ushort targetCapacity)
        {
            targetCapacity = 0;
            var match = PersonalCargoUpgradePathRegex.Match(path);
            if (!match.Success)
                return false;

            var tierText = match.Groups["tier"].Value;
            var tierIndex = 0;
            if (!string.IsNullOrWhiteSpace(tierText))
            {
                if (!int.TryParse(tierText, out var oneBasedTier) || oneBasedTier <= 0)
                    return false;

                tierIndex = oneBasedTier - 1;
            }

            if (tierIndex < 0 || tierIndex >= PersonalCargoCapacityTiers.Length)
                return false;

            targetCapacity = PersonalCargoCapacityTiers[tierIndex];
            return true;
        }

        private static bool TryResolvePersonalCargoUpgradeTargetFromText(string text, out ushort targetCapacity)
        {
            targetCapacity = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            foreach (Match match in PersonalCargoCapacityTextRegex.Matches(text))
            {
                if (!ushort.TryParse(match.Groups["capacity"].Value, out var capacity))
                    continue;

                if (!IsPersonalCargoCapacityTier(capacity))
                    continue;

                targetCapacity = capacity;
                return true;
            }

            return false;
        }

        private static bool LooksLikePersonalCargoUpgradeTool(string name, string explain)
        {
            var text = ((name ?? string.Empty) + " " + (explain ?? string.Empty)).Replace("`", string.Empty);
            if (text.IndexOf("\u8d26\u53f7\u91d1\u5e93", StringComparison.Ordinal) >= 0
                || text.IndexOf("\u5e10\u53f7\u91d1\u5e93", StringComparison.Ordinal) >= 0)
                return false;

            return text.IndexOf("\u91d1\u5e93", StringComparison.Ordinal) >= 0
                && (text.IndexOf("\u5347\u7ea7", StringComparison.Ordinal) >= 0
                    || text.IndexOf("\u589e\u52a0", StringComparison.Ordinal) >= 0
                    || text.IndexOf("\u7a7a\u95f4", StringComparison.Ordinal) >= 0);
        }

        private static string NormalizeStackablePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Replace('\\', '/').Trim();
        }
    }
}
