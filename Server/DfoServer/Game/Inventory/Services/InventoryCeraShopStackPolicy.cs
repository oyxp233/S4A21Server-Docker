using System;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryCeraShopStackPolicy
    {
        internal static int NormalizeEffectiveStackCount(
            int buyCount,
            int productCount,
            int metadataStackLimit)
        {
            if (buyCount <= 0)
                buyCount = 1;

            var unitCount = Math.Max(1, productCount);
            var requestedCount = (long)buyCount * unitCount;
            var stackLimit = ResolveStackLimit(unitCount, metadataStackLimit);
            if (stackLimit > 0 && requestedCount > stackLimit)
                return stackLimit;

            return requestedCount > int.MaxValue ? int.MaxValue : (int)requestedCount;
        }

        internal static int ResolveStackLimit(int productCount, int metadataStackLimit)
        {
            var unitCount = Math.Max(1, productCount);
            if (metadataStackLimit <= 0)
                return 0;

            return Math.Max(metadataStackLimit, unitCount);
        }
    }
}
