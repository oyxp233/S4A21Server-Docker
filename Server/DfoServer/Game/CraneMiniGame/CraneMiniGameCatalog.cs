using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.Game.CraneMiniGame
{
    internal sealed class CraneMiniGameItem
    {
        public int CatalogIndex { get; set; }
        public int ItemId { get; set; }
        public int Count { get; set; }
        public double ViewWeight { get; set; }
        public double PickChance { get; set; }
    }

    internal sealed class CraneMiniGameCatalog
    {
        private static readonly Regex FieldRegex = new Regex(
            @"^\s*\[(?<name>[^\]]+)\]\s*(?<value>.*)$",
            RegexOptions.Compiled);

        public int ViewCount { get; private set; }
        public int MaterialItemId { get; private set; }
        public int MaterialCount { get; private set; }
        public int ExchangeMaterialItemId { get; private set; }
        public int ExchangeMaterialCount { get; private set; }
        public IReadOnlyList<CraneMiniGameItem> Items { get; private set; }

        internal static CraneMiniGameCatalog Load()
            => Parse(PvfArchiveAccessor.ReadText("etc/craneminigameitem.etc"));

        internal static CraneMiniGameCatalog Parse(string text)
        {
            var items = new List<CraneMiniGameItem>();
            CraneMiniGameItem current = null;
            var catalog = new CraneMiniGameCatalog();
            string pendingField = null;
            var readingNeedMaterial = false;

            foreach (var rawLine in (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = FieldRegex.Match(rawLine);
                if (readingNeedMaterial)
                {
                    if (match.Success && match.Groups["name"].Value.Trim().Equals("/need material", StringComparison.OrdinalIgnoreCase))
                    {
                        readingNeedMaterial = false;
                        continue;
                    }

                    ParseExchangeMaterialPairs(rawLine, catalog);
                    continue;
                }
                string name;
                string valueText;
                if (match.Success)
                {
                    name = match.Groups["name"].Value.Trim().ToLowerInvariant();
                    valueText = match.Groups["value"].Value;
                    if (name == "need material")
                    {
                        ParseExchangeMaterialPairs(valueText, catalog);
                        readingNeedMaterial = true;
                        continue;
                    }
                    if (name.StartsWith("/", StringComparison.Ordinal))
                    {
                        pendingField = null;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(valueText))
                    {
                        pendingField = name;
                        continue;
                    }
                    pendingField = null;
                }
                else if (pendingField != null)
                {
                    name = pendingField;
                    valueText = rawLine;
                    pendingField = null;
                }
                else
                {
                    continue;
                }

                var values = Regex.Matches(valueText, @"-?\d+(?:\.\d+)?");
                if (name == "viewcnt" && TryInt(values, 0, out var viewCount))
                {
                    catalog.ViewCount = viewCount;
                }
                else if (name == "item" && TryInt(values, 0, out var itemId))
                {
                    current = new CraneMiniGameItem
                    {
                        CatalogIndex = items.Count,
                        ItemId = itemId,
                        Count = 1,
                    };
                    items.Add(current);
                }
                else if (name == "cnt" && current != null && TryInt(values, 0, out var count))
                {
                    current.Count = count;
                }
                else if (name == "viewratio" && current != null && TryDouble(values, 0, out var viewWeight))
                {
                    current.ViewWeight = viewWeight;
                }
                else if (name == "pickratio" && current != null && TryDouble(values, 0, out var pickChance))
                {
                    current.PickChance = pickChance;
                }
                else if (name == "material"
                    && TryInt(values, 0, out var materialItemId)
                    && TryInt(values, 1, out var materialCount))
                {
                    catalog.MaterialItemId = materialItemId;
                    catalog.MaterialCount = materialCount;
                }
            }

            catalog.Items = items;
            if (catalog.ViewCount <= 0 || catalog.MaterialItemId <= 0 || catalog.MaterialCount <= 0)
                throw new FormatException("etc/craneminigameitem.etc is missing viewCnt or material.");
            if (items.Count < catalog.ViewCount)
                throw new FormatException("etc/craneminigameitem.etc does not contain enough display items.");
            foreach (var item in items)
            {
                if (item.ItemId <= 0
                    || item.Count <= 0
                    || item.Count > short.MaxValue
                    || item.ViewWeight < 0
                    || item.PickChance < 0)
                    throw new FormatException("etc/craneminigameitem.etc contains an invalid item entry.");
            }

            return catalog;
        }

        internal bool TryResolveCoinExchange(int itemTemplateId, out int materialItemId, out int materialCount)
        {
            materialItemId = 0;
            materialCount = 0;
            if (itemTemplateId != MaterialItemId)
                return false;

            materialItemId = ExchangeMaterialItemId;
            materialCount = ExchangeMaterialCount;
            return materialItemId > 0 && materialCount > 0;
        }

        private static void ParseExchangeMaterialPairs(string text, CraneMiniGameCatalog catalog)
        {
            if (catalog == null || string.IsNullOrWhiteSpace(text))
                return;

            var values = Regex.Matches(text, @"-?\d+");
            for (var index = 0; index + 1 < values.Count; index += 2)
            {
                if (TryInt(values, index, out var itemId)
                    && TryInt(values, index + 1, out var count))
                {
                    catalog.ExchangeMaterialItemId = itemId;
                    catalog.ExchangeMaterialCount = count;
                }
            }
        }

        private static bool TryInt(MatchCollection values, int index, out int value)
        {
            value = 0;
            return index < values.Count
                && int.TryParse(values[index].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryDouble(MatchCollection values, int index, out double value)
        {
            value = 0;
            return index < values.Count
                && double.TryParse(values[index].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
