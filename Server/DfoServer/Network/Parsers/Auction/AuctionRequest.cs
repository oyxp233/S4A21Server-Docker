using System.Collections.Immutable;

namespace DfoServer.Network.Parsers.Auction
{
    // Immutable wire DTOs only. WireN denotes request offset; FieldN denotes the
    // evidenced source UI offset when a variable request tail has no fixed offset.
    public abstract record AuctionRequest;
    public sealed record AuctionAveragePriceRequest(uint ItemKey, byte AttributeA, byte AttributeB, byte AttributeC) : AuctionRequest;
    public readonly record struct AuctionFilterTuple(ushort Field, byte Value);
    public sealed record AuctionRegisterRequest(byte Space, ushort Slot, uint ItemKey, uint Quantity,
        uint Wire12, uint Wire16, uint Wire20, uint Wire24,
        AuctionFilterTuple FilterA, AuctionFilterTuple FilterB, AuctionFilterTuple FilterC) : AuctionRequest;
    public sealed record AuctionCancelRequest(ulong IdentityBits) : AuctionRequest;
    public sealed record AuctionBidRequest(uint Amount, ulong IdentityBits, ImmutableArray<byte> SellerTag) : AuctionRequest;
    public sealed record AuctionSearchByItemKeyRequest(uint Position, byte Wire5, byte Wire6, ushort Wire8,
        ImmutableArray<uint> ItemKeys, ushort FilterA, ushort FilterB, ushort FilterC, byte Field226, byte Field227) : AuctionRequest;
    public sealed record AuctionSearchWithoutItemKeyRequest(uint Position, ushort Wire5,
        byte Wire7, byte Wire8, byte Wire9, byte Wire10, byte Wire11,
        ushort FilterA, ushort FilterB, ushort FilterC, byte Field233, byte Field234) : AuctionRequest;
    public sealed record AuctionMyListingsRequest : AuctionRequest;
    public sealed record AuctionMyBidsRequest : AuctionRequest;
    public sealed record AuctionBuyApieceRequest(uint Amount, uint Quantity, ulong IdentityBits,
        ImmutableArray<byte> SellerTag, uint ItemKey) : AuctionRequest;
}
