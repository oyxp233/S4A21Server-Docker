using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Queries;

namespace DfoServer.Network.Handlers.Auction;

// Owned by one connection and one InventoryLease generation. Access is serialized
// by the existing character transition and session-send locks.
internal sealed class AuctionNativeSessionState
{
    private readonly Dictionary<RequestKey, Mutation> requests = new();
    internal AuctionNativeSessionState(AuctionQueryPrincipal principal, AuctionItemCodec codec)
    { Principal = principal; Observations = new(principal, codec); }
    internal AuctionQueryPrincipal Principal { get; }
    internal AuctionNativeObservations Observations { get; }
    internal Dictionary<AuctionQueryView, AuctionQuerySnapshot> Queries { get; } = new();

    internal bool TryGetMutation(GamePacketHeader header, byte[] body, out Mutation mutation)
        => requests.TryGetValue(Key(header, body), out mutation);
    internal bool CanRememberMutation => requests.Count < 4096;
    internal Mutation RememberMutation(GamePacketHeader header, byte[] body, AuctionMarketRequest request)
    {
        if (!CanRememberMutation) throw new InvalidOperationException("Auction session request budget exhausted.");
        var mutation = new Mutation(request);
        requests.Add(Key(header, body), mutation);
        return mutation;
    }
    private static RequestKey Key(GamePacketHeader header, byte[] body)
        => new(header.type, header.seq, header.checksum, Convert.ToHexString(SHA256.HashData(body)));
    private sealed record RequestKey(ushort Command, ushort Sequence, uint Checksum, string BodyHash);
    internal sealed class Mutation
    {
        internal Mutation(AuctionMarketRequest request) => Request = request;
        internal AuctionMarketRequest Request { get; }
        internal AuctionMarketResult Result { get; set; }
        internal bool Acknowledged { get; set; }
    }
}
