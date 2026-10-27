using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests;

public static class AuctionTradeHistorySelfTest
{
    public static int Run()
    {
        int passed = 0, failed = 0;
        void Check(string name, bool ok)
        { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] auction history: {name}"); if (ok) passed++; else failed++; }
        bool Refuses(Action action)
        { try { action(); return false; } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or SqliteException) { return true; } }
        string root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var codec = new AuctionItemCodec(new EmptyExtension());
            string path = Path.Combine(root, "history.db"), source = Path.Combine(root, "source.db");
            var history = new SqliteAuctionTradeHistory(path, codec);
            var journal = new AuctionOperationJournal(source);
            var store = new AuctionOrderStore(source, codec);
            AuctionTradeHistoryQuery Buyer(string cursor = null, int limit = 100) => new(2, 22, cursor, limit);
            var seller = new AuctionParty(11, 1, "seller"); var buyer = new AuctionParty(22, 2, "buyer");
            AuctionOperationIntent Sale(long order, AuctionOperationAction action = AuctionOperationAction.BuyPiece,
                long gross = 101, long fee = 5)
            {
                var core = new ItemCore { ItemId = 3156, ItemKind = ItemCore.KindMaterial, Count = 1 };
                string item = codec.Serialize(AuctionItemEnvelope.Create(core.ToBytes(), "", "{}"));
                var before = new AuctionOrderSnapshot(order, 1, AuctionOrderState.Active, seller,
                    item, item, 1, 1, null, gross, null, 0, 10000, 1792700000, 1792786400, null,
                    "account:1", "history-fixture", 5, 0, 0, gross);
                if (action == AuctionOperationAction.Expire) before = before with { HighestBidder = buyer, FundedBidGold = gross, OpeningBidGold = 100 };
                var after = before with { Version = 2, State = AuctionOrderState.Sold, RemainingQuantity = 0,
                    HighestBidder = null, FundedBidGold = 0, FundedDepositGold = 0, CumulativeGrossGold = gross, CumulativeFeeGold = fee };
                var actor = action == AuctionOperationAction.Expire ? seller : buyer;
                var binding = new AuctionQuoteBinding(action, order, 1, actor.CharacterId, actor.AccountId, 1, gross, before.ExpiresAtUnix, "fixture");
                var quote = new AuctionVerifiedQuote(AuctionQuoteAvailability.ApprovedProductPolicy, "fixture", 1792786400,
                    gross, 0, 0, fee, 10000, 0, "account:1", 5, binding, "history-fixture", 5, gross);
                var transition = new AuctionOrderTransition(before, after, quote,
                    new[] { new AuctionDeliveryLeg("item-delivery", AuctionDeliveryKind.ItemDelivery, buyer, 0, item, 1) });
                return new(Guid.NewGuid(), "order:" + order, action, actor.CharacterId, actor.AccountId, 1, 1792700100 + order,
                    JsonSerializer.Serialize(new { orderTransition = transition.Serialize() }));
            }
            Check("independent fresh database has no seeded history", File.Exists(path) && history.Read(Buyer()).Entries.Count == 0);
            var first = Sale(1); var second = Sale(2, AuctionOperationAction.Buyout);
            journal.Prepare(first); journal.Prepare(second); journal.Complete(second, "{}");
            history.Observe(store.ReadHistorySource(history.ReadCheckpoint()), advanceSource: true);
            Check("forward scan includes later committed sale beyond an old pending operation", history.ReadCheckpoint().Sequence == 2
                && history.Read(Buyer()).Entries.Single().OrderId == 2);
            Check("pending hole survives forward scan", history.ReadPending().SequenceEqual(new[] { first.OperationId }));
            journal.Complete(first, "{}");
            foreach (var id in history.ReadPending()) history.Observe(new[] { store.FindOperation(id) });
            Check("late commit below cursor is not lost", history.Read(Buyer()).Entries.Select(x => x.OrderId).SequenceEqual(new long[] { 2, 1 }));
            history.Observe(new[] { store.FindOperation(first.OperationId), store.FindOperation(second.OperationId) });
            Check("same operation replay never duplicates a sale", history.Read(Buyer()).Entries.Count == 2 && history.ReadPending().Count == 0);
            var sold = history.Read(new(1, 11, null, 100)).Entries;
            Check("seller and buyer perspectives retain exact item totals and frozen event time", sold.Count == 2
                && sold.All(x => x.IsSeller && x.ItemId == 3156 && x.Quantity == 1 && x.GrossGold == 101 && x.FeeGold == 5)
                && history.Read(Buyer()).Entries.All(x => !x.IsSeller && x.CompletedAtUnix == 1792700100 + x.OrderId));
            Check("account and character jointly scope reads", history.Read(new(1, 22, null, 100)).Entries.Count == 0
                && history.Read(new(2, 11, null, 100)).Entries.Count == 0);
            var page = history.Read(Buyer(limit: 1)); var next = history.Read(Buyer(page.NextCursor, 1));
            Check("keyset cursor has no duplicate or omitted sale", page.Entries.Single().OrderId == 2
                && next.Entries.Single().OrderId == 1 && next.NextCursor == null);
            Check("foreign scope or malformed cursor rejected", Refuses(() => history.Read(new(1, 11, page.NextCursor, 1)))
                && Refuses(() => history.Read(Buyer("v1:2:22:-1"))) && Refuses(() => history.Read(Buyer("junk"))));
            Check("invalid scopes and unbounded pages rejected", Refuses(() => history.Read(new(0, 22, null, 1)))
                && Refuses(() => history.Read(Buyer(limit: 0))) && Refuses(() => history.Read(Buyer(limit: 101))));
            Check("source replacement is detected before advancing", Refuses(() => store.ReadHistorySource(new(2, Guid.NewGuid().ToString("N"), "bad"))));
            var conflict = new AuctionJournalEntry(2, Sale(99), AuctionOperationState.Committed, "{}", "");
            Check("sequence identity conflict rejected without overwriting", Refuses(() => history.Observe(new[] { conflict }))
                && history.Read(Buyer()).Entries.Select(x => x.OrderId).SequenceEqual(new long[] { 2, 1 }));
            var invalid = new AuctionJournalEntry(4, Sale(4, gross: 101, fee: 102), AuctionOperationState.Committed, "{}", "");
            var valid = new AuctionJournalEntry(3, Sale(3), AuctionOperationState.Committed, "{}", "");
            Check("a malformed later row rolls back all earlier rows and cursor in its batch", Refuses(() => history.Observe(new[] { valid, invalid }, true))
                && history.Read(Buyer()).Entries.Count == 2 && history.ReadCheckpoint().Sequence == 2);
            var bid = new AuctionOperationIntent(Guid.NewGuid(), "order:5", AuctionOperationAction.Bid, 22, 2, 1, 1792700200, "{}");
            history.Observe(new[] { new AuctionJournalEntry(5, bid, AuctionOperationState.Committed, "{}", "") });
            Check("committed bids without a sale are not trade history", history.Read(Buyer()).Entries.Count == 2);
            var held = new AuctionJournalEntry(6, Sale(6, AuctionOperationAction.Expire), AuctionOperationState.Quarantined, "{}", "held");
            history.Observe(new[] { held });
            history.Observe(new[] { held with { State = AuctionOperationState.Committed } });
            Check("resolved quarantine and funded expiry can append exactly once", history.Read(Buyer()).Entries.First().OrderId == 6);
            Check("terminal results cannot revert to pending", Refuses(() => history.Observe(new[] { held with { State = AuctionOperationState.Prepared } })));
            var reopened = new SqliteAuctionTradeHistory(path, codec);
            Check("new instance restores sales, source checkpoint and pending state", reopened.Read(Buyer()).Entries.Count == 3
                && reopened.ReadCheckpoint() == history.ReadCheckpoint() && reopened.ReadPending().Count == 0);
            using (var c = new SqliteConnection("Data Source=" + path))
            { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE unexpected(value);"; cmd.ExecuteNonQuery(); }
            Check("matching version with unexpected schema is rejected", Refuses(() => new SqliteAuctionTradeHistory(path, codec)));
            string unknown = Path.Combine(root, "unknown.db"); File.WriteAllBytes(unknown, Array.Empty<byte>());
            Check("existing unmarked database is not implicitly adopted", Refuses(() => new SqliteAuctionTradeHistory(unknown, codec)));
        }
        catch (Exception ex) { Console.WriteLine(ex); failed++; }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"AuctionTradeHistorySelfTest: passed={passed} failed={failed}; DTO/journal projection fixtures, no player data or server.");
        return failed == 0 ? 0 : 1;
    }

    private sealed class EmptyExtension : IAuctionInstanceExtension
    {
        public string Capture(ItemCore source, ItemCore finalTransferCore) => "{}";
        public void Restore(ItemCore target, string json) { if (json != "{}") throw new ArgumentException("Unknown fixture extension."); }
    }
}
