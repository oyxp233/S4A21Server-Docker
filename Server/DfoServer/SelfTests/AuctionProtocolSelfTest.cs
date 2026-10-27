using System;
using System.Buffers.Binary;
using System.Linq;
using DfoServer.Network;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Parsers.Auction;

namespace DfoServer.SelfTests
{
    public static class AuctionProtocolSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            void Check(string name, bool condition)
            {
                Console.WriteLine((condition ? "[PASS] " : "[FAIL] ") + name);
                if (!condition) failures++;
            }
            Console.WriteLine("=== AUCTION_PROTOCOL selftest ===");
            try
            {
                var cases = new (CmdPacketTypeA21 Command, int Length)[]
                {
                    (CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE, 8),
                    (CmdPacketTypeA21.AUCTION_REGIST_ITEM, 37),
                    (CmdPacketTypeA21.AUCTION_REGIST_CANCEL, 9),
                    (CmdPacketTypeA21.AUCTION_BIDDING, 26),
                    (CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY, 20),
                    (CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO, 1),
                    (CmdPacketTypeA21.AUCTION_MY_BIDDING_INFO, 1),
                    (CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE, 33)
                };
                foreach (var entry in cases)
                {
                    var body = Fixture(entry.Length);
                    Check(entry.Command + " exact length", AuctionRequestParser.TryParse(entry.Command, body, out _));
                    for (var length = 0; length < body.Length; length++)
                        Check(entry.Command + " rejects truncated " + length,
                            !AuctionRequestParser.TryParse(entry.Command, body.AsSpan(0, length), out var rejected) && rejected == null);
                    Check(entry.Command + " rejects trailing data",
                        !AuctionRequestParser.TryParse(entry.Command, body.Concat(new byte[] { 0 }).ToArray(), out _));
                    body[0] = 1;
                    Check(entry.Command + " ordinary selector boundary",
                        AuctionRequestParser.TryParse(entry.Command, body, out _) == (entry.Command == CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE));
                }

                foreach (var count in new[] { 0, 1, 255 })
                {
                    var body = Fixture(18 + 4 * count);
                    body[7] = (byte)count;
                    Check("search key count " + count, AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY, body, out var parsed)
                        && parsed is AuctionSearchByItemKeyRequest search && search.ItemKeys.Length == count);
                    for (var length = 0; length < body.Length; length++)
                        if (AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY, body.AsSpan(0, length), out _))
                            throw new InvalidOperationException("Search accepted a truncated declared key array.");
                    Check("search rejects trailing " + count,
                        !AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY, body.Concat(new byte[] { 0 }).ToArray(), out _));
                    body[0] = 1;
                    Check("search rejects other service " + count,
                        !AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY, body, out _));
                }

                AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_REGIST_ITEM, Fixture(37), out var registered);
                Check("register little endian offsets and filter tuple", registered is AuctionRegisterRequest reg
                    && reg.Space == 1 && reg.Slot == 0x0302 && reg.ItemKey == 0x07060504 && reg.Quantity == 0x0B0A0908
                    && reg.Wire12 == 0x0F0E0D0C && reg.Wire24 == 0x1B1A1918
                    && reg.FilterA == new AuctionFilterTuple(0x1D1C, 0x1E)
                    && reg.FilterC == new AuctionFilterTuple(0x2322, 0x24));
                var bidBody = Fixture(26);
                AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_BIDDING, bidBody, out var bidParsed);
                var bid = (AuctionBidRequest)bidParsed;
                Array.Fill(bidBody, (byte)0xFF);
                Check("bid identity and tag are owned snapshots", bid.Amount == 0x04030201
                    && bid.IdentityBits == 0x0C0B0A0908070605UL && bid.SellerTag[0] == 13 && bid.SellerTag[12] == 25);
                var apieceBody = Fixture(33);
                apieceBody[0] = 0x80;
                AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE, apieceBody, out var apieceParsed);
                var apiece = (AuctionBuyApieceRequest)apieceParsed;
                Array.Clear(apieceBody);
                Check("apiece has no selector and owns tag", apiece.Amount == 0x03020180
                    && apiece.Quantity == 0x07060504 && apiece.SellerTag[0] == 16 && apiece.ItemKey == 0x201F1E1D);
                var keyBody = Fixture(22);
                keyBody[7] = 1;
                AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY, keyBody, out var keyParsed);
                Array.Clear(keyBody);
                Check("search keys are owned snapshots", keyParsed is AuctionSearchByItemKeyRequest key
                    && key.ItemKeys[0] == 0x0D0C0B0A && key.FilterA == 0x0F0E
                    && key.Field226 == 20 && key.Field227 == 21);
                AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY, Fixture(20), out var noKeyParsed);
                Check("search without key preserves both numeric tail fields", noKeyParsed is AuctionSearchWithoutItemKeyRequest noKey
                    && noKey.Field233 == 18 && noKey.Field234 == 19);
                Check("history remains unsupported", !AuctionRequestParser.TryParse(CmdPacketTypeA21.AUCTION_MY_AUCTION_HISTORY, new byte[] { 0 }, out _));
                Check("unrelated command remains unsupported", !AuctionRequestParser.TryParse(default, new byte[] { 0 }, out _));

                Check("empty search body preserves reported total count", AuctionOrdinaryBodyBuilder.EmptySearchSuccess(0x78563412)
                    .SequenceEqual(new byte[] { 1, 0, 0x12, 0x34, 0x56, 0x78, 0, 0 }));
                Check("empty listings exact body", AuctionOrdinaryBodyBuilder.EmptyListingsSuccess().SequenceEqual(new byte[] { 1, 0, 0 }));
                Check("empty bids exact body", AuctionOrdinaryBodyBuilder.EmptyBidsSuccess().SequenceEqual(new byte[] { 1, 0, 0, 0 }));
                Check("ordinary service bodies", AuctionOrdinaryBodyBuilder.Service(true).SequenceEqual(new byte[] { 0, 1 })
                    && AuctionOrdinaryBodyBuilder.Service(false).SequenceEqual(new byte[] { 0, 0 }));
                uint[] valuesA = { 1, 2, 3, 4, 5, uint.MaxValue };
                uint[] valuesB = { 6, 7, 8, 9, 10, 0x80000000 };
                var average = AuctionOrdinaryBodyBuilder.AveragePriceSuccess(0x12345678, 0xFEDCBA98, valuesA, valuesB);
                Array.Clear(valuesA);
                Array.Clear(valuesB);
                Check("average exact width and array boundaries", average.Length == 58 && average[0] == 1 && average[1] == 0
                    && BinaryPrimitives.ReadUInt32LittleEndian(average.AsSpan(2)) == 0x12345678
                    && BinaryPrimitives.ReadUInt32LittleEndian(average.AsSpan(6)) == 0xFEDCBA98
                    && BinaryPrimitives.ReadUInt32LittleEndian(average.AsSpan(10)) == 1
                    && BinaryPrimitives.ReadUInt32LittleEndian(average.AsSpan(30)) == uint.MaxValue
                    && BinaryPrimitives.ReadUInt32LittleEndian(average.AsSpan(34)) == 6
                    && BinaryPrimitives.ReadUInt32LittleEndian(average.AsSpan(54)) == 0x80000000);
                foreach (var length in new[] { 0, 5, 7 })
                {
                    Check("average rejects first array width " + length, RejectAverage(new uint[length], new uint[6]));
                    Check("average rejects second array width " + length, RejectAverage(new uint[6], new uint[length]));
                }
                CheckRecords(Check);
                CheckIdentity(Check);
                CheckSellerNameTag(Check);
            }
            catch (Exception exception)
            {
                Console.WriteLine("[FAIL] " + exception);
                failures++;
            }
            Console.WriteLine("AUCTION_PROTOCOL failures=" + failures);
            return failures == 0 ? 0 : 1;
        }

        private static byte[] Fixture(int length)
        {
            var body = new byte[length];
            for (var index = 1; index < length; index++) body[index] = unchecked((byte)index);
            return body;
        }

        private static void CheckRecords(Action<string, bool> check)
        {
            var native = Enumerable.Range(1, 83).Select(index => (byte)index).ToArray();
            var extra30 = Enumerable.Range(0x80, 30).Select(index => (byte)index).ToArray();
            var extra4 = Convert.FromHexString("DEADBEEF");
            var tag = Enumerable.Range(0xB0, 13).Select(index => (byte)index).ToArray();
            var preservedNative = native.Concat(extra30).Concat(extra4).ToArray();
            var preservedTag = tag.ToArray();
            foreach (var length in new[] { 0, 82, 84, 4096 })
                check("native rejects width " + length,
                    !AuctionNativeItemWireSnapshot.TryCreate(new byte[length], extra30, extra4, out var bad) && bad == null);
            foreach (var length in new[] { 0, 29, 31 })
                check("additional30 rejects width " + length,
                    !AuctionNativeItemWireSnapshot.TryCreate(native, new byte[length], extra4, out _));
            foreach (var length in new[] { 0, 3, 5 })
                check("additional4 rejects width " + length,
                    !AuctionNativeItemWireSnapshot.TryCreate(native, extra30, new byte[length], out _));
            check("native exact widths", AuctionNativeItemWireSnapshot.TryCreate(native, extra30, extra4, out var snapshot));

            foreach (var length in new[] { 0, 12, 14 })
            {
                check("search tag rejects width " + length, !AuctionSearchRecordWire.TryCreate(1, 2, 3, 4, 5,
                    new byte[length], 6, 7, snapshot, 8, 9, out _));
                check("listings tag rejects width " + length, !AuctionMyListingsRecordWire.TryCreate(1, 2, 3,
                    new byte[length], 4, snapshot, 5, out _));
                check("bids tag rejects width " + length, !AuctionMyBidsRecordWire.TryCreate(1, 2, 3,
                    new byte[length], 4, 5, snapshot, 6, out _));
            }
            check("search rejects missing native", !AuctionSearchRecordWire.TryCreate(1, 2, 3, 4, 5, tag, 6, 7, null, 8, 9, out _));
            check("listings rejects missing native", !AuctionMyListingsRecordWire.TryCreate(1, 2, 3, tag, 4, null, 5, out _));
            check("bids rejects missing native", !AuctionMyBidsRecordWire.TryCreate(1, 2, 3, tag, 4, 5, null, 6, out _));

            check("search explicit record", AuctionSearchRecordWire.TryCreate(0x0102030405060708UL, 0xAA,
                uint.MaxValue, 0x10203040, 0x55667788, tag, 0x1122334455667788UL, 0x18,
                snapshot, 0xA1B2C3D4, 0x10293847, out var search));
            check("listings explicit record", AuctionMyListingsRecordWire.TryCreate(0x0102030405060708UL,
                uint.MaxValue, 0x10203040, tag, 0x18, snapshot, 0x55667788, out var listings));
            check("bids explicit record", AuctionMyBidsRecordWire.TryCreate(0x0102030405060708UL,
                uint.MaxValue, 0x10203040, tag, 0x9988776655443322UL, 0x18, snapshot, 0x55667788, out var bids));
            Array.Fill(native, (byte)0xFF);
            Array.Fill(extra30, (byte)0xFF);
            Array.Fill(extra4, (byte)0xFF);
            Array.Fill(tag, (byte)0xFF);

            var searchExpected = Join(Convert.FromHexString("0807060504030201AAFFFFFFFF4030201088776655"), preservedTag,
                Convert.FromHexString("887766554433221118"), preservedNative, Convert.FromHexString("D4C3B2A147382910"));
            var listingsExpected = Join(Convert.FromHexString("0807060504030201FFFFFFFF40302010"), preservedTag,
                new byte[] { 0x18 }, preservedNative, Convert.FromHexString("88776655"));
            var bidsExpected = Join(Convert.FromHexString("0807060504030201FFFFFFFF40302010"), preservedTag,
                Convert.FromHexString("223344556677889918"), preservedNative, Convert.FromHexString("88776655"));
            var searchBytes = AuctionRecordBodyBuilder.Build(search);
            var listingsBytes = AuctionRecordBodyBuilder.Build(listings);
            var bidsBytes = AuctionRecordBodyBuilder.Build(bids);
            check("search168 complete fixture with opaque bytes", searchBytes.Length == 168 && searchBytes.SequenceEqual(searchExpected));
            check("listings151 complete fixture", listingsBytes.Length == 151 && listingsBytes.SequenceEqual(listingsExpected));
            check("bids159 complete fixture with opaque extra8", bidsBytes.Length == 159 && bidsBytes.SequenceEqual(bidsExpected));
            var searchBody = AuctionOrdinaryBodyBuilder.SearchSuccess(0x78563412, new[] { search, search });
            check("search response total/count/record boundaries without tail", searchBody.Length == 344
                && searchBody[0] == 1 && searchBody[1] == 0
                && BinaryPrimitives.ReadUInt32LittleEndian(searchBody.AsSpan(2)) == 0x78563412
                && BinaryPrimitives.ReadUInt16LittleEndian(searchBody.AsSpan(6)) == 2
                && searchBody.AsSpan(8, 168).SequenceEqual(searchExpected)
                && searchBody.AsSpan(176, 168).SequenceEqual(searchExpected));
            check("zero-record search uses the same total/count contract",
                AuctionOrdinaryBodyBuilder.SearchSuccess(123, ReadOnlySpan<AuctionSearchRecordWire>.Empty)
                    .SequenceEqual(AuctionOrdinaryBodyBuilder.EmptySearchSuccess(123)));
            var maxBody = AuctionOrdinaryBodyBuilder.SearchSuccess(500, Enumerable.Repeat(search, 60).ToArray());
            var maxFrame = GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY, maxBody);
            check("search maximum ordinary batch and current envelope widths", maxBody.Length == 10088
                && BinaryPrimitives.ReadUInt16LittleEndian(maxBody.AsSpan(6)) == 60 && maxFrame.Length == 10103
                && BinaryPrimitives.ReadUInt32LittleEndian(maxFrame.AsSpan(3)) == maxFrame.Length
                && maxFrame.AsSpan(15).SequenceEqual(maxBody));
            var overBatchRejected = false;
            try { AuctionOrdinaryBodyBuilder.SearchSuccess(500, Enumerable.Repeat(search, 61).ToArray()); }
            catch (ArgumentOutOfRangeException) { overBatchRejected = true; }
            check("search private outbound policy rejects batch beyond 60", overBatchRejected);
            var missingRecordRejected = false;
            try { AuctionOrdinaryBodyBuilder.SearchSuccess(1, new AuctionSearchRecordWire[] { null }); }
            catch (ArgumentException) { missingRecordRejected = true; }
            check("search rejects missing record", missingRecordRejected);
            Array.Clear(searchBody);
            check("search body output does not mutate record snapshot", AuctionRecordBodyBuilder.Build(search).SequenceEqual(searchExpected));
            Array.Clear(searchBytes);
            Array.Clear(listingsBytes);
            Array.Clear(bidsBytes);
            check("record outputs do not mutate immutable snapshots", AuctionRecordBodyBuilder.Build(search).SequenceEqual(searchExpected)
                && AuctionRecordBodyBuilder.Build(listings).SequenceEqual(listingsExpected)
                && AuctionRecordBodyBuilder.Build(bids).SequenceEqual(bidsExpected));
            check("null search DTO rejected", RejectNullRecord(() => AuctionRecordBodyBuilder.Build((AuctionSearchRecordWire)null)));
            check("null listings DTO rejected", RejectNullRecord(() => AuctionRecordBodyBuilder.Build((AuctionMyListingsRecordWire)null)));
            check("null bids DTO rejected", RejectNullRecord(() => AuctionRecordBodyBuilder.Build((AuctionMyBidsRecordWire)null)));
        }

        private static byte[] Join(params byte[][] pieces) => pieces.SelectMany(piece => piece).ToArray();

        private static void CheckIdentity(Action<string, bool> check)
        {
            foreach (var value in new[] { 1L, 0x0102030405060708L, long.MaxValue })
                check("order identity roundtrip " + value, AuctionOrderWireIdentity.TryDecode(AuctionOrderWireIdentity.Encode(value), out var decoded)
                    && decoded == value);
            check("order identity explicit little endian", AuctionOrderWireIdentity.Encode(0x0102030405060708L)
                .SequenceEqual(Convert.FromHexString("0807060504030201")));
            foreach (var length in new[] { 0, 7, 9 })
                check("order identity rejects width " + length, !AuctionOrderWireIdentity.TryDecode(new byte[length], out var rejected) && rejected == 0);
            foreach (var invalid in new[] { "0000000000000000", "0000000000000080", "FFFFFFFFFFFFFFFF" })
                check("order identity rejects nonpositive " + invalid,
                    !AuctionOrderWireIdentity.TryDecode(Convert.FromHexString(invalid), out var rejected) && rejected == 0);
            foreach (var invalid in new[] { 0L, -1L, long.MinValue })
            {
                var rejected = false;
                try { AuctionOrderWireIdentity.Encode(invalid); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                check("order identity encode rejects " + invalid, rejected);
            }
        }

        private static void CheckSellerNameTag(Action<string, bool> check)
        {
            foreach (var name in new[] { "A", "ABCDEFGHIJKL", "方糖咖啡厅甲" })
            {
                check("seller name roundtrip " + name,
                    AuctionSellerNameTag13.TryEncodeFrozenRoleName(name, out var tag)
                    && tag.Length == 13 && tag[12] == 0
                    && AuctionSellerNameTag13.TryDecode(tag, out var decoded) && decoded == name);
            }
            AuctionSellerNameTag13.TryEncodeFrozenRoleName("方糖咖啡厅甲", out var chineseTag);
            check("seller name exact CP936 fixture", chineseTag.SequenceEqual(
                new byte[] { 0xB7, 0xBD, 0xCC, 0xC7, 0xBF, 0xA7, 0xB7, 0xC8, 0xCC, 0xFC, 0xBC, 0xD7, 0 }));
            AuctionSellerNameTag13.TryEncodeFrozenRoleName("A", out var shortTag);
            check("seller name zero padding", shortTag[0] == 0x41 && shortTag.Skip(1).All(value => value == 0));
            shortTag[0] = 0x42;
            check("seller name encode returns fresh bytes",
                AuctionSellerNameTag13.TryEncodeFrozenRoleName("A", out var fresh) && fresh[0] == 0x41);
            foreach (var name in new[] { null, "", "ABCDEFGHIJKLM", "方糖咖啡厅甲乙", "A\0B", "\U0001F600", "\uD800" })
                check("seller name rejects invalid input " + (name == null ? "null" : name.Length.ToString()),
                    !AuctionSellerNameTag13.TryEncodeFrozenRoleName(name, out var rejected) && rejected == null);
            foreach (var length in new[] { 0, 12, 14 })
                check("seller tag rejects width " + length,
                    !AuctionSellerNameTag13.TryDecode(new byte[length], out var rejected) && rejected == string.Empty);
            check("seller tag rejects empty name", !AuctionSellerNameTag13.TryDecode(new byte[13], out _));
            check("seller tag requires NUL", !AuctionSellerNameTag13.TryDecode(Enumerable.Repeat((byte)0x41, 13).ToArray(), out _));
            var malformed = new byte[13];
            malformed[0] = 0x81;
            check("seller tag rejects invalid CP936 prefix", !AuctionSellerNameTag13.TryDecode(malformed, out _));
            var noncanonical = new byte[13];
            noncanonical[0] = 0x41;
            noncanonical[12] = 0x42;
            check("seller tag private canonical policy rejects nonzero NUL tail",
                !AuctionSellerNameTag13.TryDecode(noncanonical, out var rejectedTail) && rejectedTail == string.Empty);
        }

        private static bool RejectNullRecord(Func<byte[]> build)
        {
            try { build(); return false; }
            catch (ArgumentNullException) { return true; }
        }

        private static bool RejectAverage(uint[] first, uint[] second)
        {
            try { AuctionOrdinaryBodyBuilder.AveragePriceSuccess(0, 0, first, second); return false; }
            catch (ArgumentException) { return true; }
        }
    }
}
