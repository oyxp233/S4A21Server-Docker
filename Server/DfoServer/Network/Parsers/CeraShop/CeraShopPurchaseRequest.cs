using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Parsers.CeraShop
{
    public sealed class CeraShopPurchaseRequest
    {
        public List<int> CommodityNos { get; } = new List<int>();

        public List<byte> AttributeValues { get; } = new List<byte>();

        internal List<CeraShopPurchaseOptions> ItemOptions { get; } = new List<CeraShopPurchaseOptions>();

        public byte PaymentMode { get; private set; }

        public bool CouponSelected { get; private set; }

        public int CouponItemId { get; private set; }

        public short CouponSlot { get; private set; } = -1;

        public int ProductId => CommodityNos.Count > 0 ? CommodityNos[0] : 0;

        public int ItemTemplateId => ProductId;

        public int Count => 1;

        private const int HeaderSize = 4;
        private const int ItemStride = 15;
        private const int CommodityOffsetInItem = 3;

        public static bool TryParse(byte[] body, out CeraShopPurchaseRequest request)
        {
            request = null;
            if (body == null || body.Length < HeaderSize + ItemStride)
                return false;

            var totalCount = body[2];
            if (totalCount <= 0)
                totalCount = 1;

            var parsed = new CeraShopPurchaseRequest
            {
                CouponSelected = body[3] != 0,
                PaymentMode = body[4],
            };
            for (var i = 0; i < totalCount; i++)
            {
                var itemBase = HeaderSize + i * ItemStride;
                var commodityOffset = itemBase + CommodityOffsetInItem;
                if (commodityOffset + 4 > body.Length)
                    break;

                var commodityNo = BitConverter.ToInt32(body, commodityOffset);
                if (commodityNo <= 0)
                    continue;

                parsed.CommodityNos.Add(commodityNo);
                parsed.AttributeValues.Add(body[itemBase + 1]);
                parsed.ItemOptions.Add(totalCount == 1
                    ? ParseItemOptions(body, commodityOffset + 4)
                    : new CeraShopPurchaseOptions());
            }

            if (parsed.CommodityNos.Count == 0)
                return false;

            if (parsed.CouponSelected)
            {
                // Package purchases may append a variable-length component list.
                // The selected coupon tuple is always the final itemId(U32)+slot(U16).
                var couponOffset = body.Length - 6;
                if (couponOffset < 0 || couponOffset + 6 > body.Length)
                    return false;

                parsed.CouponItemId = BitConverter.ToInt32(body, couponOffset);
                parsed.CouponSlot = BitConverter.ToInt16(body, couponOffset + 4);
                if (parsed.CouponItemId <= 0 || parsed.CouponSlot < 0)
                    return false;
            }

            request = parsed;
            return true;
        }

        private static CeraShopPurchaseOptions ParseItemOptions(byte[] body, int offset)
        {
            var options = new CeraShopPurchaseOptions();
            if (body == null || offset < 0 || offset >= body.Length)
                return options;

            var cursor = offset;
            var avatarCount = body[cursor];
            var avatarEnd = cursor + 1 + avatarCount * 5;
            if (avatarCount > 0 && avatarCount <= 32 && avatarEnd <= body.Length)
            {
                cursor++;
                for (var index = 0; index < avatarCount; index++, cursor += 5)
                {
                    var itemTemplateId = BitConverter.ToInt32(body, cursor);
                    if (itemTemplateId <= 0)
                        continue;

                    options.AvatarChoices.Add(new AvatarPackageChoice
                    {
                        ItemTemplateId = itemTemplateId,
                        OptionValue = body[cursor + 4],
                    });
                }
            }

            if (cursor >= body.Length)
                return options;

            var selectionCount = body[cursor];
            var selectionEnd = cursor + 1 + selectionCount * 8;
            if (selectionCount == 0 || selectionCount > 16 || selectionEnd > body.Length)
                return options;

            cursor++;
            for (var index = 0; index < selectionCount; index++, cursor += 8)
            {
                var packageItemTemplateId = BitConverter.ToInt32(body, cursor);
                if (packageItemTemplateId <= 0)
                    continue;

                options.SelectionChoices.Add(new CeraShopSelectionChoice
                {
                    PackageItemTemplateId = packageItemTemplateId,
                    GroupIndex = BitConverter.ToUInt16(body, cursor + 4),
                    SelectionIndex = BitConverter.ToUInt16(body, cursor + 6),
                });
            }

            return options;
        }
    }
}
