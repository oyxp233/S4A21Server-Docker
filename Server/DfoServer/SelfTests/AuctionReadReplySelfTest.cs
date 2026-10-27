using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld.Auction;
using DfoServer.Network.Builders.Auction;

namespace DfoServer.SelfTests
{
    public static class AuctionReadReplySelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] auction read reply: {name}"); if (ok) passed++; else failed++; }
            try
            {
                const long now = 1792700000;
                const string hash = "963cd331342a0066c01cb189ec2cd822b8b8206065b109ec63ca664adb3da8d8";
                var resources = new Dictionary<string, string> {
                    ["equipment/equipment.lst"] = "101 `a.equ`",
                    ["equipment/a.equ"] = "[name]\n`weapon`\n[equipment type]\n`[weapon]` 0\n",
                    ["stackable/stackable.lst"] = "201 `a.stk`",
                    ["stackable/a.stk"] = "[name]\n`material`\n[minimum level]\n1\n[rarity]\n2\n[stackable type]\n`[material]` 0\n[attach type]\n`[free]`\n"
                };
                var catalog = AuctionTemplateCatalog.Load(path => resources.TryGetValue(path, out var text) ? text : null, hash);
                var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
                var principal = new AuctionQueryPrincipal(Guid.Parse("8bd67102-1010-4000-8000-000000000002"), 1, 80101, 80101);
                var seller = new AuctionParty(principal.CharacterId, principal.AccountId, "小兔");
                var core = new ItemCore { ItemId = 201, ItemKind = ItemCore.KindMaterial, Count = 3,
                    A21Tail_Unknown97 = 0x10203040 };
                string Serialize(ItemCore item) => codec.Serialize(AuctionItemEnvelope.Create(item.ToBytes(), "", "{}"));
                var escrow = Serialize(core);
                var order = new AuctionOrderSnapshot(long.MaxValue, 1, AuctionOrderState.Active, seller,
                    escrow, escrow, 3, 3, null, 300, null, 0, 10000, now - 100, now + 3601,
                    null, "account:80101", AuctionEconomyPolicy.PolicyVersion, 5, 0, 0, 100);
                var engine = new AuctionQueryEngine(catalog, codec);
                var criteria = AuctionQueryEngine.OwnedCriteria(AuctionQueryView.OwnActiveListings);
                var snapshot = engine.CreateSnapshot(new[] { order }, principal, criteria, now);
                var page = engine.Page(snapshot.Value, principal, criteria, 0, now).Value;
                var row = page.Rows.Single();
                uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
                ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
                ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
                AuctionQueryPage With(AuctionQueryRow replacement) => page with { Rows = ImmutableArray.Create(replacement) };
                void Reject(string name, AuctionQueryPage candidate)
                {
                    var ok = AuctionReadReplyProjector.TrySearch(candidate, codec, now, out var rejectedBody, out var reason);
                    Check(name, !ok && rejectedBody == null && !string.IsNullOrEmpty(reason));
                }

                bool search = AuctionReadReplyProjector.TrySearch(page, codec, now, out var body, out var failure);
                Check("pure query through ordinary search is available", search && failure == "" && body.Length == 8 + 168);
                Check("search keeps frozen scalar total and count", body[0] == 1 && body[1] == 0 && U32(body, 2) == 1 && U16(body, 6) == 1);
                Check("positive long opaque identity preserves both dwords", U64(body, 8) == (ulong)long.MaxValue);
                Check("absent bid sentinel and exact buyout survive", U32(body, 8 + 9) == uint.MaxValue && U32(body, 8 + 13) == 300);
                Check("outer arg21 is the full core unknown97", U32(body, 8 + 17) == 0x10203040);
                Check("quantity uses native Value without source-stack rewrite", U32(body, 8 + 43 + 6) == 3 && row.RemainingQuantity == 3);
                Check("remaining hours round upward", body[8 + 42] == 2);
                Check("strict CP936 tag roundtrips with padding", AuctionSellerNameTag13.TryDecode(body.AsSpan(8 + 21, 13), out var name) && name == "小兔");
                Check("explicit reserved outer fields and warning are zero", body[8 + 8] == 0 && U64(body, 8 + 34) == 0
                    && U32(body, 8 + 160) == 0 && U32(body, 8 + 164) == 0);
                var totalPage = page with { ReportedTotalCount = 177 };
                Check("page total is not replaced by emitted row count", AuctionReadReplyProjector.TrySearch(totalPage, codec, now, out var totalBody, out _) && U32(totalBody, 2) == 177);
                Check("empty later block preserves frozen total", AuctionReadReplyProjector.TrySearch(totalPage with { Rows = ImmutableArray<AuctionQueryRow>.Empty }, codec, now, out var empty, out _) && empty.Length == 8 && U32(empty, 2) == 177 && U16(empty, 6) == 0);
                Check("listings use byte count and exact151-byte stride", AuctionReadReplyProjector.TryListings(page.Rows, codec, now, out var listings, out _)
                    && listings.Length == 154 && listings[0] == 1 && listings[1] == 0 && listings[2] == 1
                    && U64(listings, 3) == (ulong)long.MaxValue && U32(listings, 3 + 147) == 0x10203040 && listings[3 + 29] == 2);
                Check("bids use u16 count and exact159-byte stride", AuctionReadReplyProjector.TryBids(page.Rows, codec, now, out var bids, out _)
                    && bids.Length == 163 && bids[0] == 1 && U16(bids, 2) == 1 && U64(bids, 4 + 29) == 0
                    && U32(bids, 4 + 155) == 0x10203040 && bids[4 + 37] == 2);
                Check("empty owned views keep proven framing", AuctionReadReplyProjector.TryListings(Array.Empty<AuctionQueryRow>(), codec, now, out var el, out _)
                    && el.SequenceEqual(new byte[] { 1, 0, 0 }) && AuctionReadReplyProjector.TryBids(Array.Empty<AuctionQueryRow>(), codec, now, out var eb, out _)
                    && eb.SequenceEqual(new byte[] { 1, 0, 0, 0 }));

                Reject("missing page fails", null);
                Reject("negative total fails", page with { ReportedTotalCount = -1 });
                Reject("total cannot be smaller than body count", page with { ReportedTotalCount = 0 });
                Reject("unrepresentable search batch is not truncated", page with { ReportedTotalCount = 61,
                    Rows = Enumerable.Range(1, 61).Select(id => row with { OrderId = id }).ToImmutableArray() });
                Check("maximum60 search block is complete", AuctionReadReplyProjector.TrySearch(page with { ReportedTotalCount = 5000,
                    Rows = Enumerable.Range(1, 60).Select(id => row with { OrderId = id }).ToImmutableArray() }, codec, now,
                    out var fullBlock, out _) && fullBlock.Length == 8 + 168 * 60 && U32(fullBlock, 2) == 5000 && U16(fullBlock, 6) == 60);
                Reject("nonpositive identity is unavailable", With(row with { OrderId = 0 }));
                Reject("duplicate records cannot alias opaque identity", page with { ReportedTotalCount = 2, Rows = ImmutableArray.Create(row, row) });
                Reject("zero gold never becomes the absent sentinel", With(row with { BuyoutGold = 0 }));
                Reject("negative gold never wraps", With(row with { CurrentOrOpeningBidGold = -1 }));
                Reject("price above signed int cannot wrap", With(row with { BuyoutGold = (long)int.MaxValue + 1 }));
                Check("signed int maximum price is exact", AuctionReadReplyProjector.TrySearch(With(row with { BuyoutGold = int.MaxValue }), codec, now, out var maxPrice, out _)
                    && U32(maxPrice, 8 + 13) == int.MaxValue);
                Reject("both missing prices are corrupt", With(row with { BuyoutGold = null }));
                Reject("escrow hash binding is required", With(row with { EscrowContentHash = new string('0', 64) }));
                Reject("escrow template binding is required", With(row with { TemplateId = 202 }));
                Reject("escrow quantity binding is required", With(row with { RemainingQuantity = 2 }));
                Reject("bad descriptor cannot produce successful empty", With(row with { EscrowEnvelopeJson = "{}" }));
                Reject("empty seller name cannot satisfy strict tag", With(row with { SellerName = "" }));
                Reject("13-byte name leaves no NUL", With(row with { SellerName = new string('A', 13) }));
                Reject("unencodable seller name is unavailable", With(row with { SellerName = "兔😀" }));
                Reject("embedded NUL cannot alias another seller", With(row with { SellerName = "兔\0x" }));
                Check("12-byte seller tag terminates at byte12", AuctionReadReplyProjector.TrySearch(With(row with { SellerName = new string('A', 12) }), codec, now, out var fullTag, out _) && fullTag[8 + 21 + 12] == 0);
                Reject("expired at exact boundary", With(row with { ExpiresAtUnix = now }));
                Check("one second remaining displays one hour", AuctionReadReplyProjector.TrySearch(With(row with { ExpiresAtUnix = now + 1 }), codec, now, out var oneHour, out _) && oneHour[8 + 42] == 1);
                Check("exact3600 does not gain a second hour", AuctionReadReplyProjector.TrySearch(With(row with { ExpiresAtUnix = now + 3600 }), codec, now, out var exactHour, out _) && exactHour[8 + 42] == 1);
                Check("255-hour maximum is representable", AuctionReadReplyProjector.TrySearch(With(row with { ExpiresAtUnix = now + 255 * 3600 }), codec, now, out var maxHours, out _) && maxHours[8 + 42] == 255);
                Reject("255 hours plus one second never truncates", With(row with { ExpiresAtUnix = now + 255 * 3600 + 1 }));
                Reject("extreme expiry does not overflow ceiling arithmetic", With(row with { ExpiresAtUnix = long.MaxValue }));
                var timed = core.Copy(); timed.ExpireTime = 100;
                var timedEnvelope = codec.DeserializeStrict(Serialize(timed));
                var unsupported = row with { OrderId = 1, EscrowEnvelopeJson = codec.Serialize(timedEnvelope), EscrowContentHash = timedEnvelope.ContentHashHex };
                Reject("one unsupported native projection fails whole batch", page with { ReportedTotalCount = 2, Rows = ImmutableArray.Create(row, unsupported) });
                Check("owned unsupported row does not publish partial prefix", !AuctionReadReplyProjector.TryListings(new[] { row, unsupported }, codec, now, out var partial, out _) && partial == null);
                Check("bid unsupported row does not publish partial prefix", !AuctionReadReplyProjector.TryBids(new[] { row, unsupported }, codec, now, out var partialBid, out _) && partialBid == null);
                var tooMany = Enumerable.Range(1, 256).Select(id => row with { OrderId = id }).ToArray();
                Check("own listings count overflow never truncates", !AuctionReadReplyProjector.TryListings(tooMany, codec, now, out var tooManyListings, out _) && tooManyListings == null);
                Check("bids enforce UI byte limit despite u16 wire count", !AuctionReadReplyProjector.TryBids(tooMany, codec, now, out var tooManyBids, out _) && tooManyBids == null);
                var maxRows = tooMany.Take(255).ToArray();
                Check("maximum255 owned counts remain exact", AuctionReadReplyProjector.TryListings(maxRows, codec, now, out var maxListings, out _)
                    && maxListings.Length == 3 + 151 * 255 && maxListings[2] == 255
                    && AuctionReadReplyProjector.TryBids(maxRows, codec, now, out var maxBids, out _) && maxBids.Length == 4 + 159 * 255 && U16(maxBids, 2) == 255);

                var reference = engine.PriceReference(new[] { order }, principal,
                    new DfoServer.Network.Parsers.Auction.AuctionAveragePriceRequest(201, 0, 0, 0), now).Value;
                Check("pure reference emits paired price and remaining quantity", AuctionReadReplyProjector.TryPriceReference(reference, out var average, out _)
                    && average.Length == 58 && average[0] == 1 && U32(average, 2) == 100 && U32(average, 6) == 0
                    && U32(average, 10) == 100 && U32(average, 34) == 3);
                Check("unused reference pairs are explicit policy zeros", average.AsSpan(14, 20).IndexOfAnyExcept((byte)0) < 0 && average.AsSpan(38, 20).IndexOfAnyExcept((byte)0) < 0);
                var noReference = reference with { SuggestedPriceScalar = null, Levels = ImmutableArray<AuctionQueryPriceLevel>.Empty };
                Check("empty reference matches no-item display", AuctionReadReplyProjector.TryPriceReference(noReference, out var zeroAverage, out _)
                    && zeroAverage.Length == 58 && zeroAverage.AsSpan(1).IndexOfAnyExcept((byte)0) < 0);
                var sixLevels = reference with { Levels = Enumerable.Range(1, 6).Select(i => new AuctionQueryPriceLevel(99 + i, i)).ToImmutableArray() };
                Check("all six reference pairs retain index alignment", AuctionReadReplyProjector.TryPriceReference(sixLevels, out var sixAverage, out _)
                    && U32(sixAverage, 30) == 105 && U32(sixAverage, 54) == 6);
                void RejectReference(string name, AuctionQueryPriceReference candidate)
                    => Check(name, !AuctionReadReplyProjector.TryPriceReference(candidate, out var rejected, out _) && rejected == null);
                RejectReference("reference sorting must be strictly ascending", reference with { Levels = ImmutableArray.Create(new AuctionQueryPriceLevel(100, 1), new AuctionQueryPriceLevel(90, 1)) });
                RejectReference("duplicate levels must have been combined by query", reference with { Levels = ImmutableArray.Create(new AuctionQueryPriceLevel(100, 1), new AuctionQueryPriceLevel(100, 2)) });
                RejectReference("suggestion must match lowest level", reference with { SuggestedPriceScalar = 99 });
                RejectReference("zero-quantity level is corrupt", reference with { Levels = ImmutableArray.Create(new AuctionQueryPriceLevel(100, 0)) });
                RejectReference("seventh price level is not silently dropped", reference with { Levels = Enumerable.Range(1, 7).Select(i => new AuctionQueryPriceLevel(i, 1)).ToImmutableArray() });
                RejectReference("unknown scalar cannot be treated as a proved value", reference with { UnknownScalar = 7 });
                RejectReference("unknown query policy cannot emit complete ACK", reference with { PolicyVersion = "other" });
                Check("projection preserves full escrow and its original hash", row.EscrowEnvelopeJson == escrow
                    && row.EscrowContentHash == codec.DeserializeStrict(escrow).ContentHashHex && codec.DecodeStrict(codec.DeserializeStrict(escrow)).A21Tail_Unknown97 == 0x10203040);
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] unexpected auction read reply exception: " + ex); failed++; }
            Console.WriteLine($"AuctionReadReplySelfTest: passed={passed} failed={failed}; pure fixture, no database/server/client.");
            return failed == 0 ? 0 : 1;
        }
    }
}
