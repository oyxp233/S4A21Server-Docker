using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    /// 校验 A21 租赁目录中的装备模板，并读取 PVF 定义的幸运星价格。
    public static class RentalWeaponInventoryMapper
    {
        private const string RentalCatalogPath = "etc/chnrentsystem/rentsysteminfo.etc";

        private sealed class RentalWeaponIdentity
        {
            public int StarPrice { get; set; }
        }

        private static readonly Lazy<Dictionary<int, RentalWeaponIdentity>> IdentityById =
            new Lazy<Dictionary<int, RentalWeaponIdentity>>(BuildIdentityIndex);

        public static bool IsValidInventoryTemplate(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return false;

            return IdentityById.Value.ContainsKey(itemTemplateId);
        }

        public static int GetStarPrice(int inventoryTemplateId)
        {
            if (IdentityById.Value.TryGetValue(inventoryTemplateId, out var identity) && identity.StarPrice > 0)
                return identity.StarPrice;

            return 0;
        }

        private static Dictionary<int, RentalWeaponIdentity> BuildIdentityIndex()
        {
            var byId = new Dictionary<int, RentalWeaponIdentity>();
            var catalog = ParseRentalCatalog(PvfArchiveAccessor.ReadText(RentalCatalogPath));
            if (catalog.Count == 0)
                throw new InvalidOperationException($"PVF {RentalCatalogPath} contains no rental package selections.");

            var lst = LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst"));
            var equipmentIds = new HashSet<int>();
            foreach (var entry in lst.Entries)
            {
                equipmentIds.Add(entry.Id);
            }

            foreach (var item in catalog)
            {
                if (!equipmentIds.Contains(item.Key))
                {
                    throw new InvalidOperationException(
                        $"PVF {RentalCatalogPath} references missing equipment item {item.Key}.");
                }

                byId[item.Key] = new RentalWeaponIdentity { StarPrice = item.Value };
            }

            return byId;
        }

        internal static IReadOnlyDictionary<int, int> ParseRentalCatalog(string text)
        {
            var catalog = new Dictionary<int, int>();
            var inPackageSelection = false;
            foreach (var rawLine in (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line.Equals("[package selection]", StringComparison.OrdinalIgnoreCase))
                {
                    inPackageSelection = true;
                    continue;
                }

                if (line.StartsWith("[/", StringComparison.Ordinal))
                {
                    inPackageSelection = false;
                    continue;
                }

                if (!inPackageSelection)
                    continue;

                var matches = Regex.Matches(line, @"-?\d+");
                if (matches.Count == 0)
                    continue;
                if ((matches.Count & 1) != 0)
                    throw new FormatException($"PVF {RentalCatalogPath} contains an incomplete rental item/price pair: {line}");

                for (var index = 0; index < matches.Count; index += 2)
                {
                    if (!int.TryParse(matches[index].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
                        || !int.TryParse(matches[index + 1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var starPrice)
                        || itemId <= 0
                        || starPrice <= 0)
                    {
                        throw new FormatException($"PVF {RentalCatalogPath} contains an invalid rental item/price pair: {line}");
                    }

                    if (catalog.TryGetValue(itemId, out var previousPrice) && previousPrice != starPrice)
                    {
                        throw new FormatException(
                            $"PVF {RentalCatalogPath} assigns conflicting prices to item {itemId}: {previousPrice} and {starPrice}.");
                    }

                    catalog[itemId] = starPrice;
                }
            }

            return catalog;
        }
    }
}
