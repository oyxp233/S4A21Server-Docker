using System;
using System.IO;
using System.Text.Json;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction.Storage
{
    internal sealed record AuctionOrderRecoveryResult(int Completed, int Aborted, int Quarantined, int Pending);

    // Startup/quiescent recovery composes order escrow and journal resolution.
    // Never register the earlier journal-only primitive as order recovery.
    internal sealed class AuctionOrderRecovery
    {
        private readonly AuctionOrderStore store;
        private readonly IGameDatabase publicDatabase;
        private readonly AuctionPublicAssetCommit rail;
        private readonly object marketGate;
        internal AuctionOrderRecovery(AuctionOrderStore store, IGameDatabase publicDatabase,
            AuctionPublicAssetCommit rail, object sharedMarketGate)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.publicDatabase = publicDatabase ?? throw new ArgumentNullException(nameof(publicDatabase));
            this.rail = rail ?? throw new ArgumentNullException(nameof(rail));
            marketGate = sharedMarketGate ?? throw new ArgumentNullException(nameof(sharedMarketGate));
        }

        // A timeout is not quiescence. Caller must have stopped all executors
        // (e.g. before opening the runtime's packet intake after a restart).
        internal AuctionOrderRecoveryResult RecoverPendingAfterExecutorsQuiesced(int limit)
        {
            lock (marketGate)
            {
                int completed = 0, aborted = 0, quarantined = 0, pending = 0;
                foreach (var entry in store.Pending(limit))
                {
                    try
                    {
                        var receipt = publicDatabase.Read(c => AuctionPublicReceiptRepository.Find(c, null, entry.Intent));
                        if (receipt == null)
                        {
                            store.AbortVerifiedUncommitted(entry.Intent, "ExecutorsQuiescedAndReceiptVerifiedAbsent");
                            aborted++;
                        }
                        else { store.Complete(entry.Intent, receipt); completed++; }
                        rail.ReleaseTerminalAttempt(store.FindOperation(entry.Intent.OperationId));
                    }
                    catch (Exception ex) when (ex is AuctionReceiptConflictException || ex is JsonException || ex is ArgumentException)
                    {
                        store.Quarantine(entry.Intent, "RecoveryConflict:" + ex.GetType().Name);
                        quarantined++;
                    }
                    catch (Exception ex) when (ex is SqliteException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                    {
                        // Unknown/unavailable evidence preserves the reservation.
                        pending++;
                    }
                }
                return new(completed, aborted, quarantined, pending);
            }
        }
    }
}
