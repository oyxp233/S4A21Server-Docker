using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction.Market
{
    // Server-domain request; none of these IDs/versions claim native wire layout.
    internal sealed record AuctionMarketRequest(Guid OperationId, AuctionOperationAction Action,
        long OrderId, long ExpectedVersion, AuctionParty Actor, int Quantity,
        long OfferedGold, long RequestedExpiryUnix, long? OpeningBidGold = null,
        long? BuyoutGold = null, long? UnitPriceGold = null,
        InventoryListType SourceList = InventoryListType.Main, short SourceSlot = -1, int DurationHours = 0)
    {
        internal AuctionQuoteBinding Binding() => new(Action, OrderId, ExpectedVersion,
            Actor.CharacterId, Actor.AccountId, Quantity, OfferedGold, RequestedExpiryUnix,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this)))));
    }

    internal enum AuctionMarketStatus { Planned, Unavailable, Rejected, Pending, Committed, Aborted, Quarantined }
    internal sealed class AuctionMarketUnavailableException : InvalidOperationException
    { internal AuctionMarketUnavailableException(string message) : base(message) { } }

    internal sealed record AuctionMarketPlan(AuctionOrderTransition Transition, long GoldDebit);
    internal sealed record AuctionMarketPlanningResult(AuctionMarketStatus Status, string Reason, AuctionMarketPlan Plan);
    internal sealed record AuctionMarketResult(AuctionMarketStatus Status, string Reason,
        AuctionOrderSnapshot Order, AuctionJournalEntry Operation);

    // Native transfer eligibility/count changes remain an explicit capability.
    // A fixture may preserve fields, but no such implementation is a default.
    internal delegate AuctionItemEnvelope AuctionMarketItemTransform(AuctionItemEnvelope source, int quantity, bool returning);
    internal delegate AuctionVerifiedQuote AuctionMarketQuoteProvider(AuctionMarketRequest request, AuctionOrderSnapshot before, long nowUnix);
}
