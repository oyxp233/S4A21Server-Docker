using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld;
using PvfLib;
using System;

namespace DfoServer.Game.Inventory
{
    internal static class EquipmentRepairPriceProvider
    {
        private const string RepairCostTag = "[repair cost]";
        private const string QuickRepairCostRateTag = "[quick repair cost rate]";
        private static readonly object LoadLock = new object();
        private static float _itemRepairCost = float.NaN;
        private static float _quickRepairRate = float.NaN;   // 快速修理倍率(pricetable.tbl [quick repair cost rate], 如150→1.5)

        // cost = (int)( (repairPrice × (grade+5)/10) × itemRepairCost_ / maxDura × (maxDura - curDura) × upgradeRate [× quickRate] )
        // quickRepair=true: 侧边栏"快速修理"(请求 body 末字节=01), 乘 [quick repair cost rate](150%)。
        public static int CalcRepairCost(int repairPrice, int grade, int maxDurability, int currentDurability, int upgradeLevel, bool quickRepair = false)
        {
            if (repairPrice <= 0 || maxDurability <= 0 || currentDurability >= maxDurability)
                return 0;

            EnsureLoaded();

            var lostDura = maxDurability - currentDurability;
            var basePrice = (float)(repairPrice * (grade + 5) / 10) * _itemRepairCost / maxDurability * lostDura;

            var upgradeRate = GetUpgradeRate(upgradeLevel);
            var cost = basePrice * upgradeRate;
            if (quickRepair)
                cost *= _quickRepairRate;
            return (int)cost;
        }

        private static float GetUpgradeRate(int upgradeLevel)
        {
            var rates = ItemUpgradeTableProvider.GetFile(ItemUpgradeTableKind.Normal)?.RepairCostRatesByUpgradeLevel;
            if (rates == null || rates.Count == 0)
                return 1.0f;

            var index = Math.Max(0, Math.Min(upgradeLevel, rates.Count - 1));
            var rate = rates[index];
            return rate > 0 ? (float)rate : 1.0f;
        }

        private static void EnsureLoaded()
        {
            if (!float.IsNaN(_itemRepairCost))
                return;

            lock (LoadLock)
            {
                if (!float.IsNaN(_itemRepairCost))
                    return;

                try
                {
                    // [repair cost] 和 [quick repair cost rate] 同在 equipment/pricetable.tbl
                    var text = PvfArchiveAccessor.ReadText("equipment/pricetable.tbl");
                    _itemRepairCost = ParseFloatAfterTag(text, RepairCostTag, 0.08415f);
                    // [quick repair cost rate] 是百分数(如150), 除100转倍率(1.5)
                    _quickRepairRate = ParseFloatAfterTag(text, QuickRepairCostRateTag, 150f) / 100f;
                    FileLogger.Log($"[EquipmentRepairPriceProvider] Loaded itemRepairCost={_itemRepairCost:F5} quickRepairRate={_quickRepairRate:F3}");
                }
                catch (Exception ex)
                {
                    _itemRepairCost = 0.08415f;
                    _quickRepairRate = 1.5f;
                    FileLogger.Log($"[EquipmentRepairPriceProvider] Failed to load pricetable.tbl: {ex.Message}");
                }
            }
        }

        // 读取 tag 后到下一个 '[' 之间的第一个浮点数; 找不到返回 fallback。
        private static float ParseFloatAfterTag(string text, string tag, float fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            var idx = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return fallback;

            var after = idx + tag.Length;
            var nextBracket = text.IndexOf('[', after);
            var slice = nextBracket > after ? text.Substring(after, nextBracket - after) : text.Substring(after);

            foreach (var token in slice.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (float.TryParse(token, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v))
                    return v;
            }

            return fallback;
        }
    }
}
