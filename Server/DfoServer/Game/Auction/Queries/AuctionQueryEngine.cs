using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld.Auction;
using DfoServer.Network.Parsers.Auction;

namespace DfoServer.Game.Auction.Queries
{
    // Read policy is documented in Docs/拍卖行业务与运营接口.md.
    // Client evidence establishes field meanings and pagination, not the
    // original backend's stable order, snapshot TTL or reference-price formula.
    internal sealed class AuctionQueryEngine
    {
        internal const string PolicyVersion = "private-query-20261023-v1";
        internal const int SnapshotSeconds = 120, MaximumSnapshotRows = 5000, BatchSize = 60;
        private readonly AuctionTemplateCatalog catalog;
        private readonly AuctionItemCodec codec;
        private readonly Func<ushort, AuctionQueryCategoryRule> categoryResolver;

        internal AuctionQueryEngine(AuctionTemplateCatalog catalog, AuctionItemCodec codec,
            Func<ushort, AuctionQueryCategoryRule> categoryResolver = null)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
            this.categoryResolver = categoryResolver;
        }

        internal static AuctionQueryResult<AuctionQueryCriteria> Normalize(AuctionSearchByItemKeyRequest request)
        {
            if (request == null || request.ItemKeys.IsDefaultOrEmpty
                || request.ItemKeys.Any(k => k == 0 || k > int.MaxValue) || !ValidPosition(request.Position))
                return Fail<AuctionQueryCriteria>(AuctionQueryStatus.InvalidRequest, "InvalidItemSearch");
            var criteria = new AuctionQueryCriteria(AuctionQueryView.Search, true,
                request.ItemKeys.Select(k => (int)k).Distinct().OrderBy(k => k).ToImmutableArray(),
                NormalizeCategory(request.Wire8), request.Wire5, request.Wire6,
                request.Field226, request.Field227, 7, 0, 0, request.FilterA, request.FilterB, request.FilterC);
            return ValidateCriteria(criteria);
        }

        internal static AuctionQueryResult<AuctionQueryCriteria> Normalize(AuctionSearchWithoutItemKeyRequest request)
        {
            if (request == null || !ValidPosition(request.Position))
                return Fail<AuctionQueryCriteria>(AuctionQueryStatus.InvalidRequest, "InvalidCategorySearch");
            bool defaultLevel = request.Wire10 == 0 && request.Wire11 == 86;
            byte minimumLevel = defaultLevel ? (byte)0 : request.Wire10 == 0 && request.Wire11 > 0 ? (byte)1 : request.Wire10;
            byte maximumLevel = defaultLevel ? (byte)0 : request.Wire11;
            return ValidateCriteria(new(AuctionQueryView.Search, false, ImmutableArray<int>.Empty,
                NormalizeCategory(request.Wire5), request.Wire8, request.Wire9, request.Field233,
                request.Field234, request.Wire7, minimumLevel, maximumLevel,
                request.FilterA, request.FilterB, request.FilterC));
        }

        internal static AuctionQueryCriteria OwnedCriteria(AuctionQueryView view)
        {
            if (view is not (AuctionQueryView.OwnActiveListings or AuctionQueryView.CurrentHighestBids))
                throw new ArgumentOutOfRangeException(nameof(view));
            return new(view, false, ImmutableArray<int>.Empty, 40119, 0, 31, 0, 31, 7, 0, 0, 0, 0, 0);
        }

        internal AuctionQueryResult<AuctionQuerySnapshot> CreateSnapshot(IReadOnlyList<AuctionOrderSnapshot> orders,
            AuctionQueryPrincipal principal, AuctionQueryCriteria criteria, long nowUnix)
        {
            var selected = SelectCandidates(orders, principal, criteria, nowUnix);
            if (!selected.IsAvailable) return Fail<AuctionQuerySnapshot>(selected.Status, selected.Reason);
            try
            {
                return Available(new AuctionQuerySnapshot(principal, Fingerprint(criteria), catalog.SourcePvfSha256,
                    nowUnix, checked(nowUnix + SnapshotSeconds), selected.Value));
            }
            catch (OverflowException) { return Fail<AuctionQuerySnapshot>(AuctionQueryStatus.InvalidRequest, "SnapshotTimeOverflow"); }
        }

        internal AuctionQueryResult<AuctionQueryPage> Page(AuctionQuerySnapshot snapshot, AuctionQueryPrincipal principal,
            AuctionQueryCriteria criteria, uint position, long nowUnix)
        {
            var validation = ValidateCriteria(criteria);
            if (!ValidPrincipal(principal) || !validation.IsAvailable || !ValidPosition(position) || nowUnix < 0)
                return Fail<AuctionQueryPage>(validation.IsAvailable ? AuctionQueryStatus.InvalidRequest : validation.Status,
                    validation.IsAvailable ? "InvalidPageRequest" : validation.Reason);
            if (snapshot == null) return Fail<AuctionQueryPage>(AuctionQueryStatus.SnapshotExpired, "SnapshotRequired");
            if (snapshot.Principal != principal || snapshot.CriteriaFingerprint != Fingerprint(criteria)
                || snapshot.CatalogPvfSha256 != catalog.SourcePvfSha256)
                return Fail<AuctionQueryPage>(AuctionQueryStatus.InvalidRequest, "SnapshotBindingMismatch");
            if (nowUnix < snapshot.CapturedAtUnix)
                return Fail<AuctionQueryPage>(AuctionQueryStatus.InvalidRequest, "PageTimeBeforeSnapshot");
            if (nowUnix >= snapshot.ValidUntilUnix)
                return Fail<AuctionQueryPage>(AuctionQueryStatus.SnapshotExpired, "SnapshotExpired");
            return Available(new AuctionQueryPage(snapshot.SnapshotId, snapshot.CapturedAtUnix,
                snapshot.Rows.Length, snapshot.Rows.Skip((int)position).Take(BatchSize).ToImmutableArray()));
        }

        internal AuctionQueryResult<ImmutableArray<AuctionQueryRow>> SelectCandidates(IReadOnlyList<AuctionOrderSnapshot> orders,
            AuctionQueryPrincipal principal, AuctionQueryCriteria criteria, long nowUnix)
        {
            var validation = ValidateCriteria(criteria);
            if (!validation.IsAvailable) return Fail<ImmutableArray<AuctionQueryRow>>(validation.Status, validation.Reason);
            if (!ValidPrincipal(principal) || orders == null || nowUnix < 0)
                return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.InvalidRequest, "InvalidQueryOwnerOrSource");
            var rows = ImmutableArray.CreateBuilder<AuctionQueryRow>();
            var identities = new HashSet<long>();
            try
            {
                // Resolve before examining rows, including an empty market.
                // An unknown category cannot masquerade as a successful empty result.
                var categoryRule = criteria.NativeCategory == 40119 ? null : categoryResolver?.Invoke(criteria.NativeCategory);
                if (criteria.NativeCategory != 40119 && (categoryRule == null || categoryRule.Matches == null
                    || categoryRule.NativeCategory != criteria.NativeCategory || categoryRule.SourcePvfSha256 != catalog.SourcePvfSha256
                    || string.IsNullOrWhiteSpace(categoryRule.EvidenceReference)))
                    return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "CategoryPredicateUnavailable");
                foreach (var order in orders)
                {
                    if (order == null || order.OrderId <= 0 || !Enum.IsDefined(order.State) || !identities.Add(order.OrderId))
                        return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "CorruptOrDuplicateCandidate");
                    if (order.State != AuctionOrderState.Active || order.PendingOperationId.HasValue || order.ExpiresAtUnix <= nowUnix)
                        continue;
                    if (!ValidParty(order.Seller) || order.Version <= 0 || order.InitialQuantity <= 0
                        || order.RemainingQuantity <= 0 || order.RemainingQuantity > order.InitialQuantity
                        || order.FundedDepositGold <= 0 || order.CreatedAtUnix < 0 || order.CreatedAtUnix > nowUnix
                        || order.FundedBidGold < 0 || (order.HighestBidder != null) != (order.FundedBidGold > 0)
                        || (order.HighestBidder != null && !ValidParty(order.HighestBidder))
                        || !ValidPrice(order.OpeningBidGold) || !ValidPrice(order.BuyoutGold)
                        || (!order.OpeningBidGold.HasValue && !order.BuyoutGold.HasValue)
                        || order.FundedBidGold > int.MaxValue)
                        return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "CorruptActiveOrder");
                    if (criteria.View == AuctionQueryView.OwnActiveListings
                        && (order.Seller.AccountId != principal.AccountId || order.Seller.CharacterId != principal.CharacterId)) continue;
                    if (criteria.View == AuctionQueryView.CurrentHighestBids
                        && (order.HighestBidder?.AccountId != principal.AccountId || order.HighestBidder?.CharacterId != principal.CharacterId)) continue;
                    var envelope = codec.DeserializeStrict(order.EscrowEnvelopeJson);
                    var core = codec.DecodeStrict(envelope);
                    if (InventoryStackRuleService.IsStackable(core) ? core.Count != order.RemainingQuantity : order.RemainingQuantity != 1)
                        return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "EscrowQuantityMismatch");
                    if (criteria.ByItemKeys && !criteria.ItemKeys.Contains(core.ItemId)) continue;
                    if (!catalog.TryGet(core.ItemId, out var template) || template.SourcePvfSha256 != catalog.SourcePvfSha256)
                        return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "ExactCatalogTemplateUnavailable");
                    var category = criteria.NativeCategory == 40119 ? AuctionQueryMatch.Match
                        : categoryRule.Matches(template);
                    if (category == AuctionQueryMatch.Unknown || !Enum.IsDefined(category))
                        return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "CategoryPredicateUnavailable");
                    if (category == AuctionQueryMatch.NoMatch) continue;
                    if (core.Upgrade < criteria.ReinforcementMin || core.Upgrade > criteria.ReinforcementMax
                        || (core.GenuineUpgrade & 31) < criteria.ForgeMin || (core.GenuineUpgrade & 31) > criteria.ForgeMax) continue;
                    if (criteria.Rarity != 7)
                    {
                        if (template.Rarity < 0) return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "TemplateRarityUnavailable");
                        if (template.Rarity != criteria.Rarity) continue;
                    }
                    if (criteria.LevelMin != 0 || criteria.LevelMax != 0)
                    {
                        if (template.MinimumLevel < 0) return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "TemplateLevelUnavailable");
                        if (template.MinimumLevel < criteria.LevelMin || template.MinimumLevel > criteria.LevelMax) continue;
                    }
                    long? unit = null;
                    if (order.BuyoutGold.HasValue)
                    {
                        if (order.BuyoutGold.Value % order.RemainingQuantity == 0)
                            unit = order.BuyoutGold.Value / order.RemainingQuantity;
                        if (order.UnitPriceGold.HasValue && (order.UnitPriceGold <= 0
                            || checked(order.UnitPriceGold.Value * order.RemainingQuantity) != order.BuyoutGold.Value))
                            return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "FrozenUnitPriceMismatch");
                    }
                    else if (order.UnitPriceGold.HasValue)
                        return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "UnitPriceWithoutBuyout");
                    rows.Add(new(order.OrderId, order.Version, core.ItemId, order.Seller.CharacterId, order.Seller.AccountId,
                        order.Seller.FrozenName, order.HighestBidder?.CharacterId, order.HighestBidder?.AccountId,
                        order.RemainingQuantity, order.FundedBidGold > 0 ? order.FundedBidGold : order.OpeningBidGold,
                        order.BuyoutGold, unit, template.DisplayName, template.MinimumLevel, template.Rarity,
                        core.Upgrade, (byte)(core.GenuineUpgrade & 31), order.CreatedAtUnix, order.ExpiresAtUnix,
                        order.EscrowEnvelopeJson, envelope.ContentHashHex));
                    if (rows.Count > MaximumSnapshotRows)
                        return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "SnapshotRowBudgetExceeded");
                }
                return Available(rows.OrderBy(row => row.OrderId).ToImmutableArray());
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException
                || exception is JsonException || exception is OverflowException)
            {
                return Fail<ImmutableArray<AuctionQueryRow>>(AuctionQueryStatus.Unavailable, "InvalidCandidateProjection:" + exception.GetType().Name);
            }
        }

        internal AuctionQueryResult<AuctionQueryPriceReference> PriceReference(IReadOnlyList<AuctionOrderSnapshot> orders,
            AuctionQueryPrincipal principal, AuctionAveragePriceRequest request, long nowUnix)
        {
            if (request == null || request.ItemKey == 0 || request.ItemKey > int.MaxValue)
                return Fail<AuctionQueryPriceReference>(AuctionQueryStatus.InvalidRequest, "InvalidReferenceTemplate");
            if (request.AttributeA != 0 || request.AttributeB != 0 || request.AttributeC != 0)
                return Fail<AuctionQueryPriceReference>(AuctionQueryStatus.Unavailable, "ReferenceAttributesUnavailable");
            if (!catalog.TryGet((int)request.ItemKey, out _))
                return Fail<AuctionQueryPriceReference>(AuctionQueryStatus.Unavailable, "ReferenceTemplateUnavailable");
            var criteria = new AuctionQueryCriteria(AuctionQueryView.Search, true,
                ImmutableArray.Create((int)request.ItemKey), 40119, 0, 31, 0, 31, 7, 0, 0, 0, 0, 0);
            var selected = SelectCandidates(orders, principal, criteria, nowUnix);
            if (!selected.IsAvailable) return Fail<AuctionQueryPriceReference>(selected.Status, selected.Reason);
            try
            {
                var levels = new SortedDictionary<long, int>();
                foreach (var row in selected.Value)
                {
                    if (!row.BuyoutGold.HasValue) continue;
                    if (!row.ExactUnitPriceGold.HasValue)
                        return Fail<AuctionQueryPriceReference>(AuctionQueryStatus.Unavailable, "NondivisibleBundleReferenceUnavailable");
                    var price = row.ExactUnitPriceGold.Value;
                    levels.TryGetValue(price, out var quantity);
                    levels[price] = checked(quantity + row.RemainingQuantity);
                }
                var lowest = levels.Take(6).Select(level => new AuctionQueryPriceLevel(level.Key, level.Value)).ToImmutableArray();
                // UnknownScalar remains unknown; this is not a complete native ACK.
                return Available(new AuctionQueryPriceReference(nowUnix, catalog.SourcePvfSha256, PolicyVersion,
                    lowest.IsEmpty ? null : lowest[0].UnitPriceGold, null, lowest));
            }
            catch (OverflowException) { return Fail<AuctionQueryPriceReference>(AuctionQueryStatus.Unavailable, "ReferenceQuantityOverflow"); }
        }

        private static AuctionQueryResult<AuctionQueryCriteria> ValidateCriteria(AuctionQueryCriteria criteria)
        {
            if (criteria == null || !Enum.IsDefined(criteria.View) || criteria.ItemKeys.IsDefault
                || criteria.ItemKeys.Any(k => k <= 0) || (criteria.ByItemKeys && criteria.ItemKeys.IsEmpty)
                || (!criteria.ByItemKeys && !criteria.ItemKeys.IsEmpty) || criteria.NativeCategory == 0
                || criteria.ReinforcementMin > criteria.ReinforcementMax || criteria.ReinforcementMax > 31
                // Native comparison uses low5. This is its representable range,
                // not a claim that the unproved resource UI cap equals 31.
                || criteria.ForgeMin > criteria.ForgeMax || criteria.ForgeMax > 31
                || ((criteria.LevelMin != 0 || criteria.LevelMax != 0)
                    && (criteria.LevelMin == 0 || criteria.LevelMax < criteria.LevelMin)))
                return Fail<AuctionQueryCriteria>(AuctionQueryStatus.InvalidRequest, "InvalidCriteriaRange");
            if (criteria.Rarity is not (7 or 0 or 1 or 2 or 3 or 4 or 6))
                return Fail<AuctionQueryCriteria>(AuctionQueryStatus.Unavailable, "RarityPredicateUnavailable");
            if (criteria.RandomA != 0 || criteria.RandomB != 0 || criteria.RandomC != 0)
                return Fail<AuctionQueryCriteria>(AuctionQueryStatus.Unavailable, "RandomPredicateUnavailable");
            return Available(criteria);
        }

        private static ushort NormalizeCategory(ushort category) => category >= 12005 && category <= 12016 ? (ushort)12004 : category;
        private static bool ValidPosition(uint position) => position <= int.MaxValue && position % BatchSize == 0;
        private static bool ValidPrincipal(AuctionQueryPrincipal principal) => principal != null
            && principal.SessionId != Guid.Empty && principal.Generation > 0 && principal.AccountId > 0 && principal.CharacterId > 0;
        private static bool ValidParty(AuctionParty party) => party != null && party.AccountId > 0 && party.CharacterId > 0;
        private static bool ValidPrice(long? price) => !price.HasValue || price.Value > 0 && price.Value <= int.MaxValue;
        private static string Fingerprint(AuctionQueryCriteria criteria)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(criteria))));
        private static AuctionQueryResult<T> Available<T>(T value) => new(AuctionQueryStatus.Available, "", value);
        private static AuctionQueryResult<T> Fail<T>(AuctionQueryStatus status, string reason) => new(status, reason, default);
    }
}
