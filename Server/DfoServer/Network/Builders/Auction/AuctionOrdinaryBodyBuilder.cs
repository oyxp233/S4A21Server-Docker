using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Builders.Auction
{
    /// <summary>
    /// Body only, ordinary identity zero. No envelope/registration or transaction success is emitted.
    /// Consumers: 0111F970 search, 0111FC90 listings, 0111FE70 bids, 0112CBB0 average,
    /// 01189FB0 service. See Docs/拍卖行业务与运营接口.md.
    /// </summary>
    public static class AuctionOrdinaryBodyBuilder
    {
        // Private outbound batch limit matching the ordinary UI's 60-record fetch
        // blocks (023406DF..0716). The native ACK decoder itself reads a u16 count;
        // this is not a claim that its decoder enforces a hard 60-record limit.
        public const int MaxOrdinarySearchRecords = 60;

        public static byte[] EmptySearchSuccess(uint reportedTotalCount)
        {
            var body = new byte[8];
            body[0] = 1;
            // 0111F9B1 -> 023388D9 -> 02322B50 computes pages from this scalar.
            // It is not the request position and no hasNext/tail control is written.
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), reportedTotalCount);
            return body;
        }

        public static byte[] SearchSuccess(uint reportedTotalCount, ReadOnlySpan<AuctionSearchRecordWire> records)
        {
            if (records.Length > MaxOrdinarySearchRecords)
                throw new ArgumentOutOfRangeException(nameof(records), "The ordinary UI response batch limit is 60 records.");
            for (var index = 0; index < records.Length; index++)
                if (records[index] == null) throw new ArgumentException("Every search record is required.", nameof(records));
            var count = checked((ushort)records.Length);
            var bodyLength = checked(8 + 168 * count);
            _ = checked(bodyLength + 15); // Current server-to-client envelope length.
            var body = new byte[bodyLength];
            body[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), reportedTotalCount);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), count);
            for (var index = 0; index < records.Length; index++)
                AuctionRecordBodyBuilder.Build(records[index]).AsSpan().CopyTo(body.AsSpan(8 + 168 * index, 168));
            return body;
        }

        public static byte[] EmptyListingsSuccess() => new byte[] { 1, 0, 0 };
        public static byte[] EmptyBidsSuccess() => new byte[] { 1, 0, 0, 0 };

        // 0232F280 uses the first scalar to suggest prices. 023326F0 renders matching
        // unit-price/quantity rows; no time periods, sorting, grouping or server calculation is inferred.
        public static byte[] AveragePriceSuccess(uint suggestedPriceScalar, uint unknownScalar,
            ReadOnlySpan<uint> unitPrices, ReadOnlySpan<uint> quantities)
        {
            if (unitPrices.Length != 6) throw new ArgumentException("Exactly six raw values are required.", nameof(unitPrices));
            if (quantities.Length != 6) throw new ArgumentException("Exactly six raw values are required.", nameof(quantities));
            var body = new byte[58];
            body[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), suggestedPriceScalar);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), unknownScalar);
            for (var index = 0; index < 6; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(10 + 4 * index), unitPrices[index]);
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(34 + 4 * index), quantities[index]);
            }
            return body;
        }

        public static byte[] Service(bool enabled) => new byte[] { 0, enabled ? (byte)1 : (byte)0 };
    }
}
