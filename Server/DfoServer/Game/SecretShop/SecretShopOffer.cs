using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.SecretShop
{
    internal sealed class SecretShopOfferItem
    {
        internal int ItemId { get; init; }
        internal int RawFlag { get; init; }
        internal int Price { get; init; }
        internal int RequiredItemId { get; init; }
        internal int Count { get; init; }
        internal int Weight { get; init; }
        internal int RemainingCount { get; set; }
        internal bool Sold => RemainingCount <= 0;
    }

    internal sealed class SecretShopOffer
    {
        private readonly object _sync = new();
        private readonly IReadOnlyList<SecretShopOfferItem> _items;

        internal SecretShopOffer(int npcId, IEnumerable<SecretShopItemCandidate> candidates)
        {
            NpcId = npcId;
            _items = (candidates ?? Array.Empty<SecretShopItemCandidate>())
                .Select(x => new SecretShopOfferItem
                {
                    ItemId = x.ItemId,
                    RawFlag = x.RawFlag,
                    Price = x.Price,
                    RequiredItemId = x.RequiredItemId,
                    Count = x.Count,
                    Weight = x.Weight,
                    RemainingCount = x.Count,
                })
                .ToArray();
        }

        internal int NpcId { get; }
        internal IReadOnlyList<SecretShopOfferItem> Items => _items;
        internal bool IsSecretShop => NpcId is 1002 or 1003 or 1004;

        internal IReadOnlyList<SecretShopOfferItem> GetAvailableItems()
        {
            lock (_sync)
                return _items.Where(x => x.RemainingCount > 0).ToArray();
        }

        internal bool TryGetAvailableItem(int itemId, out SecretShopOfferItem item)
        {
            lock (_sync)
            {
                item = _items.FirstOrDefault(x => x.ItemId == itemId && x.RemainingCount > 0);
                return item != null;
            }
        }

        internal bool MarkSold(int itemId)
        {
            lock (_sync)
            {
                var item = _items.FirstOrDefault(x => x.ItemId == itemId && x.RemainingCount > 0);
                if (item == null)
                    return false;
                item.RemainingCount = 0;
                return true;
            }
        }

        internal bool TryCompletePurchase(
            int itemId,
            int requestedCount,
            Func<SecretShopOfferItem, int, bool> commitPurchase,
            out SecretShopOfferItem purchasedItem,
            out int purchasedCount,
            out int remainingCount)
        {
            purchasedItem = null;
            purchasedCount = 0;
            remainingCount = 0;
            if (commitPurchase == null)
                return false;

            lock (_sync)
            {
                var item = _items.FirstOrDefault(x => x.ItemId == itemId && x.RemainingCount > 0);
                var effectiveCount = requestedCount == 0 ? 1 : requestedCount;
                if (item == null || effectiveCount <= 0 || effectiveCount > item.RemainingCount
                    || !commitPurchase(item, effectiveCount))
                    return false;

                item.RemainingCount -= effectiveCount;
                purchasedItem = item;
                purchasedCount = effectiveCount;
                remainingCount = item.RemainingCount;
                return true;
            }
        }
    }

    internal static class SecretShopOfferFactory
    {
        internal static SecretShopOffer Create(
            SecretShopCatalog catalog,
            int dungeonId,
            int dungeonBasisLevel,
            int partySize,
            Func<int, int> next)
        {
            if (catalog == null)
                throw new ArgumentNullException(nameof(catalog));

            var npcId = SecretShopSelector.SelectNpc(
                catalog.ResolveNpcWeights(dungeonId, dungeonBasisLevel, partySize), next);
            if (npcId == 1000)
                return new SecretShopOffer(1000, Array.Empty<SecretShopItemCandidate>());

            if (npcId is not (1002 or 1003 or 1004))
                return new SecretShopOffer(1000, Array.Empty<SecretShopItemCandidate>());

            var pool = catalog.ResolvePool(npcId, dungeonId, dungeonBasisLevel, useCashItems: false);
            var selected = SecretShopSelector.SelectItems(pool, next);
            if (selected.Count == 0)
                return new SecretShopOffer(1000, Array.Empty<SecretShopItemCandidate>());

            return new SecretShopOffer(npcId, selected);
        }
    }

    internal static class SecretShopCatalogProvider
    {
        private static readonly Lazy<SecretShopCatalog> Catalog = new(
            () => SecretShopCatalog.Parse(PvfArchiveAccessor.ReadText("etc/secretshop.etc")));

        internal static SecretShopCatalog Current => Catalog.Value;
    }

}
