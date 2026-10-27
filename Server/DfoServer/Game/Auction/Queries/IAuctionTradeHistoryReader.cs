using System.Collections.Generic;

namespace DfoServer.Game.Auction.Queries
{
    // Default implementation is the independent SQLite history projection.
    // This interface does not register a native packet or a frontend view.
    internal interface IAuctionTradeHistoryReader
    {
        AuctionTradeHistoryPage Read(AuctionTradeHistoryQuery query);
    }

    internal sealed record AuctionTradeHistoryQuery(int AccountId, int CharacterId, string Cursor, int Limit);
    internal sealed record AuctionTradeHistoryEntry(long OrderId, long CompletedAtUnix,
        int ItemId, int Quantity, long GrossGold, long FeeGold, bool IsSeller);
    internal sealed record AuctionTradeHistoryPage(IReadOnlyList<AuctionTradeHistoryEntry> Entries, string NextCursor);
}
