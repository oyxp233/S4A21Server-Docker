using System;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using DfoServer.GameWorld.Auction;

namespace DfoServer.Game.Auction.Queries
{
    internal enum AuctionQueryStatus { Available, InvalidRequest, Unavailable, SnapshotExpired }
    internal enum AuctionQueryView { Search, OwnActiveListings, CurrentHighestBids }
    internal enum AuctionQueryMatch { Unknown, NoMatch, Match }
    internal sealed record AuctionQueryResult<T>(AuctionQueryStatus Status, string Reason, T Value)
    {
        internal bool IsAvailable => Status == AuctionQueryStatus.Available;
    }
    internal sealed record AuctionQueryPrincipal(Guid SessionId, long Generation, int AccountId, int CharacterId);
    internal sealed record AuctionQueryCategoryRule(ushort NativeCategory, string SourcePvfSha256,
        string EvidenceReference, Func<AuctionTemplateProjection, AuctionQueryMatch> Matches);
    internal sealed record AuctionQueryCriteria(AuctionQueryView View, bool ByItemKeys,
        ImmutableArray<int> ItemKeys, ushort NativeCategory, byte ReinforcementMin,
        byte ReinforcementMax, byte ForgeMin, byte ForgeMax, byte Rarity,
        byte LevelMin, byte LevelMax, ushort RandomA, ushort RandomB, ushort RandomC);
    // Immutable display facts, not native wire records or authority to mutate an order.
    // The complete escrow descriptor stays internal and must not be serialized to a client.
    internal sealed record AuctionQueryRow(long OrderId, long OrderVersion, int TemplateId,
        int SellerCharacterId, int SellerAccountId, string SellerName,
        int? HighestBidderCharacterId, int? HighestBidderAccountId, int RemainingQuantity,
        long? CurrentOrOpeningBidGold, long? BuyoutGold, long? ExactUnitPriceGold,
        string DisplayName, int MinimumLevel, int Rarity, byte Reinforcement, byte Forge,
        long CreatedAtUnix, long ExpiresAtUnix, string EscrowEnvelopeJson, string EscrowContentHash);
    internal sealed class AuctionQuerySnapshot
    {
        internal AuctionQuerySnapshot(AuctionQueryPrincipal principal, string criteriaFingerprint,
            string pvfHash, long capturedAtUnix, long validUntilUnix, ImmutableArray<AuctionQueryRow> rows)
        {
            Principal = principal; CriteriaFingerprint = criteriaFingerprint; CatalogPvfSha256 = pvfHash;
            CapturedAtUnix = capturedAtUnix; ValidUntilUnix = validUntilUnix; Rows = rows;
            // Deterministic read identity; no wall clock/random source inside pure queries.
            SnapshotId = new Guid(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
                principal, criteriaFingerprint, pvfHash, capturedAtUnix, validUntilUnix, rows
            })).AsSpan(0, 16));
        }
        internal Guid SnapshotId { get; }
        internal AuctionQueryPrincipal Principal { get; }
        internal string CriteriaFingerprint { get; }
        internal string CatalogPvfSha256 { get; }
        internal long CapturedAtUnix { get; }
        internal long ValidUntilUnix { get; }
        internal ImmutableArray<AuctionQueryRow> Rows { get; }
        internal string PolicyVersion => AuctionQueryEngine.PolicyVersion;
    }
    internal sealed record AuctionQueryPage(Guid SnapshotId, long CapturedAtUnix,
        int ReportedTotalCount, ImmutableArray<AuctionQueryRow> Rows);
    internal sealed record AuctionQueryPriceLevel(long UnitPriceGold, int Quantity);
    internal sealed record AuctionQueryPriceReference(long CapturedAtUnix, string CatalogPvfSha256,
        string PolicyVersion, long? SuggestedPriceScalar, long? UnknownScalar,
        ImmutableArray<AuctionQueryPriceLevel> Levels);
}
