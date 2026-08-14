using System;

namespace DfoServer.Network.Parsers
{
    internal readonly struct SecretShopBuyRequest
    {
        internal SecretShopBuyRequest(int itemId, int requestedCount)
        {
            ItemId = itemId;
            RequestedCount = requestedCount;
        }

        internal int ItemId { get; }
        internal int RequestedCount { get; }

        internal static bool TryParse(byte[] body, out SecretShopBuyRequest request)
        {
            request = default;
            if (body == null || body.Length != 8)
                return false;

            var itemId = BitConverter.ToInt32(body, 0);
            var requestedCount = BitConverter.ToInt32(body, 4);
            if (itemId <= 0 || requestedCount < 0)
                return false;

            request = new SecretShopBuyRequest(itemId, requestedCount);
            return true;
        }
    }
}
