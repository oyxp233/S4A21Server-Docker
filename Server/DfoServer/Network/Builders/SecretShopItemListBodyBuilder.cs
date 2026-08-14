using DfoServer.Game.SecretShop;

namespace DfoServer.Network.Builders
{
    internal static class SecretShopItemListBodyBuilder
    {
        internal static byte[] Build(SecretShopOffer offer)
        {
            var items = offer?.GetAvailableItems() ?? System.Array.Empty<SecretShopOfferItem>();
            var writer = new GamePacketWriter();
            writer.WriteInt32(items.Count);
            foreach (var item in items)
            {
                if (item.RawFlag is not (0 or 1))
                    throw new System.InvalidOperationException(
                        $"Unsupported secret-shop cost mode {item.RawFlag} for item {item.ItemId}.");

                var usesItemCurrency = item.RawFlag == 1;
                writer.WriteInt32(item.ItemId);
                writer.WriteByte((byte)item.RawFlag);
                writer.WriteInt32(usesItemCurrency ? 0 : item.Price);
                writer.WriteInt32(item.RemainingCount);
                writer.WriteInt32(usesItemCurrency ? item.RequiredItemId : 0);
                writer.WriteInt32(usesItemCurrency ? item.Price : 0);
                writer.WriteByte(0);
            }
            return writer.ToArray();
        }
    }
}
