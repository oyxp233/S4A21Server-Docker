using System;
using System.Linq;
using System.Text.Json;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction.Assets;

// The upstream ItemCore has no out-of-band instance fields. Installations with
// extra state must provide a matching adapter instead of discarding that state.
internal sealed class NativeAuctionInstanceExtension : IAuctionInstanceExtension
{
    public string Capture(ItemCore source, ItemCore finalTransferCore)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(finalTransferCore);
        return "{}";
    }

    public void Restore(ItemCore target, string extensionsJson)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var document = JsonDocument.Parse(extensionsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any())
            throw new JsonException("Unsupported auction instance extension.");
    }
}
