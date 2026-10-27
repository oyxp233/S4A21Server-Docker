using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Builders.Auction
{
    /// <summary>
    /// Fixed record bytes only; every unknown byte has an explicit immutable DTO source.
    /// Mapping: native 0232D2A0, listings 0232DDC4..0232E02F, bids 0232E073..0232E2DF.
    /// Evidence and compatibility scope: Docs/拍卖行业务与运营接口.md.
    /// These methods do not establish a canonical item projection or an executable order.
    /// </summary>
    public static class AuctionRecordBodyBuilder
    {
        public static byte[] Build(AuctionSearchRecordWire value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var body = new byte[168];
            U64(body, 0, value.IdentityBits);
            body[8] = value.Wire8;
            U32(body, 9, value.CurrentOrMinimumBidBits);
            U32(body, 13, value.BuyoutTotalBits);
            U32(body, 17, value.NativeArg21Bits);
            value.SellerTag.AsSpan().CopyTo(body.AsSpan(21, 13));
            U64(body, 34, value.Wire34Bits);
            body[42] = value.DisplayHours;
            value.NativeItem.CopyTo(body.AsSpan(43, 117));
            U32(body, 160, value.WarningTierValue);
            U32(body, 164, value.Wire164);
            return body;
        }

        public static byte[] Build(AuctionMyListingsRecordWire value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var body = new byte[151];
            U64(body, 0, value.IdentityBits);
            U32(body, 8, value.CurrentOrMinimumBidBits);
            U32(body, 12, value.BuyoutTotalBits);
            value.SellerTag.AsSpan().CopyTo(body.AsSpan(16, 13));
            body[29] = value.DisplayHours;
            value.NativeItem.CopyTo(body.AsSpan(30, 117));
            U32(body, 147, value.NativeArg21Bits);
            return body;
        }

        public static byte[] Build(AuctionMyBidsRecordWire value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var body = new byte[159];
            U64(body, 0, value.IdentityBits);
            U32(body, 8, value.CurrentOrMinimumBidBits);
            U32(body, 12, value.BuyoutTotalBits);
            value.SellerTag.AsSpan().CopyTo(body.AsSpan(16, 13));
            U64(body, 29, value.Wire29Bits);
            body[37] = value.DisplayHours;
            value.NativeItem.CopyTo(body.AsSpan(38, 117));
            U32(body, 155, value.NativeArg21Bits);
            return body;
        }

        private static void U32(byte[] body, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at), value);
        private static void U64(byte[] body, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(at), value);
    }
}
