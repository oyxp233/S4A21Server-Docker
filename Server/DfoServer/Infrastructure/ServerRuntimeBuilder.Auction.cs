using System;
using System.IO;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.GameWorld.Auction;

namespace DfoServer.Infrastructure;

public sealed partial class ServerRuntimeBuilder
{
    private AuctionRuntime _auctionRuntime;

    // The real Program invokes this before packet intake. Convenience builders
    // used by unrelated offline SelfTests do not open a second private owner.
    internal void ConfigureAuctionRuntime()
    {
        ThrowIfDisposed();
        if (_gameProtocolBuilt || _auctionRuntime != null)
            throw new InvalidOperationException("Configure auction exactly once before building the protocol.");
        var core = GetOrCreateGameProtocolCoreDependencies();
        _auctionRuntime = new AuctionRuntime(Database, AuctionDatabase.DefaultPath, LoadAuctionCatalog,
            () => DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Environment.GetEnvironmentVariable("AUCTION_ENABLED") == "1",
            historyDatabasePath: Environment.GetEnvironmentVariable("AUCTION_HISTORY_DATABASE_PATH"));
        core.CharacterRepository.ConfigureDeletionGuard(_auctionRuntime.DeletionGuard.TryExecuteWhenClear);
    }

    internal static AuctionTemplateCatalog LoadAuctionCatalog()
        => AuctionTemplateCatalog.LoadIndexed(PvfArchiveAccessor.ReadText, PvfArchiveAccessor.SourceSha256,
            id => ItemMetadataResolver.TryLoadEquipmentFile(id, out var item) ? item
                : throw new InvalidDataException("Auction equipment template unavailable: " + id),
            id => ItemMetadataResolver.TryLoadStackableFile(id, out var item) ? item
                : throw new InvalidDataException("Auction stackable template unavailable: " + id));
}
