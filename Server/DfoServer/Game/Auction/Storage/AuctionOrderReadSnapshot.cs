using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Auction.Storage
{
    internal enum AuctionOrderReadScope { PublicActive, SellerActive, CurrentHighestBidderActive }

    internal sealed class AuctionOrderReadSnapshot
    {
        internal AuctionOrderReadSnapshot(bool available, string reason, long capturedAtUnix,
            IEnumerable<AuctionOrderSnapshot> orders)
        {
            Available = available;
            Reason = reason;
            CapturedAtUnix = capturedAtUnix;
            Orders = Array.AsReadOnly((orders ?? throw new ArgumentNullException(nameof(orders))).ToArray());
        }
        internal bool Available { get; }
        internal string Reason { get; }
        internal long CapturedAtUnix { get; }
        internal IReadOnlyList<AuctionOrderSnapshot> Orders { get; }
    }
}
