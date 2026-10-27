using System;
using System.Collections.Generic;
using DfoServer.Game.Auction.Queries;

namespace DfoServer.Game.Auction.Storage;

internal sealed partial class AuctionOrderStore
{
    internal IReadOnlyList<AuctionJournalEntry> ReadHistorySource(SqliteAuctionTradeHistory.Checkpoint checkpoint, int limit = 256)
    {
        if (checkpoint == null || checkpoint.Sequence < 0 || limit < 1 || limit > 1000)
            throw new ArgumentException("Bounded history checkpoint required.");
        return database.Read(c => {
            using var t = c.BeginTransaction(deferred: true);
            if (checkpoint.Sequence > 0)
            {
                using var anchor = Command(c, t, "SELECT operation_id,intent_hash FROM auction_operation WHERE sequence_id=@seq;");
                Add(anchor, "@seq", checkpoint.Sequence);
                using var r = anchor.ExecuteReader();
                if (!r.Read() || r.GetString(0) != checkpoint.OperationId || r.GetString(1) != checkpoint.IntentHash)
                    throw new InvalidOperationException("History source was replaced or rewound.");
            }
            using var cmd = Command(c, t, "SELECT operation_id FROM auction_operation WHERE sequence_id>@after ORDER BY sequence_id LIMIT @limit;");
            Add(cmd, "@after", checkpoint.Sequence); Add(cmd, "@limit", limit);
            var ids = new List<Guid>();
            using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(Guid.ParseExact(r.GetString(0), "N"));
            var rows = new List<AuctionJournalEntry>();
            foreach (var id in ids) rows.Add(AuctionOperationJournal.FindInTransaction(c, t, id)
                ?? throw new InvalidOperationException("History operation disappeared within snapshot."));
            return (IReadOnlyList<AuctionJournalEntry>)rows;
        });
    }
}
