using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Storage;
using DfoServer.Network;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Handlers.Auction;
using DfoServer.Network.Parsers.Auction;

namespace DfoServer.SelfTests
{
    public static class AuctionNativeMutationAdapterSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            { Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] auction native mutation: {name}"); if (condition) passed++; else failed++; }
            const long now = 1792700000, orderId = 0x100000002L;
            var principal = new AuctionQueryPrincipal(Guid.NewGuid(), 7, 80502, 80502);
            var actor = new AuctionParty(principal.CharacterId, principal.AccountId, "买家新名");
            var seller = new AuctionParty(80501, 80501, "卖家");
            var row = new AuctionQueryRow(orderId, 23, 3001, seller.CharacterId, seller.AccountId, seller.FrozenName,
                null, null, 10, null, 1000, 100, "fixture material", 1, 2, 0, 0,
                now - 1000, now + 1000, "fixture opaque escrow", "fixture content hash");
            AuctionQuerySnapshot Snapshot(AuctionQueryRow value = null, AuctionQueryPrincipal owner = null)
                => new(owner ?? principal, "fixture criteria", new string('a', 64), now - 10, now + 110,
                    ImmutableArray.Create(value ?? row));
            ulong Bits(long id) => BinaryPrimitives.ReadUInt64LittleEndian(AuctionOrderWireIdentity.Encode(id));
            AuctionSellerNameTag13.TryEncodeFrozenRoleName(seller.FrozenName, out var sellerBytes);
            var tag = ImmutableArray.CreateRange(sellerBytes);
            var operation = Guid.NewGuid();
            AuctionNativeMutationResult Bid(AuctionBidRequest request = null, AuctionQuerySnapshot snapshot = null,
                AuctionQueryPrincipal owner = null, AuctionParty acting = null, long at = now)
                => AuctionNativeMutationAdapter.NormalizeBidOrBuyout(owner ?? principal, acting ?? actor, snapshot ?? Snapshot(),
                    operation, request ?? new AuctionBidRequest(1000, Bits(orderId), tag), at);
            AuctionNativeMutationResult Piece(AuctionBuyApieceRequest request = null, AuctionQueryRow value = null)
                => AuctionNativeMutationAdapter.NormalizeBuyPiece(principal, actor, Snapshot(value), operation,
                    request ?? new AuctionBuyApieceRequest(300, 3, Bits(orderId), tag, 3001), now);
            try
            {
                var buyout = Bid();
                Check("exact native bid total maps to Buyout with observed version and full8B identity", buyout.IsReady
                    && buyout.Request.Action == AuctionOperationAction.Buyout && buyout.Request.OrderId == orderId
                    && buyout.Request.ExpectedVersion == 23 && buyout.Request.OperationId == operation
                    && buyout.Request.Quantity == 10 && buyout.Request.OfferedGold == 1000);
                Check("actor name comes from authenticated actor rather than seller tag", buyout.Request.Actor == actor
                    && buyout.Request.Actor.FrozenName == "买家新名" && buyout.Request.Actor.CharacterId != row.SellerCharacterId);
                var bidRow = row with { CurrentOrOpeningBidGold = 100 };
                var bid = Bid(new AuctionBidRequest(110, Bits(orderId), tag), Snapshot(bidRow));
                Check("nonexact amount stays Bid for Market threshold and funded-delta validation", bid.IsReady
                    && bid.Request.Action == AuctionOperationAction.Bid && bid.Request.OfferedGold == 110);
                Check("over-buyout native amount also remains Bid rather than silently clamping", Bid(
                    new AuctionBidRequest(1001, Bits(orderId), tag), Snapshot(bidRow)).Request?.Action == AuctionOperationAction.Bid);
                var newer = row with { OrderVersion = 24 };
                var newerRequest = Bid(snapshot: Snapshot(newer));
                Check("another observation cannot rewrite the old request version", newerRequest.Request.ExpectedVersion == 24
                    && buyout.Request.ExpectedVersion == 23);
                Check("missing snapshot is unavailable without a current Store lookup", AuctionNativeMutationAdapter.NormalizeBidOrBuyout(
                    principal, actor, null, operation, new(1000, Bits(orderId), tag), now).Status == AuctionNativeMutationStatus.Unavailable);
                Check("snapshot expiry equality rejects stale observation", Bid(at: now + 110).Status == AuctionNativeMutationStatus.Unavailable);
                Check("time before snapshot is rejected", Bid(at: now - 11).Status == AuctionNativeMutationStatus.Rejected);
                Check("order expiry equality rejects even an unexpired query snapshot", Bid(snapshot: Snapshot(row with { ExpiresAtUnix = now })).Status == AuctionNativeMutationStatus.Unavailable);
                Check("different session cannot reuse a snapshot", Bid(owner: principal with { SessionId = Guid.NewGuid() }).Status == AuctionNativeMutationStatus.Rejected);
                Check("different generation cannot reuse a snapshot", Bid(owner: principal with { Generation = 8 }).Status == AuctionNativeMutationStatus.Rejected);
                var otherCharacter = principal with { CharacterId = 80503 };
                Check("different character cannot reuse same-account snapshot", Bid(owner: otherCharacter,
                    acting: new(80503, principal.AccountId, "同账号角色")).Status == AuctionNativeMutationStatus.Rejected);
                var otherAccount = principal with { AccountId = 80503 };
                Check("different account cannot reuse a snapshot", Bid(owner: otherAccount,
                    acting: new(principal.CharacterId, 80503, "其他账号")).Status == AuctionNativeMutationStatus.Rejected);
                Check("actor CID/AID must exactly match authenticated principal", Bid(acting: actor with { AccountId = 80503 }).Status == AuctionNativeMutationStatus.Rejected);
                Check("explicit stable operation UUID is required", AuctionNativeMutationAdapter.NormalizeBidOrBuyout(
                    principal, actor, Snapshot(), Guid.Empty, new(1000, Bits(orderId), tag), now).Status == AuctionNativeMutationStatus.Rejected);
                Check("zero identity cannot become an internal order", Bid(new(1000, 0, tag)).Status == AuctionNativeMutationStatus.Rejected);
                Check("unsigned high-bit identity cannot wrap to negative internal ID", Bid(new(1000, 0x8000000000000001UL, tag)).Status == AuctionNativeMutationStatus.Rejected);
                Check("unobserved positive order stays unavailable", Bid(new(1000, Bits(orderId + 1), tag)).Status == AuctionNativeMutationStatus.Unavailable);
                var duplicate = new AuctionQuerySnapshot(principal, "fixture", new string('a', 64), now - 10, now + 110, ImmutableArray.Create(row, newer));
                Check("ambiguous observed identity cannot pick an arbitrary version", Bid(snapshot: duplicate).Status == AuctionNativeMutationStatus.Unavailable);
                var wrongTag = sellerBytes.AsSpan().ToArray(); wrongTag[0] ^= 1;
                Check("seller tag must match exact encoded frozen seller name", Bid(new(1000, Bits(orderId), ImmutableArray.CreateRange(wrongTag))).Status == AuctionNativeMutationStatus.Rejected);
                var dirtyPadding = sellerBytes.AsSpan().ToArray(); dirtyPadding[12] = 1;
                Check("native seller name binding ignores bytes after its first NUL", Bid(new(1000, Bits(orderId), ImmutableArray.CreateRange(dirtyPadding))).IsReady);
                Check("private canonical tag decoder still rejects nonzero padding", !AuctionSellerNameTag13.TryDecode(dirtyPadding, out _));
                Check("piece accepts the same native post-NUL tail semantics", Piece(new(300, 3, Bits(orderId),
                    ImmutableArray.CreateRange(dirtyPadding), 3001)).IsReady);
                Check("piece still rejects a different seller prefix", Piece(new(300, 3, Bits(orderId),
                    ImmutableArray.CreateRange(wrongTag), 3001)).Status == AuctionNativeMutationStatus.Rejected);
                CheckMyBidsNativeTagRoundTrip(Check, row, sellerBytes, now);
                Check("missing seller tag is rejected", Bid(new(1000, Bits(orderId), default)).Status == AuctionNativeMutationStatus.Rejected);
                Check("wrong seller tag length is rejected", Bid(new(1000, Bits(orderId), ImmutableArray.CreateRange(new byte[12]))).Status == AuctionNativeMutationStatus.Rejected);
                Check("unencodable frozen seller never uses a lossy CP936 fallback", Bid(snapshot: Snapshot(row with { SellerName = "😀" })).Status == AuctionNativeMutationStatus.Unavailable);
                Check("amount zero and unsigned cash overflow are rejected", Bid(new(0, Bits(orderId), tag)).Status == AuctionNativeMutationStatus.Rejected
                    && Bid(new(uint.MaxValue, Bits(orderId), tag)).Status == AuctionNativeMutationStatus.Rejected);
                var sellerPrincipal = principal with { AccountId = seller.AccountId, CharacterId = seller.CharacterId };
                var ownSnapshot = Snapshot(owner: sellerPrincipal);
                var cancel = AuctionNativeMutationAdapter.NormalizeCancel(sellerPrincipal, seller, ownSnapshot, operation, new(Bits(orderId)), now);
                Check("cancel uses observed seller version without inventing a client version", cancel.IsReady
                    && cancel.Request.Action == AuctionOperationAction.Cancel && cancel.Request.ExpectedVersion == 23 && cancel.Request.Quantity == 10);
                Check("other owner cannot cancel an observed seller row", AuctionNativeMutationAdapter.NormalizeCancel(
                    principal, actor, Snapshot(), operation, new(Bits(orderId)), now).Status == AuctionNativeMutationStatus.Rejected);
                var funded = row with { CurrentOrOpeningBidGold = 110, HighestBidderCharacterId = actor.CharacterId, HighestBidderAccountId = actor.AccountId };
                Check("funded observed bid cannot be normalized as cancel", AuctionNativeMutationAdapter.NormalizeCancel(
                    sellerPrincipal, seller, Snapshot(funded, sellerPrincipal), operation, new(Bits(orderId)), now).Status == AuctionNativeMutationStatus.Rejected);
                Check("same account bid is rejected before Market final revalidation", AuctionNativeMutationAdapter.NormalizeBidOrBuyout(
                    sellerPrincipal, seller, ownSnapshot, operation, new(1000, Bits(orderId), tag), now).Status == AuctionNativeMutationStatus.Rejected);
                var piece = Piece();
                Check("piece binds exact template quantity frozen unit total and observed version", piece.IsReady
                    && piece.Request.Action == AuctionOperationAction.BuyPiece && piece.Request.Quantity == 3
                    && piece.Request.OfferedGold == 300 && piece.Request.ExpectedVersion == 23);
                Check("piece wrong template and unsigned template overflow are rejected", Piece(new(300, 3, Bits(orderId), tag, 3002)).Status == AuctionNativeMutationStatus.Rejected
                    && Piece(new(300, 3, Bits(orderId), tag, uint.MaxValue)).Status == AuctionNativeMutationStatus.Rejected);
                Check("piece amount cannot underpay exact frozen unit times quantity", Piece(new(299, 3, Bits(orderId), tag, 3001)).Status == AuctionNativeMutationStatus.Rejected);
                Check("piece amount cannot overpay or truncate exact total", Piece(new(301, 3, Bits(orderId), tag, 3001)).Status == AuctionNativeMutationStatus.Rejected
                    && Piece(new(uint.MaxValue, 3, Bits(orderId), tag, 3001)).Status == AuctionNativeMutationStatus.Rejected);
                Check("piece quantity zero or above remaining is rejected", Piece(new(0, 0, Bits(orderId), tag, 3001)).Status == AuctionNativeMutationStatus.Rejected
                    && Piece(new(1100, 11, Bits(orderId), tag, 3001)).Status == AuctionNativeMutationStatus.Rejected);
                var bulk = row with { RemainingQuantity = 10000, BuyoutGold = 1000000 };
                Check("evidenced piece quantity bound10000 is accepted exactly", Piece(new(1000000, 10000, Bits(orderId), tag, 3001), bulk).IsReady);
                Check("piece quantity above10000 remains rejected", Piece(new(1000100, 10001, Bits(orderId), tag, 3001),
                    bulk with { RemainingQuantity = 20000, BuyoutGold = 2000000 }).Status == AuctionNativeMutationStatus.Rejected);
                Check("nonexact bundle unit remains unavailable rather than divided silently", Piece(value: row with { ExactUnitPriceGold = null }).Status == AuctionNativeMutationStatus.Unavailable);
                Check("inconsistent observed exact unit and remaining total is unavailable", Piece(value: row with { ExactUnitPriceGold = 101 }).Status == AuctionNativeMutationStatus.Unavailable);
                Check("opening bid and funded bid both prohibit piece normalization", Piece(value: bidRow).Status == AuctionNativeMutationStatus.Rejected
                    && Piece(value: funded).Status == AuctionNativeMutationStatus.Rejected);
                Check("piece seller tag stays mandatory", Piece(new(300, 3, Bits(orderId), default, 3001)).Status == AuctionNativeMutationStatus.Rejected);
                Check("same account piece is rejected before Market final revalidation", AuctionNativeMutationAdapter.NormalizeBuyPiece(
                    sellerPrincipal, seller, ownSnapshot, operation, new(300, 3, Bits(orderId), tag, 3001), now).Status == AuctionNativeMutationStatus.Rejected);
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] unexpected native mutation adapter exception: " + ex); failed++; }
            Console.WriteLine($"AuctionNativeMutationAdapterSelfTest: passed={passed} failed={failed}; pure mapping only, no ACK/store/assets/runtime.");
            return failed == 0 ? 0 : 1;
        }

        private static void CheckMyBidsNativeTagRoundTrip(Action<string, bool> check,
            AuctionQueryRow prototype, byte[] canonicalTag, long now)
        {
            var principal = new AuctionQueryPrincipal(Guid.NewGuid(), 49, 1, 4);
            var actor = new AuctionParty(4, 1, "最高买家");
            var row = prototype with { OrderId = 5, OrderVersion = 4, TemplateId = 3156,
                SellerCharacterId = 2, SellerAccountId = 2, RemainingQuantity = 1,
                HighestBidderCharacterId = 4, HighestBidderAccountId = 1,
                CurrentOrOpeningBidGold = 600, BuyoutGold = 1000, ExactUnitPriceGold = null };
            var snapshot = new AuctionQuerySnapshot(principal, "MyBids native tag fixture", new string('a', 64),
                now - 1, now + 110, ImmutableArray.Create(row));
            var observations = new AuctionNativeObservations(principal,
                new AuctionItemCodec(new NativeAuctionInstanceExtension()));
            AuctionQuerySnapshot observed = null;
            check("MyBids records and resolves the actually sent v4 highest-bidder row", observations.RecordSentRows(
                AuctionQueryView.CurrentHighestBids, snapshot, new[] { row }, now, out _)
                && observations.TryGetObservedOrderSnapshot(5, now, out observed, out _));

            // Explicit wire-only fixture: native item bytes are not materialized
            // or treated as a persisted item. Exercise the production 159B writer.
            AuctionNativeItemWireSnapshot.TryCreate(new byte[83], new byte[30], new byte[4], out var native);
            AuctionMyBidsRecordWire.TryCreate(5, 600, 1000, canonicalTag, 0, 22, native, 0, out var record);
            var myBids = AuctionRecordBodyBuilder.Build(record);
            check("MyBids159 writer emits the canonical seller bytes at offset16", myBids.Length == 159
                && myBids.AsSpan(16, 13).SequenceEqual(canonicalTag));

            // Same-version evidence va-0232DA80:0232E28F..E2C7 copies only
            // through the first NUL into a stack temporary, without clearing tail.
            var temporary168 = new byte[168];
            Array.Fill(temporary168, (byte)0xA5);
            myBids.AsSpan(0, 8).CopyTo(temporary168.AsSpan(0, 8));
            myBids.AsSpan(8, 8).CopyTo(temporary168.AsSpan(9, 8));
            myBids.AsSpan(155, 4).CopyTo(temporary168.AsSpan(17, 4));
            temporary168[42] = myBids[37];
            myBids.AsSpan(38, 117).CopyTo(temporary168.AsSpan(43, 117));
            int copied = 0;
            while (copied < 13 && myBids[16 + copied] != 0)
            { temporary168[21 + copied] = myBids[16 + copied]; copied++; }
            if (copied == 13) copied--;
            temporary168[21 + copied] = 0;

            // va-0232D2A0:0232D2EC..FA copies every byte of168 into slot+358;
            // va-023253F0:02325471..AC sends full buyout, identity8 and tag13.
            var slot = new byte[0x358 + 168];
            temporary168.AsSpan().CopyTo(slot.AsSpan(0x358, 168));
            var body = new byte[26];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1, 4),
                BinaryPrimitives.ReadUInt32LittleEndian(slot.AsSpan(0x365, 4)));
            slot.AsSpan(0x358, 8).CopyTo(body.AsSpan(5, 8));
            slot.AsSpan(0x36D, 13).CopyTo(body.AsSpan(13, 13));
            check("MyBids reconstruction preserves name and NUL but carries a nonzero post-NUL tail",
                copied > 0 && copied < 12 && body.AsSpan(13, copied + 1).SequenceEqual(canonicalTag.AsSpan(0, copied + 1))
                && body[25] == 0xA5);

            AuctionNativeMutationResult NormalizePacket(byte[] packet)
            {
                if (!AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_BIDDING, packet, out var parsed)
                    || parsed is not AuctionBidRequest bid)
                    return null;
                return AuctionNativeMutationAdapter.NormalizeBidOrBuyout(principal, actor, observed,
                    Guid.NewGuid(), bid, now);
            }
            check("MyBids generated26B B9 uses the real parser with full1000 and identity5",
                AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_BIDDING, body, out var request)
                && request is AuctionBidRequest parsedBid && parsedBid.Amount == 1000 && parsedBid.IdentityBits == 5);
            var result = NormalizePacket(body);
            check($"MyBids native-copy B9 must be Ready Buyout (actual={result?.Status}/{result?.Reason})",
                result?.IsReady == true && result.Request.Action == AuctionOperationAction.Buyout
                && result.Request.OrderId == 5 && result.Request.ExpectedVersion == 4
                && result.Request.Actor == actor && result.Request.Quantity == 1 && result.Request.OfferedGold == 1000);

            byte[] WithTag(byte[] value)
            {
                var packet = body.AsSpan().ToArray();
                value.AsSpan().CopyTo(packet.AsSpan(13, 13));
                return packet;
            }
            AuctionSellerNameTag13.TryEncodeFrozenRoleName("别家", out var different);
            check("MyBids still rejects a different complete seller prefix",
                NormalizePacket(WithTag(different))?.Status == AuctionNativeMutationStatus.Rejected);
            AuctionSellerNameTag13.TryEncodeFrozenRoleName(prototype.SellerName.Substring(0, 1), out var shortened);
            check("MyBids still rejects a valid but shortened seller name",
                NormalizePacket(WithTag(shortened))?.Status == AuctionNativeMutationStatus.Rejected);
            var unterminated = canonicalTag.AsSpan().ToArray();
            for (int index = copied; index < 13; index++) unterminated[index] = 0xA5;
            check("MyBids still rejects tag13 without any NUL",
                NormalizePacket(WithTag(unterminated))?.Status == AuctionNativeMutationStatus.Rejected);
            check("MyBids wrong length25B cannot reach normalization", !AuctionRequestParser.TryParse(
                CmdPacketTypeA21.AUCTION_BIDDING, body.AsSpan(0, 25), out _));
        }
    }
}
