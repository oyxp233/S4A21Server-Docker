using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Network;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.Game.Premium
{
    internal sealed class DevilContractPurchase
    {
        internal DevilContractPurchase(
            int itemTemplateId,
            int durationDays,
            int ceraPrice,
            bool isPackage)
        {
            ItemTemplateId = itemTemplateId;
            DurationDays = durationDays;
            CeraPrice = ceraPrice;
            IsPackage = isPackage;
        }

        public int ItemTemplateId { get; }

        public int DurationDays { get; }

        public int CeraPrice { get; }

        public bool IsPackage { get; }
    }

    public sealed class DevilContractCatalog
    {
        private static DevilContractCatalog _cached;
        private readonly Dictionary<int, DevilContractPurchase> _byCommodityNo;
        private readonly Dictionary<int, int> _slotByItemTemplateId;
        private readonly Dictionary<int, int> _durationDaysByItemTemplateId;

        private DevilContractCatalog(
            Dictionary<int, DevilContractPurchase> byCommodityNo,
            Dictionary<int, int> slotByItemTemplateId,
            Dictionary<int, int> durationDaysByItemTemplateId)
        {
            _byCommodityNo = byCommodityNo;
            _slotByItemTemplateId = slotByItemTemplateId;
            _durationDaysByItemTemplateId = durationDaysByItemTemplateId;
        }

        public static DevilContractCatalog Load()
        {
            return _cached ?? (_cached = Parse(PvfArchiveAccessor.ReadText("etc/cerashop.etc")));
        }

        internal bool TryGetPurchase(int commodityNo, out DevilContractPurchase purchase)
        {
            return _byCommodityNo.TryGetValue(commodityNo, out purchase);
        }

        internal bool TryFindAllServicePackagePurchase(
            out int commodityNo,
            out DevilContractPurchase purchase)
        {
            commodityNo = 0;
            purchase = null;

            foreach (var pair in _byCommodityNo)
            {
                var candidate = pair.Value;
                if (candidate == null || !candidate.IsPackage)
                    continue;
                if (!TryResolveServiceGrants(candidate, out var grants)
                    || grants.Count != SlotCount)
                    continue;
                if (purchase != null && pair.Key >= commodityNo)
                    continue;

                commodityNo = pair.Key;
                purchase = candidate;
            }

            return purchase != null;
        }

        internal List<int> GetServiceItemTemplateIdsBySlot()
        {
            var result = new List<int>();
            for (var slotIndex = 0; slotIndex < SlotCount; slotIndex++)
            {
                var itemTemplateId = 0;
                foreach (var pair in _slotByItemTemplateId)
                {
                    if (pair.Value != slotIndex)
                        continue;

                    itemTemplateId = pair.Key;
                    break;
                }

                if (itemTemplateId > 0)
                    result.Add(itemTemplateId);
            }

            return result;
        }

        internal bool TryResolveServiceItem(
            int itemTemplateId,
            out int slotIndex,
            out int durationDays)
        {
            slotIndex = -1;
            durationDays = 0;
            return _slotByItemTemplateId.TryGetValue(itemTemplateId, out slotIndex)
                && _durationDaysByItemTemplateId.TryGetValue(itemTemplateId, out durationDays)
                && slotIndex >= 0
                && slotIndex < SlotCount
                && durationDays > 0;
        }

        internal bool TryResolveServiceGrants(
            DevilContractPurchase purchase,
            out List<DevilContractServiceGrant> grants)
        {
            grants = new List<DevilContractServiceGrant>();
            if (purchase == null || purchase.DurationDays <= 0)
                return false;

            var durationDaysBySlot = new int[SlotCount];
            if (!purchase.IsPackage)
            {
                if (!TryAddServiceGrant(
                        durationDaysBySlot,
                        purchase.ItemTemplateId,
                        1,
                        purchase.DurationDays))
                    return false;
            }
            else
            {
                var package = StackableItemProvider.Load(purchase.ItemTemplateId);
                if (package == null)
                    return false;

                var packageType = StackableItemProvider.NormalizeType(package.StackableType);
                var rewards = new List<PvfLib.BoosterRewardEntry>();
                if (string.Equals(packageType, "[cera package]", StringComparison.OrdinalIgnoreCase))
                {
                    rewards.AddRange(package.PackageRewards);
                }
                else if (string.Equals(packageType, "[cera booster]", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var reward in package.BoosterRewards)
                    {
                        if (string.Equals(reward?.RewardKind, "cera", StringComparison.OrdinalIgnoreCase))
                            rewards.Add(reward);
                    }
                }
                else
                {
                    return false;
                }

                if (rewards.Count == 0)
                    return false;

                foreach (var reward in rewards)
                {
                    // 混合礼包仍走通用奖励链，确保非服务奖励不会丢失。
                    if (reward == null
                        || !TryAddServiceGrant(
                            durationDaysBySlot,
                            reward.ItemId,
                            reward.Count,
                            purchase.DurationDays))
                        return false;
                }
            }

            for (var slotIndex = 0; slotIndex < SlotCount; slotIndex++)
            {
                if (durationDaysBySlot[slotIndex] <= 0)
                    continue;

                grants.Add(new DevilContractServiceGrant(
                    slotIndex,
                    durationDaysBySlot[slotIndex]));
            }

            return grants.Count > 0;
        }

        // slot 0-7 的 premium_type 存储偏移，避免与 premiumlist_new.etc 的 type 冲突
        public const int SlotPremiumTypeBase = 580;
        public const int SlotCount = 8;

        public static int SlotToPremiumType(int slotIndex) => SlotPremiumTypeBase + slotIndex;
        public static bool IsDevilContractSlotType(int premiumType) => premiumType >= SlotPremiumTypeBase && premiumType < SlotPremiumTypeBase + SlotCount;
        public static int PremiumTypeToSlot(int premiumType) => premiumType - SlotPremiumTypeBase;

        // "自动修理"服务 = 魔王契约 slot 6 (实测确认), premium_type=586。
        // 激活且未过期时装备修理免费。
        public const int AutoRepairSlotIndex = 6;
        public const int AutoRepairPremiumType = SlotPremiumTypeBase + AutoRepairSlotIndex;   // 586

        // ack_premium_blob 中魔王契約整体激活标记
        public const int ActivationPremiumType = 58;

        internal static DevilContractCatalog Parse(string text)
        {
            var content = text ?? string.Empty;
            var map = new Dictionary<int, DevilContractPurchase>();
            var slotByItemTemplateId = new Dictionary<int, int>();
            var durationDaysByItemTemplateId = new Dictionary<int, int>();

            ParseSelectableSlots(
                ExtractSection(content, "selectable character premium"),
                map,
                slotByItemTemplateId,
                durationDaysByItemTemplateId);
            ParseAllServicePackages(ExtractSection(content, "charac premium package"), map);

            FileLogger.Log($"[DevilContractCatalog] Loaded {map.Count} entries from Devil Contract cera-shop sections");
            return new DevilContractCatalog(
                map,
                slotByItemTemplateId,
                durationDaysByItemTemplateId);
        }

        private static void ParseSelectableSlots(
            string section,
            Dictionary<int, DevilContractPurchase> map,
            Dictionary<int, int> slotByItemTemplateId,
            Dictionary<int, int> durationDaysByItemTemplateId)
        {
            var tokens = Tokenize(section);

            for (var i = 0; i + 8 < tokens.Count; i += 9)
            {
                if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var commodityNo))
                    continue;
                if (commodityNo <= 0) continue;

                int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId);
                int.TryParse(tokens[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slotIndex);
                int.TryParse(tokens[i + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days);
                int.TryParse(tokens[i + 5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ceraPrice);

                if (slotIndex < 0 || slotIndex >= SlotCount || days <= 0 || ceraPrice < 0) continue;

                map[commodityNo] = new DevilContractPurchase(itemId, days, ceraPrice, false);
                slotByItemTemplateId[itemId] = slotIndex;
                durationDaysByItemTemplateId[itemId] = days;
            }
        }

        private static void ParseAllServicePackages(
            string section,
            Dictionary<int, DevilContractPurchase> map)
        {
            var tokens = Tokenize(section);
            for (var i = 0; i + 8 < tokens.Count; i += 9)
            {
                if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var commodityNo)
                    || commodityNo <= 0)
                    continue;
                if (!int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
                    || itemId <= 0)
                    continue;
                if (!int.TryParse(tokens[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ceraPrice)
                    || ceraPrice < 0)
                    continue;
                if (!int.TryParse(tokens[i + 7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
                    || days <= 0)
                    continue;

                map[commodityNo] = new DevilContractPurchase(itemId, days, ceraPrice, true);
            }
        }

        private bool TryAddServiceGrant(
            int[] durationDaysBySlot,
            int itemTemplateId,
            int count,
            int durationDays)
        {
            if (durationDaysBySlot == null
                || !_slotByItemTemplateId.TryGetValue(itemTemplateId, out var slotIndex)
                || slotIndex < 0
                || slotIndex >= durationDaysBySlot.Length
                || count <= 0
                || durationDays <= 0)
                return false;

            var totalDuration = (long)count * durationDays;
            if (totalDuration > int.MaxValue
                || durationDaysBySlot[slotIndex] > int.MaxValue - totalDuration)
                return false;

            durationDaysBySlot[slotIndex] += (int)totalDuration;
            return true;
        }

        private static string ExtractSection(string content, string section)
        {
            var startTag = "[" + section + "]";
            var endTag = "[/" + section + "]";
            var start = content.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += startTag.Length;
            var end = content.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
            return end > start ? content.Substring(start, end - start) : content.Substring(start);
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            for (var i = 0; i < text.Length;)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                if (text[i] == '`')
                {
                    var end = text.IndexOf('`', i + 1);
                    if (end < 0) end = text.Length - 1;
                    tokens.Add(text.Substring(i + 1, end - i - 1));
                    i = end + 1;
                    continue;
                }
                var s = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '`')
                    i++;
                tokens.Add(text.Substring(s, i - s));
            }
            return tokens;
        }
    }

    internal sealed class DevilContractServiceGrant
    {
        public DevilContractServiceGrant(int slotIndex, int durationDays)
        {
            SlotIndex = slotIndex;
            DurationDays = durationDays;
        }

        public int SlotIndex { get; }

        public int DurationDays { get; }
    }
}
