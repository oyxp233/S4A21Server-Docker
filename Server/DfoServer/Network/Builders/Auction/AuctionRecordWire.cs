using System;
using System.Collections.Immutable;

namespace DfoServer.Network.Builders.Auction
{
    /// <summary>Explicit native wire snapshot; no ItemCore projection or implicit padding.</summary>
    public sealed class AuctionNativeItemWireSnapshot
    {
        private AuctionNativeItemWireSnapshot(ReadOnlySpan<byte> native83, ReadOnlySpan<byte> additional30, ReadOnlySpan<byte> additional4)
        {
            Native83 = ImmutableArray.CreateRange(native83.ToArray());
            Additional30 = ImmutableArray.CreateRange(additional30.ToArray());
            Additional4 = ImmutableArray.CreateRange(additional4.ToArray());
        }

        public ImmutableArray<byte> Native83 { get; }
        public ImmutableArray<byte> Additional30 { get; }
        public ImmutableArray<byte> Additional4 { get; }

        public static bool TryCreate(ReadOnlySpan<byte> native83, ReadOnlySpan<byte> additional30,
            ReadOnlySpan<byte> additional4, out AuctionNativeItemWireSnapshot value)
        {
            value = null;
            if (native83.Length != 83 || additional30.Length != 30 || additional4.Length != 4) return false;
            value = new AuctionNativeItemWireSnapshot(native83, additional30, additional4);
            return true;
        }

        internal void CopyTo(Span<byte> target)
        {
            if (target.Length != 117) throw new ArgumentException("Exactly 117 native record bytes are required.", nameof(target));
            Native83.AsSpan().CopyTo(target);
            Additional30.AsSpan().CopyTo(target[83..]);
            Additional4.AsSpan().CopyTo(target[113..]);
        }
    }

    public abstract class AuctionRecordWire
    {
        protected AuctionRecordWire(ulong identityBits, uint currentOrMinimumBidBits, uint buyoutTotalBits,
            ReadOnlySpan<byte> sellerTag, byte displayHours, AuctionNativeItemWireSnapshot nativeItem, uint nativeArg21Bits)
        {
            IdentityBits = identityBits;
            CurrentOrMinimumBidBits = currentOrMinimumBidBits;
            BuyoutTotalBits = buyoutTotalBits;
            SellerTag = ImmutableArray.CreateRange(sellerTag.ToArray());
            DisplayHours = displayHours;
            NativeItem = nativeItem;
            NativeArg21Bits = nativeArg21Bits;
        }

        public ulong IdentityBits { get; }
        public uint CurrentOrMinimumBidBits { get; }
        public uint BuyoutTotalBits { get; }
        public ImmutableArray<byte> SellerTag { get; }
        public byte DisplayHours { get; }
        public AuctionNativeItemWireSnapshot NativeItem { get; }
        // Canonical ITEM_LIST supplies its unknown97 scalar to constructor argument21.
        public uint NativeArg21Bits { get; }
    }

    public sealed class AuctionSearchRecordWire : AuctionRecordWire
    {
        private AuctionSearchRecordWire(ulong identityBits, byte wire8, uint bidBits, uint buyoutBits, uint nativeArg21Bits,
            ReadOnlySpan<byte> tag, ulong wire34Bits, byte hours, AuctionNativeItemWireSnapshot item, uint warning, uint wire164)
            : base(identityBits, bidBits, buyoutBits, tag, hours, item, nativeArg21Bits)
        { Wire8 = wire8; Wire34Bits = wire34Bits; WarningTierValue = warning; Wire164 = wire164; }

        public byte Wire8 { get; }
        public ulong Wire34Bits { get; }
        public uint WarningTierValue { get; }
        public uint Wire164 { get; }

        public static bool TryCreate(ulong identityBits, byte wire8, uint currentOrMinimumBidBits, uint buyoutTotalBits,
            uint nativeArg21Bits, ReadOnlySpan<byte> sellerTag, ulong wire34Bits, byte displayHours,
            AuctionNativeItemWireSnapshot nativeItem, uint warningTierValue, uint wire164, out AuctionSearchRecordWire value)
        {
            value = null;
            if (sellerTag.Length != 13 || nativeItem == null) return false;
            value = new AuctionSearchRecordWire(identityBits, wire8, currentOrMinimumBidBits, buyoutTotalBits, nativeArg21Bits,
                sellerTag, wire34Bits, displayHours, nativeItem, warningTierValue, wire164);
            return true;
        }
    }

    public sealed class AuctionMyListingsRecordWire : AuctionRecordWire
    {
        private AuctionMyListingsRecordWire(ulong identityBits, uint bidBits, uint buyoutBits, ReadOnlySpan<byte> tag,
            byte hours, AuctionNativeItemWireSnapshot item, uint nativeArg21Bits)
            : base(identityBits, bidBits, buyoutBits, tag, hours, item, nativeArg21Bits) { }

        public static bool TryCreate(ulong identityBits, uint currentOrMinimumBidBits, uint buyoutTotalBits,
            ReadOnlySpan<byte> sellerTag, byte displayHours, AuctionNativeItemWireSnapshot nativeItem,
            uint nativeArg21Bits, out AuctionMyListingsRecordWire value)
        {
            value = null;
            if (sellerTag.Length != 13 || nativeItem == null) return false;
            value = new AuctionMyListingsRecordWire(identityBits, currentOrMinimumBidBits, buyoutTotalBits,
                sellerTag, displayHours, nativeItem, nativeArg21Bits);
            return true;
        }
    }

    public sealed class AuctionMyBidsRecordWire : AuctionRecordWire
    {
        private AuctionMyBidsRecordWire(ulong identityBits, uint bidBits, uint buyoutBits, ReadOnlySpan<byte> tag,
            ulong wire29Bits, byte hours, AuctionNativeItemWireSnapshot item, uint nativeArg21Bits)
            : base(identityBits, bidBits, buyoutBits, tag, hours, item, nativeArg21Bits) { Wire29Bits = wire29Bits; }

        public ulong Wire29Bits { get; }

        public static bool TryCreate(ulong identityBits, uint currentOrMinimumBidBits, uint buyoutTotalBits,
            ReadOnlySpan<byte> sellerTag, ulong wire29Bits, byte displayHours, AuctionNativeItemWireSnapshot nativeItem,
            uint nativeArg21Bits, out AuctionMyBidsRecordWire value)
        {
            value = null;
            if (sellerTag.Length != 13 || nativeItem == null) return false;
            value = new AuctionMyBidsRecordWire(identityBits, currentOrMinimumBidBits, buyoutTotalBits,
                sellerTag, wire29Bits, displayHours, nativeItem, nativeArg21Bits);
            return true;
        }
    }
}
