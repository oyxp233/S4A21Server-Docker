using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld.Auction;
using DfoServer.Network.Parsers.Auction;

namespace DfoServer.SelfTests
{
    public static class AuctionQuerySelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] auction query: {name}"); if (ok) passed++; else failed++; }
            const long now = 1792700000;
            const string hash = "963cd331342a0066c01cb189ec2cd822b8b8206065b109ec63ca664adb3da8d8";
            var principal = new AuctionQueryPrincipal(Guid.Parse("8bd67102-1010-4000-8000-000000000001"), 1, 80101, 80101);
            var seller = new AuctionParty(principal.CharacterId, principal.AccountId, "fixture seller");
            var other = new AuctionParty(80102, 80102, "fixture other");
            try
            {
                var resources = new Dictionary<string, string> {
                    ["equipment/equipment.lst"] = "101 `a.equ` 102 `b.equ`",
                    ["stackable/stackable.lst"] = "201 `a.stk`",
                    ["equipment/a.equ"] = "[name]\n`known weapon`\n[minimum level]\n50\n[rarity]\n3\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n",
                    ["equipment/b.equ"] = "[name]\n`other weapon`\n[minimum level]\n20\n[rarity]\n2\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n",
                    ["stackable/a.stk"] = "[name]\n`known material`\n[minimum level]\n1\n[rarity]\n2\n[stackable type]\n`[material]` 0\n[attach type]\n`[free]`\n"
                };
                var catalog = AuctionTemplateCatalog.Load(path => resources.TryGetValue(path, out var text) ? text : null, hash);
                var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
                var engine = new AuctionQueryEngine(catalog, codec);
                AuctionOrderSnapshot Order(long id, int template = 201, int quantity = 2,
                    long? buyout = 200, long? unit = 100, byte upgrade = 0, byte forge = 0)
                {
                    var core = new ItemCore { ItemId = template, ItemKind = template < 200 ? ItemCore.KindEquipment : ItemCore.KindMaterial,
                        Count = quantity, Upgrade = upgrade, GenuineUpgrade = forge };
                    var escrow = codec.Serialize(AuctionItemEnvelope.Create(core.ToBytes(), "", "{}"));
                    var original = core.Copy(); if (template >= 200) original.Count = checked(quantity + (quantity < int.MaxValue - 7 ? 7 : 0));
                    return new(id, 1, AuctionOrderState.Active, seller,
                        codec.Serialize(AuctionItemEnvelope.Create(original.ToBytes(), "", "{}")), escrow,
                        quantity, quantity, null, buyout, null, 0, 10000,
                        now - 100, now + 1000, null, "account:" + seller.AccountId,
                        AuctionEconomyPolicy.PolicyVersion, 5, 0, 0, unit);
                }
                AuctionSearchByItemKeyRequest BA(ImmutableArray<uint> keys, byte forgeMin = 0, byte forgeMax = 31,
                    ushort random = 0, ushort category = 40119, uint position = 0)
                    => new(position, 0, 31, category, keys, random, 0, 0, forgeMin, forgeMax);
                AuctionSearchWithoutItemKeyRequest BB(byte rarity = 7, byte reinforceMin = 0, byte reinforceMax = 31,
                    byte levelMin = 0, byte levelMax = 0, byte forgeMin = 0, byte forgeMax = 31,
                    ushort category = 40119) => new(0, category, rarity, reinforceMin, reinforceMax,
                        levelMin, levelMax, 0, 0, 0, forgeMin, forgeMax);
                var all = AuctionQueryEngine.Normalize(BB()).Value;
                var normalizedKeys = AuctionQueryEngine.Normalize(BA(ImmutableArray.Create<uint>(201, 101, 201)));
                Check("BA normalizes keys once without inventing a sort field", normalizedKeys.IsAvailable
                    && normalizedKeys.Value.ItemKeys.SequenceEqual(new[] { 101, 201 }) && normalizedKeys.Value.ForgeMax == 31);
                Check("nonblock offsets reject before query", AuctionQueryEngine.Normalize(BA(ImmutableArray.Create<uint>(101), position: 1)).Status == AuctionQueryStatus.InvalidRequest);
                Check("empty BA key list is invalid", AuctionQueryEngine.Normalize(BA(ImmutableArray<uint>.Empty)).Status == AuctionQueryStatus.InvalidRequest);
                Check("unsigned template overflow never becomes negative canonical key", AuctionQueryEngine.Normalize(BA(ImmutableArray.Create(uint.MaxValue))).Status == AuctionQueryStatus.InvalidRequest);
                Check("strength range is bounded by real low5 field", AuctionQueryEngine.Normalize(BB(reinforceMax: 32)).Status == AuctionQueryStatus.InvalidRequest);
                Check("reversed forge range is not silently swapped", AuctionQueryEngine.Normalize(BB(forgeMin: 9, forgeMax: 3)).Status == AuctionQueryStatus.InvalidRequest);
                Check("rarity not generated by current dropdown stays unavailable", AuctionQueryEngine.Normalize(BB(rarity: 5)).Status == AuctionQueryStatus.Unavailable);
                Check("level sender zero lower normalization is preserved", AuctionQueryEngine.Normalize(BB(levelMax: 60)).Value.LevelMin == 1);
                Check("UI default level range normalizes to no extra predicate", AuctionQueryEngine.Normalize(BB(levelMax: 86)).Value.LevelMax == 0);
                Check("random 30000 is not assumed AND or OR", AuctionQueryEngine.Normalize(BA(ImmutableArray.Create<uint>(101), random: 30000)).Status == AuctionQueryStatus.Unavailable);
                Check("random FFFF is not ignored", AuctionQueryEngine.Normalize(BA(ImmutableArray.Create<uint>(101), random: ushort.MaxValue)).Status == AuctionQueryStatus.Unavailable);
                var orders = new[] { Order(3), Order(1, 101, 1, 500, 500, 10, 3), Order(2, 102, 1, 1000, 1000, 1, 0) };
                var frozen = engine.CreateSnapshot(orders, principal, all, now);
                Check("public search includes own listings in stable order", frozen.IsAvailable
                    && frozen.Value.Rows.Select(x => x.OrderId).SequenceEqual(new long[] { 1, 2, 3 }));
                Check("escrow quantity is not source stack quantity", frozen.Value.Rows.Single(x => x.OrderId == 3).RemainingQuantity == 2);
                var exactRange = AuctionQueryEngine.Normalize(BB(rarity: 3, reinforceMin: 10, reinforceMax: 10,
                    levelMin: 50, levelMax: 50, forgeMin: 3, forgeMax: 3)).Value;
                Check("range fields filter actual instance plus exact template", engine.SelectCandidates(orders, principal, exactRange, now).Value.Select(x => x.OrderId).SequenceEqual(new long[] { 1 }));
                var keyCriteria = AuctionQueryEngine.Normalize(BA(ImmutableArray.Create<uint>(201))).Value;
                Check("BA excludes other keys", engine.SelectCandidates(orders, principal, keyCriteria, now).Value.Select(x => x.OrderId).SequenceEqual(new long[] { 3 }));
                var titleCriteria = AuctionQueryEngine.Normalize(BB(category: 12005)).Value;
                Check("title job sender remap does not invent membership", titleCriteria.NativeCategory == 12004
                    && engine.SelectCandidates(Array.Empty<AuctionOrderSnapshot>(), principal, titleCriteria, now).Status == AuctionQueryStatus.Unavailable);
                var unknownCategory = AuctionQueryEngine.Normalize(BB(category: 10000)).Value;
                Check("unknown category is unavailable even with no market rows", engine.SelectCandidates(Array.Empty<AuctionOrderSnapshot>(), principal, unknownCategory, now).Status == AuctionQueryStatus.Unavailable);
                var withRule = new AuctionQueryEngine(catalog, codec, code => code == 10000
                    ? new(code, hash, "fixture/explicit-test-only-membership", t => t.Kind == "equipment" ? AuctionQueryMatch.Match : AuctionQueryMatch.NoMatch) : null);
                Check("injected catalog-bound category rule selects only its predicate", withRule.SelectCandidates(orders, principal, unknownCategory, now).Value.Length == 2);
                var wrongHashRule = new AuctionQueryEngine(catalog, codec, code => new(code, new string('0', 64), "fixture", _ => AuctionQueryMatch.Match));
                Check("category predicate cannot cross PVF batches", wrongHashRule.SelectCandidates(orders, principal, unknownCategory, now).Status == AuctionQueryStatus.Unavailable);
                var pending = Order(4) with { PendingOperationId = Guid.NewGuid() };
                var expired = Order(5) with { ExpiresAtUnix = now };
                var funding = Order(6) with { State = AuctionOrderState.Funding };
                Check("funding pending and exact-expiry orders do not advertise purchasability", engine.SelectCandidates(new[] { pending, expired, funding }, principal, all, now).Value.IsEmpty);
                Check("unfunded active model is corrupt rather than successful empty", engine.SelectCandidates(new[] { Order(7) with { FundedDepositGold = 0 } }, principal, all, now).Status == AuctionQueryStatus.Unavailable);
                Check("duplicate candidate identities are rejected", engine.SelectCandidates(new[] { orders[0], orders[0] }, principal, all, now).Status == AuctionQueryStatus.Unavailable);
                Check("bad escrow is unavailable rather than hidden", engine.SelectCandidates(new[] { Order(7) with { EscrowEnvelopeJson = "{}" } }, principal, all, now).Status == AuctionQueryStatus.Unavailable);
                var unknownTemplateCore = new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 999, Count = 2 };
                var unknownTemplate = Order(7) with { EscrowEnvelopeJson = codec.Serialize(AuctionItemEnvelope.Create(unknownTemplateCore.ToBytes(), "", "{}")) };
                Check("exact template is mandatory", engine.SelectCandidates(new[] { unknownTemplate }, principal, all, now).Status == AuctionQueryStatus.Unavailable);
                var badQuantity = Order(7) with { RemainingQuantity = 1 };
                Check("quantity mismatch cannot escape into a snapshot", engine.SelectCandidates(new[] { badQuantity }, principal, all, now).Status == AuctionQueryStatus.Unavailable);
                var page0 = engine.Page(frozen.Value, principal, all, 0, now);
                orders[0] = orders[0] with { Version = 2, State = AuctionOrderState.Sold, RemainingQuantity = 0 };
                Check("source list changes cannot mutate existing snapshot", engine.Page(frozen.Value, principal, all, 0, now + 1).Value.Rows.Single(x => x.OrderId == 3).OrderVersion == 1);
                Check("position zero caller can explicitly refresh snapshot", engine.CreateSnapshot(orders, principal, all, now + 1).Value.Rows.Length == 2);
                Check("snapshot carries total not remaining row quantity", page0.Value.ReportedTotalCount == 3 && page0.Value.Rows.Length == 3);
                Check("TTL is inactive at exact 120-second boundary", engine.Page(frozen.Value, principal, all, 0, now + 120).Status == AuctionQueryStatus.SnapshotExpired);
                Check("snapshot cannot cross session generation", engine.Page(frozen.Value, principal with { Generation = 2 }, all, 0, now).Status == AuctionQueryStatus.InvalidRequest);
                Check("snapshot cannot cross current character", engine.Page(frozen.Value, principal with { CharacterId = 80103 }, all, 0, now).Status == AuctionQueryStatus.InvalidRequest);
                Check("snapshot cannot cross criteria", engine.Page(frozen.Value, principal, keyCriteria, 0, now).Status == AuctionQueryStatus.InvalidRequest);
                var otherPvfCatalog = AuctionTemplateCatalog.Load(path => resources.TryGetValue(path, out var text) ? text : null, new string('a', 64));
                Check("snapshot cannot cross exact PVF source", new AuctionQueryEngine(otherPvfCatalog, codec).Page(
                    frozen.Value, principal, all, 0, now).Status == AuctionQueryStatus.InvalidRequest);
                Check("later page without snapshot is explicit expiry", engine.Page(null, principal, all, 60, now).Status == AuctionQueryStatus.SnapshotExpired);
                Check("backdated reads are invalid", engine.Page(frozen.Value, principal, all, 0, now - 1).Status == AuctionQueryStatus.InvalidRequest);
                var basis = Order(1, quantity: 1, buyout: 100, unit: 100);
                var many = Enumerable.Range(1, 121).Select(id => basis with { OrderId = id }).Reverse().ToArray();
                var manySnapshot = engine.CreateSnapshot(many, principal, all, now).Value;
                var second = engine.Page(manySnapshot, principal, all, 60, now).Value;
                Check("sixty-row block retains immutable full total", second.ReportedTotalCount == 121 && second.Rows.Length == 60
                    && second.Rows[0].OrderId == 61 && second.Rows[^1].OrderId == 120);
                Check("last block has one exact record", engine.Page(manySnapshot, principal, all, 120, now).Value.Rows.Single().OrderId == 121);
                Check("snapshot row excess fails rather than returning capped success", engine.CreateSnapshot(
                    Enumerable.Range(1, 5001).Select(id => basis with { OrderId = id }).ToArray(), principal, all, now).Status == AuctionQueryStatus.Unavailable);
                Check("invalid principal is never an anonymous seller query", engine.CreateSnapshot(many, principal with { AccountId = 0 }, all, now).Status == AuctionQueryStatus.InvalidRequest);
                var mine = engine.SelectCandidates(new[] { Order(1), Order(2) with { Seller = other },
                    Order(3) with { Seller = new AuctionParty(80103, principal.AccountId, "another role") } }, principal,
                    AuctionQueryEngine.OwnedCriteria(AuctionQueryView.OwnActiveListings), now);
                Check("own listings means exact current character and account", mine.Value.Length == 1 && mine.Value[0].OrderId == 1);
                var bids = new[] { Order(1) with { Seller = other, OpeningBidGold = 100, HighestBidder = seller, FundedBidGold = 150 },
                    Order(2) with { Seller = other, OpeningBidGold = 100, HighestBidder = new AuctionParty(80103, principal.AccountId, "other role"), FundedBidGold = 150 } };
                var myBids = engine.SelectCandidates(bids, principal, AuctionQueryEngine.OwnedCriteria(AuctionQueryView.CurrentHighestBids), now);
                Check("own bids excludes other characters even in same account", myBids.Value.Length == 1 && myBids.Value[0].OrderId == 1);
                var referenceOrders = new List<AuctionOrderSnapshot> { Order(10, quantity: 2, buyout: 200, unit: 100),
                    Order(11, quantity: 3, buyout: 300, unit: 100) };
                for (int i = 1; i <= 7; i++) referenceOrders.Add(Order(11 + i, quantity: 1, buyout: 100 + i * 10, unit: 100 + i * 10));
                var request = new AuctionAveragePriceRequest(201, 0, 0, 0);
                var reference = engine.PriceReference(referenceOrders, principal, request, now);
                Check("reference aggregates same-price quantities and chooses six lowest levels", reference.IsAvailable
                    && reference.Value.Levels.Length == 6 && reference.Value.Levels[0] == new AuctionQueryPriceLevel(100, 5)
                    && reference.Value.Levels[^1].UnitPriceGold == 150 && reference.Value.SuggestedPriceScalar == 100);
                Check("unknown AVG scalar is not fabricated", reference.Value.UnknownScalar == null);
                Check("nondefault AVG attributes stay unavailable", engine.PriceReference(referenceOrders, principal, request with { AttributeA = 1 }, now).Status == AuctionQueryStatus.Unavailable);
                Check("nondivisible bundle cannot produce false complete minimum", engine.PriceReference(
                    new[] { Order(10, quantity: 3, buyout: 100, unit: null) }, principal, request, now).Status == AuctionQueryStatus.Unavailable);
                Check("nondivisible offer remains an exact total-price browse row", engine.SelectCandidates(
                    new[] { Order(10, quantity: 3, buyout: 100, unit: null) }, principal, all, now).Value.Single().BuyoutGold == 100);
                Check("exact integer bundle without frozen piece capability still has precise display unit", engine.PriceReference(
                    new[] { Order(10, quantity: 2, buyout: 200, unit: null) }, principal, request, now).Value.SuggestedPriceScalar == 100);
                Check("frozen unit inconsistency is not rounded", engine.PriceReference(new[] { Order(10, unit: 99) }, principal, request, now).Status == AuctionQueryStatus.Unavailable);
                var huge = Order(10, quantity: int.MaxValue, buyout: int.MaxValue, unit: 1);
                Check("reference quantity aggregation checks overflow", engine.PriceReference(new[] { huge, huge with { OrderId = 11 } }, principal, request, now).Status == AuctionQueryStatus.Unavailable);
                var emptyReference = engine.PriceReference(Array.Empty<AuctionOrderSnapshot>(), principal, request, now);
                Check("genuinely empty reference has no suggested scalar and no invented zero levels", emptyReference.IsAvailable
                    && emptyReference.Value.Levels.IsEmpty && emptyReference.Value.SuggestedPriceScalar == null);
                Check("pure snapshot repeat is deterministic", engine.CreateSnapshot(many, principal, all, now).Value.SnapshotId == manySnapshot.SnapshotId);
            }
            catch (Exception exception) { Console.WriteLine("[FAIL] unexpected auction query exception: " + exception); failed++; }
            Console.WriteLine($"AuctionQuerySelfTest: passed={passed} failed={failed}; pure fixture, no database/server/client.");
            return failed == 0 ? 0 : 1;
        }
    }
}
