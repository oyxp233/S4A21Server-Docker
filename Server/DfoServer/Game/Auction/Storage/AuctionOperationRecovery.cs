using System;
using System.Text.Json;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Auction.Storage
{
    internal sealed record AuctionRecoveryResult(int Committed, int Aborted, int Quarantined);

    // All auction executors, expiry work and recovery must share executionGate.
    // No running executor may outlive that gate. A missing receipt is otherwise
    // not proof that a worker cannot commit after the lookup.
    internal sealed class AuctionOperationRecovery
    {
        private readonly AuctionOperationJournal journal;
        private readonly IGameDatabase publicDatabase;
        private readonly object executionGate;

        internal AuctionOperationRecovery(AuctionOperationJournal journal,
            IGameDatabase publicDatabase, object executionGate)
        {
            this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
            this.publicDatabase = publicDatabase ?? throw new ArgumentNullException(nameof(publicDatabase));
            this.executionGate = executionGate ?? throw new ArgumentNullException(nameof(executionGate));
        }

        // This journal-only primitive must be composed with order finalization
        // in the same private transaction before enabling any market commands.
        // It currently has no runtime registration and cannot move game assets.
        internal AuctionRecoveryResult RecoverPendingAfterExecutorsQuiesced(int limit)
        {
            lock (executionGate)
            {
                int committed = 0, aborted = 0, quarantined = 0;
                foreach (var entry in journal.Pending(0, limit))
                {
                    AuctionPublicReceipt receipt;
                    try
                    {
                        receipt = publicDatabase.Read(c =>
                            AuctionPublicReceiptRepository.Find(c, null, entry.Intent));
                    }
                    catch (Exception ex) when (ex is AuctionReceiptConflictException || ex is JsonException)
                    {
                        // An identity/duplicate/payload conflict preserves the
                        // durable reservation. SQL/I/O access errors propagate
                        // and leave Prepared; they are never treated as absent.
                        journal.Quarantine(entry.Intent, "PublicReceiptConflict");
                        quarantined++; continue;
                    }
                    if (receipt == null)
                    {
                        journal.AbortVerifiedUncommitted(entry.Intent, "NoCommittedPublicReceipt");
                        aborted++;
                    }
                    else
                    {
                        journal.Complete(entry.Intent, receipt.ResultJson);
                        committed++;
                    }
                }
                return new AuctionRecoveryResult(committed, aborted, quarantined);
            }
        }
    }
}
