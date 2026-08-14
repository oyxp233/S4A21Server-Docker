using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Shop
{
    public sealed class CeraShopProductEntry
    {
        public int ProductId { get; set; }

        public int ItemTemplateId { get; set; }

        public int Count { get; set; }

        // 金币价(标准段 +3 列); 0 表示该商品不用金币。
        public int GoldPrice { get; set; }

        // 点券价(标准段 +5 列 / visual +5 / package +4)。胜点(+4)暂不实现, 不解析。
        public int CoinPrice { get; set; }

        public string Section { get; set; }

        public int DurationDays { get; set; }
    }

    public static class CeraShopProductCatalog
    {
        private static readonly Lazy<CatalogData> Data = new Lazy<CatalogData>(Load);

        public static bool TryResolve(int productId, out CeraShopProductEntry entry)
        {
            return Data.Value.Products.TryGetValue(productId, out entry);
        }

        // [buy only cera]: 仅可用点券(cera)购买的物品。
        public static bool IsBuyOnlyCera(int itemTemplateId)
        {
            return Data.Value.BuyOnlyCera.Contains(itemTemplateId);
        }

        // [buy only cera point]: 仅可用代币券(欢乐代币券+代币券)购买的物品。
        public static bool IsBuyOnlyCeraPoint(int itemTemplateId)
        {
            return Data.Value.BuyOnlyCeraPoint.Contains(itemTemplateId);
        }

        internal static bool TryFindBuyOnlyCeraProduct(out CeraShopProductEntry entry)
        {
            entry = null;
            foreach (var candidate in Data.Value.Products.Values)
            {
                if (candidate.ItemTemplateId <= 0
                    || !Data.Value.BuyOnlyCera.Contains(candidate.ItemTemplateId))
                    continue;
                if (entry != null && candidate.ProductId >= entry.ProductId)
                    continue;

                entry = candidate;
            }

            return entry != null;
        }

        private static CatalogData Load()
        {
            var content = ReadCatalogText();
            var entries = new Dictionary<int, CeraShopProductEntry>();
            // cerashop.etc: 客户端购买实际使用的商品定义 (commodityNo->itemId)。
            // 各段格式: 标准段 stride=9 (commodityNo itemId count 金币 胜点 点券 name 0 0),
            //          visual stride=8(+3时长 +5点券), package stride=11(价格在 col4), avatar stride=6。
            ParseStandardSection(content, "item", 9, entries);
            ParseStandardSection(content, "premium", 9, entries);
            ParseStandardSection(content, "creature", 9, entries);
            ParseStandardSection(content, "coin", 9, entries);
            ParseStandardSection(content, "material", 9, entries);
            ParseStandardSection(content, "recoveryitem", 9, entries);
            ParseStandardSection(content, "visual", 8, entries);
            ParseStandardSection(content, "package", 11, entries);
            // [regular package]: 与 [package] 同为 stride=11 布局(价格在 col4),
            // 收录商城中的常规礼包/幸运礼盒等商品(如 强化成功幸运礼盒 102661 / 增幅成功幸运礼盒 102660)。
            // 缺此解析会导致该段商品在 TryResolve 时"product not found in cerashop.etc",
            // 购买直接失败并被客户端显示为通用"物品栏空间不足"错误。
            ParseStandardSection(content, "regular package", 11, entries);
            // [community package]: 与 [package] 同为 stride=11(价格在 col4), 收录婚庆/社区礼包
            // (如 结婚戒指礼包 102317/102318)。缺此解析同 [regular package] 老 bug: TryResolve product not found → 购买失败。
            // 角色服务商品通常由专用服务路径处理；保留目录项以便混合礼包回落到通用奖励链。
            ParseStandardSection(content, "community package", 11, entries);
            // 角色服务段使用独立字段布局；保留普通目录项可避免混合服务礼包回落时丢失奖励。
            ParseSelectableCharacterPremiumSection(content, entries);
            ParseCharacterPremiumPackageSection(content, entries);
            ParseAvatarSection(content, entries);

            var data = new CatalogData
            {
                Products = entries,
                BuyOnlyCera = ParseIdSet(content, "buy only cera"),
                BuyOnlyCeraPoint = ParseIdSet(content, "buy only cera point"),
            };

            FileLogger.Log($"[CeraShopProductCatalog] Loaded {entries.Count} products, buyOnlyCera={data.BuyOnlyCera.Count}, buyOnlyCeraPoint={data.BuyOnlyCeraPoint.Count} from cerashop.etc");
            return data;
        }

        // 解析仅一串整数 itemId 的段(如 [buy only cera] / [buy only cera point])。
        private static HashSet<int> ParseIdSet(string content, string section)
        {
            var set = new HashSet<int>();
            foreach (var tok in TokenizeSection(content, section))
            {
                if (TryParseInt(tok, out var id) && id > 0)
                    set.Add(id);
            }
            return set;
        }

        private sealed class CatalogData
        {
            public Dictionary<int, CeraShopProductEntry> Products { get; set; }

            public HashSet<int> BuyOnlyCera { get; set; }

            public HashSet<int> BuyOnlyCeraPoint { get; set; }
        }

        private static void ParseStandardSection(string content, string section, int stride, Dictionary<int, CeraShopProductEntry> entries)
        {
            var tokens = TokenizeSection(content, section);
            if (tokens.Count == 0)
                return;

            for (var i = 0; i + stride - 1 < tokens.Count; i += stride)
            {
                if (!TryParseInt(tokens[i], out var productId) || productId <= 0)
                    continue;
                if (!TryParseInt(tokens[i + 1], out var itemTemplateId) || itemTemplateId <= 0)
                    continue;
                if (!TryParseInt(tokens[i + 2], out var count) || count <= 0)
                    count = 1;

                var priceIndex = (section == "package" || section == "regular package" || section == "community package") ? i + 4 : i + 5;
                if (!TryParseInt(tokens[priceIndex], out var coinPrice) || coinPrice < 0)
                    coinPrice = 0;

                // 标准段(stride 9): +3 金币, +4 胜点(忽略), +5 点券。
                // visual(8)/package(11) 的 +3 不是金币(时长/其它), 不取金币。
                var goldPrice = 0;
                if (stride == 9 && (!TryParseInt(tokens[i + 3], out goldPrice) || goldPrice < 0))
                    goldPrice = 0;

                var durationDays = 0;
                if (string.Equals(section, "visual", StringComparison.OrdinalIgnoreCase))
                    TryParseInt(tokens[i + 3], out durationDays);

                entries[productId] = new CeraShopProductEntry
                {
                    ProductId = productId,
                    ItemTemplateId = itemTemplateId,
                    Count = count,
                    GoldPrice = goldPrice,
                    CoinPrice = coinPrice,
                    Section = section,
                    DurationDays = Math.Max(0, durationDays),
                };
            }
        }

        private static void ParseAvatarSection(string content, Dictionary<int, CeraShopProductEntry> entries)
        {
            var tokens = TokenizeSection(content, "avatar");
            for (var i = 0; i + 5 < tokens.Count; i += 6)
            {
                if (!TryParseInt(tokens[i], out var productId) || productId <= 0)
                    continue;
                if (!TryParseInt(tokens[i + 1], out var itemTemplateId) || itemTemplateId <= 0)
                    continue;
                if (!TryParseInt(tokens[i + 2], out var count) || count <= 0)
                    count = 1;

                var coinPrice = 0;
                TryParseInt(tokens[i + 5], out coinPrice);
                entries[productId] = new CeraShopProductEntry
                {
                    ProductId = productId,
                    ItemTemplateId = itemTemplateId,
                    Count = count,
                    CoinPrice = Math.Max(0, coinPrice),
                    Section = "avatar",
                };
            }
        }

        private static void ParseSelectableCharacterPremiumSection(
            string content,
            Dictionary<int, CeraShopProductEntry> entries)
        {
            var tokens = TokenizeSection(content, "selectable character premium");
            for (var i = 0; i + 8 < tokens.Count; i += 9)
            {
                if (!TryParseInt(tokens[i], out var productId) || productId <= 0
                    || !TryParseInt(tokens[i + 1], out var itemTemplateId) || itemTemplateId <= 0)
                    continue;

                TryParseInt(tokens[i + 3], out var durationDays);
                TryParseInt(tokens[i + 5], out var coinPrice);
                entries[productId] = new CeraShopProductEntry
                {
                    ProductId = productId,
                    ItemTemplateId = itemTemplateId,
                    Count = 1,
                    CoinPrice = Math.Max(0, coinPrice),
                    Section = "selectable character premium",
                    DurationDays = Math.Max(0, durationDays),
                };
            }
        }

        private static void ParseCharacterPremiumPackageSection(
            string content,
            Dictionary<int, CeraShopProductEntry> entries)
        {
            var tokens = TokenizeSection(content, "charac premium package");
            for (var i = 0; i + 8 < tokens.Count; i += 9)
            {
                if (!TryParseInt(tokens[i], out var productId) || productId <= 0
                    || !TryParseInt(tokens[i + 1], out var itemTemplateId) || itemTemplateId <= 0)
                    continue;

                TryParseInt(tokens[i + 2], out var coinPrice);
                TryParseInt(tokens[i + 7], out var durationDays);
                entries[productId] = new CeraShopProductEntry
                {
                    ProductId = productId,
                    ItemTemplateId = itemTemplateId,
                    Count = 1,
                    CoinPrice = Math.Max(0, coinPrice),
                    Section = "charac premium package",
                    DurationDays = Math.Max(0, durationDays),
                };
            }
        }

        private static List<string> TokenizeSection(string content, string section)
        {
            var sectionText = ExtractSection(content, section);
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(sectionText))
                return tokens;

            for (var i = 0; i < sectionText.Length;)
            {
                while (i < sectionText.Length && char.IsWhiteSpace(sectionText[i]))
                    i++;
                if (i >= sectionText.Length)
                    break;

                if (sectionText[i] == '`')
                {
                    var end = sectionText.IndexOf('`', i + 1);
                    if (end < 0)
                        end = sectionText.Length - 1;
                    tokens.Add(sectionText.Substring(i + 1, end - i - 1));
                    i = end + 1;
                    continue;
                }

                var start = i;
                while (i < sectionText.Length && !char.IsWhiteSpace(sectionText[i]))
                    i++;
                tokens.Add(sectionText.Substring(start, i - start));
            }

            return tokens;
        }

        private static string ExtractSection(string content, string section)
        {
            if (string.IsNullOrEmpty(content))
                return string.Empty;

            var startTag = "[" + section + "]";
            var endTag = "[/" + section + "]";
            var start = content.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;
            start += startTag.Length;

            var end = content.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
            return end > start ? content.Substring(start, end - start) : content.Substring(start);
        }

        private static bool TryParseInt(string value, out int result)
        {
            return int.TryParse((value ?? string.Empty).Trim(), out result);
        }

        private static string ReadCatalogText()
        {
            try
            {
                return PvfArchiveAccessor.ReadText("etc/cerashop.etc");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CeraShopProductCatalog] PVF read failed, trying docs fallback: {ex.Message}");
            }

            var directory = AppDomain.CurrentDomain.BaseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(directory); i++)
            {
                var candidate = Path.Combine(directory, "docs", "cerashop.etc");
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                directory = Directory.GetParent(directory)?.FullName;
            }

            throw new FileNotFoundException("Cannot find etc/cerashop.etc in PVF or docs/cerashop.etc fallback.");
        }
    }
}
