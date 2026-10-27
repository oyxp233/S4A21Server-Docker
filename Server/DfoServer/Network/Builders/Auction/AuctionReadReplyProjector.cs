using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders.Auction
{
    // M4-CONTRACT's ordinary display policy only. No socket, order mutation or
    // permission to reconstruct assets from the resulting native display bytes.
    internal static class AuctionReadReplyProjector
    {
        internal static bool TrySearch(AuctionQueryPage page, AuctionItemCodec codec, long nowUnix,
            out byte[] body, out string reason)
        {
            body = null; reason = "";
            if (page == null || page.Rows.IsDefault || page.ReportedTotalCount < 0
                || page.ReportedTotalCount < page.Rows.Length || page.Rows.Length > 60
                || nowUnix < 0 || page.CapturedAtUnix < 0 || page.CapturedAtUnix > nowUnix || codec == null)
                return Fail("invalid-search-page", out reason);
            var records = new AuctionSearchRecordWire[page.Rows.Length];
            var ids = new HashSet<long>();
            for (int i = 0; i < records.Length; i++)
            {
                var row = page.Rows[i];
                if (!TryRow(row, codec, nowUnix, ids, out var display, out reason)) return false;
                if (!AuctionSearchRecordWire.TryCreate((ulong)row.OrderId, 0, display.Bid, display.Buyout,
                    display.Arg21, display.Tag, 0, display.Hours, display.Item, 0, 0, out records[i]))
                    return Fail("invalid-search-record", out reason);
            }
            body = AuctionOrdinaryBodyBuilder.SearchSuccess((uint)page.ReportedTotalCount, records);
            return true;
        }

        internal static bool TryListings(IReadOnlyList<AuctionQueryRow> rows, AuctionItemCodec codec,
            long nowUnix, out byte[] body, out string reason)
            => TryOwned(rows, codec, nowUnix, false, out body, out reason);

        internal static bool TryBids(IReadOnlyList<AuctionQueryRow> rows, AuctionItemCodec codec,
            long nowUnix, out byte[] body, out string reason)
            => TryOwned(rows, codec, nowUnix, true, out body, out reason);

        private static bool TryOwned(IReadOnlyList<AuctionQueryRow> rows, AuctionItemCodec codec,
            long nowUnix, bool bids, out byte[] body, out string reason)
        {
            body = null; reason = "";
            if (rows == null || rows.Count > 255 || codec == null || nowUnix < 0)
                return Fail("invalid-owned-list", out reason);
            int prefix = bids ? 4 : 3, stride = bids ? 159 : 151;
            var candidate = new byte[prefix + stride * rows.Count];
            candidate[0] = 1;
            if (bids) BinaryPrimitives.WriteUInt16LittleEndian(candidate.AsSpan(2, 2), (ushort)rows.Count);
            else candidate[2] = (byte)rows.Count;
            var ids = new HashSet<long>();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (!TryRow(row, codec, nowUnix, ids, out var display, out reason)) return false;
                byte[] record;
                if (bids)
                {
                    if (!AuctionMyBidsRecordWire.TryCreate((ulong)row.OrderId, display.Bid, display.Buyout,
                        display.Tag, 0, display.Hours, display.Item, display.Arg21, out var value))
                        return Fail("invalid-bid-record", out reason);
                    record = AuctionRecordBodyBuilder.Build(value);
                }
                else
                {
                    if (!AuctionMyListingsRecordWire.TryCreate((ulong)row.OrderId, display.Bid, display.Buyout,
                        display.Tag, display.Hours, display.Item, display.Arg21, out var value))
                        return Fail("invalid-listing-record", out reason);
                    record = AuctionRecordBodyBuilder.Build(value);
                }
                record.AsSpan().CopyTo(candidate.AsSpan(prefix + stride * i, stride));
            }
            body = candidate;
            return true;
        }

        internal static bool TryPriceReference(AuctionQueryPriceReference reference,
            out byte[] body, out string reason)
        {
            body = null; reason = "";
            if (reference == null || reference.Levels.IsDefault || reference.Levels.Length > 6
                || reference.CapturedAtUnix < 0 || reference.PolicyVersion != AuctionQueryEngine.PolicyVersion
                || string.IsNullOrWhiteSpace(reference.CatalogPvfSha256)
                || reference.UnknownScalar.HasValue)
                return Fail("invalid-price-reference", out reason);
            var prices = new uint[6]; var quantities = new uint[6];
            long previous = 0;
            for (int i = 0; i < reference.Levels.Length; i++)
            {
                var level = reference.Levels[i];
                if (level == null || level.UnitPriceGold <= previous || level.UnitPriceGold > int.MaxValue || level.Quantity <= 0)
                    return Fail("invalid-price-level", out reason);
                previous = level.UnitPriceGold;
                prices[i] = (uint)level.UnitPriceGold; quantities[i] = (uint)level.Quantity;
            }
            long? expected = reference.Levels.IsEmpty ? null : reference.Levels[0].UnitPriceGold;
            if (reference.SuggestedPriceScalar != expected)
                return Fail("reference-suggestion-mismatch", out reason);
            // Explicit M4 private reserved policy; not an inferred native meaning.
            body = AuctionOrdinaryBodyBuilder.AveragePriceSuccess((uint)(expected ?? 0), 0, prices, quantities);
            return true;
        }

        private sealed record DisplayRow(uint Bid, uint Buyout, byte[] Tag, byte Hours,
            AuctionNativeItemWireSnapshot Item, uint Arg21);

        private static bool TryRow(AuctionQueryRow row, AuctionItemCodec codec, long nowUnix,
            HashSet<long> ids, out DisplayRow display, out string reason)
        {
            display = null; reason = "";
            if (row == null || row.OrderId <= 0 || !ids.Add(row.OrderId) || row.OrderVersion <= 0
                || row.TemplateId <= 0 || row.SellerCharacterId <= 0 || row.SellerAccountId <= 0
                || row.RemainingQuantity <= 0 || row.CreatedAtUnix < 0 || row.CreatedAtUnix > nowUnix
                || row.ExpiresAtUnix <= nowUnix)
                return Fail("invalid-display-row", out reason);
            if (!TryPrice(row.CurrentOrOpeningBidGold, out var bid) || !TryPrice(row.BuyoutGold, out var buyout)
                || (!row.CurrentOrOpeningBidGold.HasValue && !row.BuyoutGold.HasValue))
                return Fail("invalid-display-price", out reason);
            if (!AuctionSellerNameTag13.TryEncodeFrozenRoleName(row.SellerName, out var tag))
                return Fail("seller-tag-unavailable", out reason);
            long seconds = row.ExpiresAtUnix - nowUnix;
            long hours = seconds / 3600 + (seconds % 3600 == 0 ? 0 : 1);
            if (hours < 1 || hours > byte.MaxValue)
                return Fail("display-hours-unavailable", out reason);
            try
            {
                var envelope = codec.DeserializeStrict(row.EscrowEnvelopeJson);
                var core = codec.DecodeStrict(envelope);
                if (!string.Equals(envelope.ContentHashHex, row.EscrowContentHash, StringComparison.Ordinal)
                    || core.ItemId != row.TemplateId
                    || (InventoryStackRuleService.IsStackable(core) ? core.Count != row.RemainingQuantity : row.RemainingQuantity != 1))
                    return Fail("escrow-display-binding-mismatch", out reason);
                if (!AuctionNativeDisplayProjection.TryProject(envelope, codec, out var native, out reason)) return false;
                display = new(bid, buyout, tag, (byte)hours, native, core.A21Tail_Unknown97);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException
                || ex is JsonException || ex is IOException || ex is FormatException || ex is OverflowException)
            { return Fail("invalid-display-escrow", out reason); }
        }

        private static bool TryPrice(long? value, out uint bits)
        {
            bits = uint.MaxValue;
            if (!value.HasValue) return true;
            if (value.Value <= 0 || value.Value > int.MaxValue) return false;
            bits = (uint)value.Value;
            return true;
        }
        private static bool Fail(string code, out string reason) { reason = code; return false; }
    }
}
