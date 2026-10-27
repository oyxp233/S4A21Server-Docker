using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction.Assets
{
    // Composition must supply an adapter aware of the locally installed item
    // extensions. There is no permissive default that could discard paid state.
    internal interface IAuctionInstanceExtension
    {
        // The source identity must survive; native transfer changes have already
        // been decided by the caller. Capture against the FINAL native bytes.
        string Capture(ItemCore source, ItemCore finalTransferCore);

        // Reject malformed/unknown versions and keys rather than dropping them.
        void Restore(ItemCore target, string extensionsJson);
    }
}
