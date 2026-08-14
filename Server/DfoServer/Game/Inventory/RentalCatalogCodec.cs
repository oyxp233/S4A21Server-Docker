using DfoServer.Game.Settings;
using System;

namespace DfoServer.Game.Inventory
{
    /// 租赁商店目录包字段。当前只维护幸运星数量和购买回包所需字段。
    public static class RentalCatalogCodec
    {
        public const int BlobSize = 134;
        public const int LuckyStarOffset = 10;
        public const int PurchaseMarkerOffset = 36;
        public const int IdleMarkerValue = 3;
        public const int TailQtyOffset = 116;
        public const int ShopPacketQtyOffset = 17;
        public const int MaxLuckyStar = 999;
        public const int GoldCostPerStar = 100_000;

        private static void WriteLuckyStar(byte[] catalog, ushort luckyStar)
        {
            if (catalog == null || catalog.Length < LuckyStarOffset + 2)
                return;

            Buffer.BlockCopy(BitConverter.GetBytes(luckyStar), 0, catalog, LuckyStarOffset, 2);
        }

        private static void WriteIdleMarker(byte[] catalog)
        {
            if (catalog == null || catalog.Length < PurchaseMarkerOffset + 2)
                return;

            Buffer.BlockCopy(BitConverter.GetBytes((ushort)IdleMarkerValue), 0, catalog, PurchaseMarkerOffset, 2);
        }

        public static bool TryParseShopPacketBuyCount(byte[] body, out int buyCount)
        {
            buyCount = 0;
            if (body == null || body.Length < ShopPacketQtyOffset + 2)
                return false;

            buyCount = BitConverter.ToUInt16(body, ShopPacketQtyOffset);
            return IsValidBuyCount(buyCount);
        }

        private static bool IsValidBuyCount(int count) => count > 0 && count <= MaxLuckyStar;

        public static byte[] BuildPurchaseAck(byte[] accountCatalog, ushort buyCount, ushort totalLuckyStar)
        {
            // 购买幸运星后回发账号目录副本，保留客户端已有布局，只覆盖显示字段。
            var catalog = NormalizeCatalog(accountCatalog);
            ApplyDisplayFields(catalog, totalLuckyStar);
            if (catalog.Length >= TailQtyOffset + 2)
                Buffer.BlockCopy(BitConverter.GetBytes(buyCount), 0, catalog, TailQtyOffset, 2);
            return WrapCatalogAck(catalog);
        }

        private static void ApplyDisplayFields(byte[] catalog, ushort totalLuckyStar)
        {
            WriteLuckyStar(catalog, totalLuckyStar);
            WriteIdleMarker(catalog);
        }

        private static byte[] WrapCatalogAck(byte[] catalog)
        {
            var ack = new byte[4 + BlobSize];
            Buffer.BlockCopy(BitConverter.GetBytes(BlobSize), 0, ack, 0, 4);
            Buffer.BlockCopy(catalog, 0, ack, 4, BlobSize);
            return ack;
        }

        private static byte[] NormalizeCatalog(byte[] accountCatalog)
        {
            var source = accountCatalog;
            if (source == null || source.Length < BlobSize)
                source = AccountSettings.DefaultMainGameOption;

            var copy = new byte[BlobSize];
            Buffer.BlockCopy(source, 0, copy, 0, Math.Min(source.Length, BlobSize));
            return copy;
        }
    }
}
