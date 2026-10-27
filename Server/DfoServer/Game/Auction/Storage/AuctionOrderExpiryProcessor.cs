using System;
using System.Collections.Generic;
using DfoServer.Game.Auction.Market;

namespace DfoServer.Game.Auction.Storage
{
    // Register with the existing ClockService minute tick at the composition
    // root, and also invoke at startup. Absolute deadlines remain in the DB.
    // This class creates neither a timer nor a separate background loop.
    internal sealed class AuctionOrderExpiryProcessor
    {
        private readonly AuctionOrderStore store;
        private readonly AuctionMarketApplication application;
        private readonly object marketGate;
        private long cursor;
        internal AuctionOrderExpiryProcessor(AuctionOrderStore store, AuctionMarketApplication application, object sharedMarketGate)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.application = application ?? throw new ArgumentNullException(nameof(application));
            marketGate = sharedMarketGate ?? throw new ArgumentNullException(nameof(sharedMarketGate));
        }

        internal IReadOnlyList<AuctionMarketResult> RunDue(long nowUnix, int limit)
        {
            lock (marketGate)
            {
                var due = store.FindDue(nowUnix, cursor, limit);
                if (due.Count == 0 && cursor != 0) { cursor = 0; due = store.FindDue(nowUnix, cursor, limit); }
                var results = new List<AuctionMarketResult>();
                foreach (var order in due)
                {
                    // Advancing even past unavailable records prevents a held
                    // unsupported order from starving all subsequent expiry.
                    cursor = order.OrderId;
                    var request = new AuctionMarketRequest(Guid.NewGuid(), AuctionOperationAction.Expire,
                        order.OrderId, order.Version, order.Seller, order.RemainingQuantity, 0, order.ExpiresAtUnix);
                    results.Add(application.ExecuteOfflineExpiry(request));
                }
                return results.AsReadOnly();
            }
        }
    }
}
