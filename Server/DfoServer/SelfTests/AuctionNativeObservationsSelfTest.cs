using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Linq;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Handlers.Auction;
using DfoServer.Network.Parsers.Auction;

namespace DfoServer.SelfTests
{
    public static class AuctionNativeObservationsSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            { Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] auction observations: {name}"); if (condition) passed++; else failed++; }
            const long now = 1792700000;
            var principal = new AuctionQueryPrincipal(Guid.NewGuid(), 4, 80602, 80602);
            var actor = new AuctionParty(80602, 80602, "买家");
            var seller = new AuctionParty(80601, 80601, "卖家");
            var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
            AuctionItemEnvelope Envelope(int quantity, int template = 3001, uint tail = 0)
                => AuctionItemEnvelope.Create(new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = template, Count = quantity,
                    A21Tail_Unknown84 = tail }.ToBytes(), "", "{}");
            AuctionQueryRow Row(long id, long version = 1)
            {
                var item = Envelope(10);
                return new(id, version, 3001, seller.CharacterId, seller.AccountId, seller.FrozenName,
                    null, null, 10, null, 1000, 100, "fixture material", 1, 2, 0, 0,
                    now - 1000, now + 1000, codec.Serialize(item), item.ContentHashHex);
            }
            AuctionQuerySnapshot Snapshot(AuctionQueryRow[] rows, string query = "fixture-a", AuctionQueryPrincipal owner = null,
                long captured = now, long until = now + 120)
                => new(owner ?? principal, query, new string('a', 64), captured, until, rows.ToImmutableArray());
            AuctionOrderSnapshot After(AuctionQueryRow before, int remaining, AuctionOrderState state = AuctionOrderState.Active)
                => new(before.OrderId, before.OrderVersion + 1, state, seller, before.EscrowEnvelopeJson,
                    codec.Serialize(Envelope(remaining == 0 ? before.RemainingQuantity : remaining)), 10, remaining,
                    null, remaining == 0 ? before.BuyoutGold : 100L * remaining, null, 0, remaining == 0 ? 0 : 10000,
                    before.CreatedAtUnix, before.ExpiresAtUnix, null, "account:" + seller.AccountId,
                    "user-approved-20261023-v1", 5, (10L - remaining) * 100, (10L - remaining) * 5, 100);
            AuctionMarketRequest Request(AuctionQueryRow before, AuctionOperationAction action = AuctionOperationAction.BuyPiece,
                int quantity = 3, long amount = 300, AuctionParty acting = null)
                => new(Guid.NewGuid(), action, before.OrderId, before.OrderVersion, acting ?? actor, quantity, amount, 0);
            bool Has(AuctionNativeObservations book, long id, out AuctionQuerySnapshot value, long at = now)
                => book.TryGetObservedOrderSnapshot(id, at, out value, out _);
            try
            {
                var all = Enumerable.Range(1, 61).Select(i => Row(i)).ToArray();
                var full = Snapshot(all);
                var pages = new AuctionNativeObservations(principal, codec);
                Check("full query creation alone grants no observations", !Has(pages, 1, out _));
                Check("record only the first successfully sent60 rows", pages.RecordSentRows(AuctionQueryView.Search, full, all.Take(60).ToArray(), now, out _)
                    && Has(pages, 1, out var first) && first.Rows.Length == 1 && !Has(pages, 61, out _));
                Check("same snapshot accumulates second sent page without authorizing unseen rows", pages.RecordSentRows(
                    AuctionQueryView.Search, full, all.Skip(60).ToArray(), now + 1, out _) && Has(pages, 61, out _) && Has(pages, 1, out _));
                Check("search cannot mark a whole5000-row result as one sent page", !pages.RecordSentRows(AuctionQueryView.Search, full, all, now, out _));
                var changed = all[0] with { RemainingQuantity = 9 };
                Check("row membership requires exact frozen facts rather than just order ID", !pages.RecordSentRows(AuctionQueryView.Search, full,
                    new[] { changed }, now, out _) && Has(pages, 1, out var unchanged) && unchanged.Rows[0] == all[0]);
                Check("duplicate actually sent row is rejected atomically", !pages.RecordSentRows(AuctionQueryView.Search, full,
                    new[] { all[0], all[0] }, now, out _) && Has(pages, 61, out _));
                var replacementRow = Row(100);
                var replacement = Snapshot(new[] { replacementRow }, "fixture-b");
                Check("a new query replaces only that view's old observations", pages.RecordSentRows(AuctionQueryView.Search, replacement,
                    new[] { replacementRow }, now, out _) && !Has(pages, 1, out _) && Has(pages, 100, out _));
                var empty = Snapshot(Array.Empty<AuctionQueryRow>(), "empty query");
                Check("successful empty new query clears observations without creating rows", pages.RecordSentRows(AuctionQueryView.Search,
                    empty, Array.Empty<AuctionQueryRow>(), now, out _) && !Has(pages, 100, out _));
                Check("row outside full snapshot cannot create observations", !pages.RecordSentRows(AuctionQueryView.Search, empty,
                    new[] { replacementRow }, now, out _) && !Has(pages, 100, out _));
                Check("different principal session cannot record a sent page", !pages.RecordSentRows(AuctionQueryView.Search,
                    Snapshot(new[] { all[0] }, owner: principal with { SessionId = Guid.NewGuid() }), new[] { all[0] }, now, out _));
                Check("different principal generation cannot record a sent page", !pages.RecordSentRows(AuctionQueryView.Search,
                    Snapshot(new[] { all[0] }, owner: principal with { Generation = 5 }), new[] { all[0] }, now, out _));
                var laterGeneration = new AuctionNativeObservations(principal with { Generation = 5 }, codec);
                Check("a new generation inherits no old observations", !Has(laterGeneration, 1, out _)
                    && !laterGeneration.RecordSentRows(AuctionQueryView.Search, full, new[] { all[0] }, now, out _));
                var ttl = new AuctionNativeObservations(principal, codec);
                ttl.RecordSentRows(AuctionQueryView.Search, full, new[] { all[0] }, now, out _);
                Check("TTL expiry equality removes authorization", !Has(ttl, 1, out _, now + 120));
                Check("expired sent row cannot be refreshed by retrying old page", !ttl.RecordSentRows(AuctionQueryView.Search,
                    full, new[] { all[0] }, now + 120, out _));
                var due = all[0] with { ExpiresAtUnix = now + 1 };
                var dueBook = new AuctionNativeObservations(principal, codec);
                dueBook.RecordSentRows(AuctionQueryView.Search, Snapshot(new[] { due }), new[] { due }, now, out _);
                Check("order expiry also removes an otherwise live snapshot row", !Has(dueBook, 1, out _, now + 1));

                var cross = new AuctionNativeObservations(principal, codec);
                cross.RecordSentRows(AuctionQueryView.Search, Snapshot(new[] { all[0] }), new[] { all[0] }, now, out _);
                var newer = Row(1, 3);
                cross.RecordSentRows(AuctionQueryView.CurrentHighestBids, Snapshot(new[] { newer }, "own bids"), new[] { newer }, now, out _);
                Check("across views select highest version actually sent", Has(cross, 1, out var high) && high.Rows[0].OrderVersion == 3);
                cross.RecordSentRows(AuctionQueryView.Search, replacement, new[] { replacementRow }, now, out _);
                Check("search replacement does not clear a different view's observation", Has(cross, 1, out high)
                    && high.Rows[0].OrderVersion == 3 && Has(cross, 100, out _));

                var book = new AuctionNativeObservations(principal, codec);
                var row = all[0]; var frozen = Snapshot(new[] { row }); var frozenId = frozen.SnapshotId;
                book.RecordSentRows(AuctionQueryView.Search, frozen, new[] { row }, now, out _);
                var request = Request(row); var after = After(row, 7);
                Check("own successfully sent partial ACK advances only a matching observed version", book.AcknowledgeCommitted(request, after, now + 1, out _)
                    && Has(book, 1, out var advanced, now + 1) && advanced.Rows[0].OrderVersion == 2
                    && advanced.Rows[0].RemainingQuantity == 7 && advanced.Rows[0].BuyoutGold == 700
                    && advanced.Rows[0].EscrowContentHash == codec.DeserializeStrict(after.EscrowEnvelopeJson).ContentHashHex);
                Check("partial ACK leaves original query and paging facts frozen", frozen.SnapshotId == frozenId
                    && frozen.Rows[0].OrderVersion == 1 && frozen.Rows[0].RemainingQuantity == 10 && frozen.Rows[0].BuyoutGold == 1000);
                Check("re-sending an old frozen page cannot downgrade acknowledged observations", book.RecordSentRows(AuctionQueryView.Search,
                    frozen, new[] { row }, now + 2, out _) && Has(book, 1, out advanced, now + 2) && advanced.Rows[0].OrderVersion == 2);
                AuctionSellerNameTag13.TryEncodeFrozenRoleName(seller.FrozenName, out var tag);
                var bits = BinaryPrimitives.ReadUInt64LittleEndian(AuctionOrderWireIdentity.Encode(1));
                Has(book, 1, out advanced, now + 2);
                var next = AuctionNativeMutationAdapter.NormalizeBuyPiece(principal, actor, advanced, Guid.NewGuid(),
                    new AuctionBuyApieceRequest(200, 2, bits, ImmutableArray.CreateRange(tag), 3001), now + 2);
                Check("client can buy remaining pieces using own ACK-derived observed version", next.IsReady
                    && next.Request.ExpectedVersion == 2 && next.Request.Quantity == 2 && next.Request.OfferedGold == 200);
                Check("second own partial ACK advances another single version without fresh search", book.AcknowledgeCommitted(
                    next.Request, After(advanced.Rows[0], 5), now + 3, out _) && Has(book, 1, out advanced, now + 3)
                    && advanced.Rows[0].OrderVersion == 3 && advanced.Rows[0].RemainingQuantity == 5);
                Check("own ACK never extends original snapshot TTL", !Has(book, 1, out _, now + 120));

                var invalid = new AuctionNativeObservations(principal, codec);
                invalid.RecordSentRows(AuctionQueryView.Search, frozen, new[] { row }, now, out _);
                Check("foreign actor ACK cannot promote an observation", !invalid.AcknowledgeCommitted(request with { Actor = seller }, after, now, out _)
                    && Has(invalid, 1, out var untouched) && untouched.Rows[0].OrderVersion == 1);
                Check("version jumps cannot promote an observation", !invalid.AcknowledgeCommitted(request, after with { Version = 4 }, now, out _)
                    && Has(invalid, 1, out untouched) && untouched.Rows[0].OrderVersion == 1);
                Check("mismatched order identity cannot promote an observation", !invalid.AcknowledgeCommitted(request, after with { OrderId = 2 }, now, out _));
                Check("mismatched seller identity cannot promote an observation", !invalid.AcknowledgeCommitted(request,
                    after with { Seller = seller with { CharacterId = 99999 } }, now, out _));
                Check("same ID/version wrong escrow template cannot promote an observation", !invalid.AcknowledgeCommitted(request,
                    after with { EscrowEnvelopeJson = codec.Serialize(Envelope(7, 3002)) }, now, out _));
                Check("partial ACK cannot change hidden native instance state", !invalid.AcknowledgeCommitted(request,
                    after with { EscrowEnvelopeJson = codec.Serialize(Envelope(7, tail: 123)) }, now, out _));
                Check("ACK for an unsent order cannot synthesize authorization", !invalid.AcknowledgeCommitted(Request(Row(2)), After(Row(2), 7), now, out _)
                    && !Has(invalid, 2, out _));
                var bidRow = row with { CurrentOrOpeningBidGold = 100, ExactUnitPriceGold = null };
                var terminal = new AuctionNativeObservations(principal, codec);
                var bidSnapshot = Snapshot(new[] { bidRow });
                terminal.RecordSentRows(AuctionQueryView.Search, bidSnapshot, new[] { bidRow }, now, out _);
                var bidAfter = After(bidRow, 10) with { OpeningBidGold = 100, UnitPriceGold = null, BuyoutGold = 1000,
                    HighestBidder = actor, FundedBidGold = 110 };
                Check("successful native Bid ACK clears client-deleted observation", terminal.AcknowledgeCommitted(
                    Request(bidRow, AuctionOperationAction.Bid, 10, 110), bidAfter, now, out _) && !Has(terminal, 1, out _));
                Check("old frozen query cannot resurrect a cleared Bid row", terminal.RecordSentRows(AuctionQueryView.Search,
                    bidSnapshot, new[] { bidRow }, now + 1, out _) && !Has(terminal, 1, out _, now + 1));
                Check("another pre-ACK snapshot cannot resurrect cleared older order version", terminal.RecordSentRows(AuctionQueryView.CurrentHighestBids,
                    Snapshot(new[] { bidRow }, "another frozen query"), new[] { bidRow }, now + 1, out _) && !Has(terminal, 1, out _, now + 1));
                var freshBidRow = bidRow with { OrderVersion = 2, CurrentOrOpeningBidGold = 110,
                    HighestBidderCharacterId = actor.CharacterId, HighestBidderAccountId = actor.AccountId };
                Check("fresh actually sent post-bid query may observe its current version again", terminal.RecordSentRows(AuctionQueryView.CurrentHighestBids,
                    Snapshot(new[] { freshBidRow }, "fresh post bid", captured: now + 1, until: now + 121), new[] { freshBidRow }, now + 1, out _)
                    && Has(terminal, 1, out var freshBidObservation, now + 1) && freshBidObservation.Rows[0].OrderVersion == 2);
                foreach (var action in new[] { AuctionOperationAction.Buyout, AuctionOperationAction.BuyPiece, AuctionOperationAction.Cancel })
                {
                    bool cancel = action == AuctionOperationAction.Cancel;
                    var owner = cancel ? principal with { AccountId = seller.AccountId, CharacterId = seller.CharacterId } : principal;
                    var last = new AuctionNativeObservations(owner, codec);
                    var lastSnapshot = Snapshot(new[] { row }, owner: owner);
                    last.RecordSentRows(AuctionQueryView.Search, lastSnapshot, new[] { row }, now, out _);
                    var closed = After(row, 0, cancel ? AuctionOrderState.Cancelled : AuctionOrderState.Sold);
                    var closeRequest = Request(row, action, 10, cancel ? 0 : 1000, cancel ? seller : actor);
                    Check(action + " terminal ACK clears observation across views", last.AcknowledgeCommitted(closeRequest, closed, now, out _)
                        && !Has(last, 1, out _));
                }
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] unexpected auction observations exception: " + ex); failed++; }
            Console.WriteLine($"AuctionNativeObservationsSelfTest: passed={passed} failed={failed}; sent-row/ACK accounting only, no network/DB.");
            return failed == 0 ? 0 : 1;
        }
    }
}
