using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction.Assets
{
    // Only the derivation of an already proved complete instance. This adapter
    // never grants native eligibility, allocates recipient identity or sends mail.
    internal sealed class AuctionTransferableItemAdapter
    {
        private readonly AuctionItemCodec codec;
        internal AuctionTransferableItemAdapter(AuctionItemCodec codec)
            => this.codec = codec ?? throw new ArgumentNullException(nameof(codec));

        // Existing AuctionMarketItemTransform signature; return and delivery use
        // the same owner-neutral core, followed by normal transactional mail claim.
        internal AuctionItemEnvelope Transform(AuctionItemEnvelope source, int quantity, bool returning)
        {
            var core = codec.DecodeStrict(source);
            if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
            if (InventoryStackRuleService.IsStackable(core))
            {
                if (quantity > core.Count) throw new ArgumentException("Transfer exceeds owned quantity.", nameof(quantity));
                core.Count = quantity;
            }
            else
            {
                if (quantity != 1) throw new ArgumentException("Nonstackable transfer quantity must be one.", nameof(quantity));
                if (core.ItemKind == ItemCore.KindAvatar) core.AvatarUid = 0;
                else if (core.ItemKind == ItemCore.KindCreature) core.CreatureUid = 0;
            }
            return codec.CreateTransferSnapshot(source, core);
        }
    }
}
