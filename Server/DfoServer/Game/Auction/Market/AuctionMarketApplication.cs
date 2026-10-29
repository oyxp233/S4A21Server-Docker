using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction.Market
{
    // Application owner for one private/public/private operation, not a second
    // coordinator. Mail appearance and native transfer capability are injected.
    internal sealed class AuctionMarketApplication
    {
        private readonly AuctionOrderStore store;
        private readonly AuctionItemCodec codec;
        private readonly AuctionMarketPlanner planner;
        private readonly Lazy<AuctionListingEligibility> listingEligibility;
        private readonly bool allowFixtureListingProofs;
        private readonly AuctionMarketQuoteProvider quoteProvider;
        private readonly AuctionPublicAssetCommit rail;
        private readonly Func<AuctionDeliveryLeg, Guid, MailboxSendRequest> mailFactory;
        private readonly object marketGate;
        private readonly Func<long> now;
        private readonly Func<AuctionItemEnvelope, bool> canRepresentListing;
        private readonly Func<AuctionMarketRequest,bool> entryPolicy;

        internal AuctionMarketApplication(AuctionOrderStore store, AuctionItemCodec codec,
            AuctionMarketPlanner planner, AuctionListingEligibility listingEligibility, AuctionMarketQuoteProvider quoteProvider,
            AuctionPublicAssetCommit rail, Func<AuctionDeliveryLeg, Guid, MailboxSendRequest> mailFactory,
            object sharedMarketGate, Func<long> nowUnix, bool allowFixtureListingProofs = false,
            Func<AuctionListingEligibility> deferredListingEligibility = null,
            Func<AuctionItemEnvelope, bool> canRepresentListing = null,
            Func<AuctionMarketRequest,bool> entryPolicy = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
            this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
            this.listingEligibility = listingEligibility != null
                ? new Lazy<AuctionListingEligibility>(() => listingEligibility)
                : new Lazy<AuctionListingEligibility>(deferredListingEligibility
                    ?? throw new ArgumentNullException(nameof(listingEligibility)));
            this.allowFixtureListingProofs = allowFixtureListingProofs;
            this.quoteProvider = quoteProvider ?? throw new ArgumentNullException(nameof(quoteProvider));
            this.rail = rail ?? throw new ArgumentNullException(nameof(rail));
            this.mailFactory = mailFactory ?? throw new ArgumentNullException(nameof(mailFactory));
            marketGate = sharedMarketGate ?? throw new ArgumentNullException(nameof(sharedMarketGate));
            now = nowUnix ?? throw new ArgumentNullException(nameof(nowUnix));
            this.canRepresentListing = canRepresentListing;
            this.entryPolicy=entryPolicy;
        }

        internal AuctionMarketResult Execute(AuctionMarketRequest request, InventoryLease lease,
            InventoryListType sourceList = InventoryListType.Main, short sourceSlot = -1)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.OperationId == Guid.Empty || request.Actor == null)
                return new(AuctionMarketStatus.Rejected, "InvalidRequestIdentity", null, null);
            if (request.Action == AuctionOperationAction.List && sourceSlot >= 0)
            {
                if (request.SourceSlot >= 0 && (request.SourceSlot != sourceSlot || request.SourceList != sourceList))
                    return new(AuctionMarketStatus.Rejected, "SourceArgumentConflict", null, null);
                request = request with { SourceList = sourceList, SourceSlot = sourceSlot };
            }
            lock (marketGate)
            {
                try
                {
                    var prior = store.FindOperation(request.OperationId);
                    if (prior != null)
                    {
                        var frozen = FrozenTransition(prior.Intent);
                        var replay = NormalizeListingReplay(request, frozen.After);
                        if (frozen.Quote.Binding != replay.Binding())
                            return new(AuctionMarketStatus.Rejected, "OperationRequestConflict", null, prior);
                        return ResumeEntry(prior); // Never execute old Prepared.
                    }
                    if(entryPolicy!=null&&!entryPolicy(request))return new(AuctionMarketStatus.Unavailable,"EntryPolicy",null,null);
                    if (lease?.Inventory?.Database == null || request.Actor == null || lease.CharacterId != request.Actor.CharacterId
                        || lease.AccountId != request.Actor.AccountId || !InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                        return new(AuctionMarketStatus.Unavailable, "CurrentOwnedLeaseRequired", null, null);
                    lock (lease.SyncRoot)
                    {
                        if (!InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                            return new(AuctionMarketStatus.Rejected, "StaleInventoryLease", null, null);
                        // Do this BEFORE private Prepare. A failed public rollback
                        // must not discard unrelated dirty gameplay state.
                        if (!InventoryPersistenceService.SaveDirty(lease)) return new(AuctionMarketStatus.Pending, "BaselineSaveFailed", null, null);
                        if (request.Action == AuctionOperationAction.List && request.OrderId == 0)
                            request = request with { OrderId = store.AllocateOrderId() };
                        var before = request.Action == AuctionOperationAction.List ? null : store.FindOrder(request.OrderId);
                        var timestamp = now();
                        if (request.Action == AuctionOperationAction.List && request.DurationHours != 0)
                        {
                            if (request.DurationHours is not (24 or 48)) return new(AuctionMarketStatus.Rejected, "InvalidListingDuration", null, null);
                            var deadline = checked(timestamp + request.DurationHours * 3600L);
                            if (request.RequestedExpiryUnix != 0 && request.RequestedExpiryUnix != deadline)
                                return new(AuctionMarketStatus.Rejected, "ListingDurationDeadlineConflict", null, null);
                            request = request with { RequestedExpiryUnix = deadline };
                        }
                        AuctionListingEligibilityResult eligibility = null;
                        AuctionEntitlementSnapshot premium = null;
                        if (request.Action == AuctionOperationAction.List)
                        {
                            premium = lease.Inventory.Database.Read(c => AuctionEntitlementReader.Read(c, null, lease.AccountId, timestamp));
                            eligibility = listingEligibility.Value.Evaluate(lease, request.SourceList, request.SourceSlot,
                                request.Quantity, timestamp, request.RequestedExpiryUnix, premium);
                            if (!AuthorizedListing(eligibility))
                                return new(eligibility.Status == AuctionListingEligibilityStatus.Rejected
                                    ? AuctionMarketStatus.Rejected : AuctionMarketStatus.Unavailable,
                                    string.IsNullOrEmpty(eligibility.Reason) ? "ListingProofAuthorityRequired" : eligibility.Reason, null, null);
                        }
                        AuctionVerifiedQuote quote;
                        try { quote = quoteProvider(request, before, timestamp); }
                        catch (AuctionMarketUnavailableException ex) { return new(AuctionMarketStatus.Unavailable, ex.Message, before, null); }
                        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is OverflowException)
                        { return new(AuctionMarketStatus.Rejected, ex.Message, before, null); }
                        if (eligibility != null && !ListingTermsMatch(request, quote, timestamp, premium))
                            return new(AuctionMarketStatus.Rejected, "ListingQuoteEntitlementMismatch", null, null);
                        var source = eligibility?.Source;
                        if (source != null && quote.UnitPriceGold.HasValue
                            && (!InventoryStackRuleService.IsStackable(codec.DecodeStrict(source))
                                || (request.UnitPriceGold.HasValue && request.UnitPriceGold != quote.UnitPriceGold)))
                            return new(AuctionMarketStatus.Rejected, "InvalidFixedUnitPriceSource", null, null);
                        var planning = planner.Plan(request, before, source, quote, timestamp);
                        if (planning.Status != AuctionMarketStatus.Planned) return new(planning.Status, planning.Reason, before, null);
                        var transition = planning.Plan.Transition;
                        if (request.Action == AuctionOperationAction.List
                            && (canRepresentListing == null ? !allowFixtureListingProofs
                                : !canRepresentListing(codec.DeserializeStrict(transition.After.EscrowEnvelopeJson))))
                            return new(AuctionMarketStatus.Unavailable, "NativeListingDisplayUnavailable", null, null);
                        var messages = transition.Legs.Select(leg => mailFactory(leg, request.OperationId)).ToArray();
                        var debits = request.Action == AuctionOperationAction.List
                            ? new[] { new AuctionItemDebit(request.SourceList, request.SourceSlot, request.Quantity, source) }
                            : Array.Empty<AuctionItemDebit>();
                        var publicPlan = AuctionPublicAssetPlan.Create(codec, planning.Plan.GoldDebit, debits, messages);
                        var intentJson = JsonSerializer.Serialize(new { publicAssetPlan = publicPlan.Serialize(), orderTransition = transition.Serialize(),
                            listingEligibility = eligibility == null ? null : FreezeEligibility(eligibility, premium) });
                        var intent = new AuctionOperationIntent(request.OperationId, "order:" + request.OrderId,
                            request.Action, request.Actor.CharacterId, request.Actor.AccountId, request.ExpectedVersion, timestamp, intentJson);
                        var prepared = store.Prepare(intent, transition);
                        if (!prepared.Created) return ResumeEntry(prepared.Operation);
                        // The rail invokes this on its receipt-first public IMMEDIATE
                        // transaction, before any debit/mail. Never open another DB
                        // connection or resample quote/mail inside this callback.
                        bool Preconditions(SqliteConnection c, SqliteTransaction t)
                        {
                            if (eligibility == null) return true;
                            var commitTime = now();
                            if (commitTime < timestamp) return false;
                            var currentPremium = AuctionEntitlementReader.Read(c, t, lease.AccountId, commitTime);
                            if (currentPremium.IsActive != premium.IsActive) return false;
                            var current = listingEligibility.Value.Evaluate(lease, request.SourceList, request.SourceSlot,
                                request.Quantity, commitTime, request.RequestedExpiryUnix, currentPremium);
                            return AuthorizedListing(current) && current.SessionId == eligibility.SessionId
                                && current.CharacterId == eligibility.CharacterId && current.AccountId == eligibility.AccountId
                                && current.List == eligibility.List && current.Slot == eligibility.Slot && current.Quantity == eligibility.Quantity
                                && current.SourceContentHash == eligibility.SourceContentHash
                                && current.SourcePvfSha256 == eligibility.SourcePvfSha256 && current.NativeProof == eligibility.NativeProof;
                        }
                        var outcome = rail.Execute(intent, publicPlan, lease, Preconditions);
                        return FinishPublic(intent, transition, prepared.Operation, outcome);
                    }
                }
                catch (AuctionItemCodecException ex) { return new(AuctionMarketStatus.Unavailable, ex.Error.ToString(), null, null); }
                catch (AuctionReceiptConflictException ex) { return new(AuctionMarketStatus.Quarantined, ex.Message, null, null); }
                catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is InvalidOperationException)
                { return new(AuctionMarketStatus.Pending, ex.Message, null, null); }
            }
        }

        // Read-only planning uses the same policy/eligibility/planner as Execute.
        // A listing ID of zero is represented provisionally; no sequence is consumed.
        internal AuctionMarketPlanningResult Preview(AuctionMarketRequest request, InventoryLease lease)
        {
            lock(marketGate) {
                if(entryPolicy!=null&&!entryPolicy(request))return new(AuctionMarketStatus.Unavailable,"EntryPolicy",null);
                if(lease==null||!InventoryContext.IsCurrentLease(lease,lease.SessionId,request.Actor.CharacterId))
                    return new(AuctionMarketStatus.Unavailable,"CurrentOwnedLeaseRequired",null);
                lock(lease.SyncRoot) {
                    var stamp=now();
                    if(request.Action==AuctionOperationAction.List) {
                        request=request with {OrderId=request.OrderId==0?long.MaxValue:request.OrderId};
                        if(request.DurationHours!=0) {
                            if(request.DurationHours is not(24 or 48))return new(AuctionMarketStatus.Rejected,"InvalidListingDuration",null);
                            request=request with {RequestedExpiryUnix=checked(stamp+request.DurationHours*3600L)};
                        }
                    }
                    var before=request.Action==AuctionOperationAction.List?null:store.FindOrder(request.OrderId);
                    AuctionItemEnvelope source=null;AuctionEntitlementSnapshot premium=null;
                    if(request.Action==AuctionOperationAction.List) {
                        premium=lease.Inventory.Database.Read(c=>AuctionEntitlementReader.Read(c,null,lease.AccountId,stamp));
                        var eligibility=listingEligibility.Value.Evaluate(lease,request.SourceList,request.SourceSlot,request.Quantity,stamp,request.RequestedExpiryUnix,premium);
                        if(!AuthorizedListing(eligibility))return new(AuctionMarketStatus.Unavailable,eligibility.Reason,null);
                        source=eligibility.Source;
                    }
                    var quote=quoteProvider(request,before,stamp);
                    if(source!=null&&!ListingTermsMatch(request,quote,stamp,premium))return new(AuctionMarketStatus.Rejected,"ListingQuoteEntitlementMismatch",null);
                    var result=planner.Plan(request,before,source,quote,stamp);
                    if(source!=null&&result.Plan!=null&&(canRepresentListing==null?!allowFixtureListingProofs:!canRepresentListing(codec.DeserializeStrict(result.Plan.Transition.After.EscrowEnvelopeJson))))
                        return new(AuctionMarketStatus.Unavailable,"NativeListingDisplayUnavailable",null);
                    return result;
                }
            }
        }

        // Frozen funded expiry has no online debit or inventory owner. Never
        // manufacture an offline lease merely to reuse the online method.
        internal AuctionMarketResult ExecuteOfflineExpiry(AuctionMarketRequest request)
        {
            if (request == null || request.OperationId == Guid.Empty || request.Action != AuctionOperationAction.Expire || request.Actor == null)
                return new(AuctionMarketStatus.Rejected, "FundedExpiryRequestRequired", null, null);
            lock (marketGate)
            {
                try
                {
                    var prior = store.FindOperation(request.OperationId);
                    if (prior != null)
                    {
                        if (FrozenTransition(prior.Intent).Quote.Binding != request.Binding())
                            return new(AuctionMarketStatus.Rejected, "OperationRequestConflict", null, prior);
                        return ResumeEntry(prior);
                    }
                    var before = store.FindOrder(request.OrderId);
                    if (before == null || request.Actor.CharacterId != before.Seller.CharacterId || request.Actor.AccountId != before.Seller.AccountId)
                        return new(AuctionMarketStatus.Rejected, "ExpirySellerIdentityMismatch", before, null);
                    long timestamp = now();
                    var quote = quoteProvider(request, before, timestamp);
                    var planning = planner.Plan(request, before, null, quote, timestamp);
                    if (planning.Status != AuctionMarketStatus.Planned) return new(planning.Status, planning.Reason, before, null);
                    if (planning.Plan.GoldDebit != 0) return new(AuctionMarketStatus.Rejected, "ExpiryCannotDebit", before, null);
                    var transition = planning.Plan.Transition;
                    var publicPlan = AuctionPublicAssetPlan.Create(codec, 0, Array.Empty<AuctionItemDebit>(),
                        transition.Legs.Select(leg => mailFactory(leg, request.OperationId)).ToArray());
                    var intent = new AuctionOperationIntent(request.OperationId, "order:" + request.OrderId, request.Action,
                        request.Actor.CharacterId, request.Actor.AccountId, request.ExpectedVersion, timestamp,
                        JsonSerializer.Serialize(new { publicAssetPlan = publicPlan.Serialize(), orderTransition = transition.Serialize() }));
                    var prepared = store.Prepare(intent, transition);
                    if (!prepared.Created) return ResumeEntry(prepared.Operation);
                    return FinishPublic(intent, transition, prepared.Operation, rail.ExecuteFundedExpiry(intent, publicPlan));
                }
                catch (AuctionMarketUnavailableException ex) { return new(AuctionMarketStatus.Unavailable, ex.Message, null, null); }
                catch (AuctionItemCodecException ex) { return new(AuctionMarketStatus.Unavailable, ex.Error.ToString(), null, null); }
                catch (AuctionReceiptConflictException ex) { return new(AuctionMarketStatus.Quarantined, ex.Message, null, null); }
                catch (ArgumentException ex) { return new(AuctionMarketStatus.Rejected, ex.Message, null, null); }
                catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is InvalidOperationException)
                { return new(AuctionMarketStatus.Pending, ex.Message, null, null); }
            }
        }

        internal AuctionMarketResult Resume(Guid operationId)
        {
            lock (marketGate)
            {
                try
                {
                    var entry = store.FindOperation(operationId);
                    return entry == null ? new(AuctionMarketStatus.Rejected, "UnknownOperation", null, null) : ResumeEntry(entry);
                }
                catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is InvalidOperationException)
                { return new(AuctionMarketStatus.Pending, ex.Message, null, null); }
            }
        }

        private AuctionMarketResult ResumeEntry(AuctionJournalEntry entry)
        {
            var transition = FrozenTransition(entry.Intent);
            if (entry.State == AuctionOperationState.Committed)
            { rail.ReleaseTerminalAttempt(entry); return new(AuctionMarketStatus.Committed, "", transition.After, entry); }
            if (entry.State == AuctionOperationState.Aborted)
            { rail.ReleaseTerminalAttempt(entry); return new(AuctionMarketStatus.Aborted, entry.ResolutionReason, transition.Before, entry); }
            if (entry.State == AuctionOperationState.Quarantined) return new(AuctionMarketStatus.Quarantined, entry.ResolutionReason, transition.Before, entry);
            var outcome = rail.ReadOutcome(entry.Intent);
            if (outcome.State == AuctionAssetCommitState.Committed)
            {
                return FinishPublic(entry.Intent, transition, entry, outcome);
            }
            if (outcome.State == AuctionAssetCommitState.Conflict)
            {
                store.Quarantine(entry.Intent, outcome.Reason);
                return new(AuctionMarketStatus.Quarantined, outcome.Reason, transition.Before, store.FindOperation(entry.Intent.OperationId));
            }
            return new(AuctionMarketStatus.Pending, outcome.Reason, transition.Before, entry);
        }

        private AuctionMarketResult FinishPublic(AuctionOperationIntent intent, AuctionOrderTransition transition,
            AuctionJournalEntry prepared, AuctionAssetCommitResult outcome)
        {
            AuctionOrderSnapshot order = transition.Before;
            AuctionMarketStatus status;
            switch (outcome.State)
            {
                case AuctionAssetCommitState.Committed:
                    order = store.Complete(intent, outcome.Receipt); status = AuctionMarketStatus.Committed; break;
                case AuctionAssetCommitState.Rejected:
                    store.AbortVerifiedUncommitted(intent, outcome.Reason); status = AuctionMarketStatus.Aborted; break;
                case AuctionAssetCommitState.Conflict:
                    store.Quarantine(intent, outcome.Reason); status = AuctionMarketStatus.Quarantined; break;
                default: return new(AuctionMarketStatus.Pending, outcome.Reason, transition.Before, prepared);
            }
            var terminal = store.FindOperation(intent.OperationId);
            if (terminal.State is AuctionOperationState.Committed or AuctionOperationState.Aborted) rail.ReleaseTerminalAttempt(terminal);
            return new(status, outcome.Reason, order, terminal);
        }

        private static AuctionMarketRequest NormalizeListingReplay(AuctionMarketRequest request, AuctionOrderSnapshot after)
        {
            if (request.Action != AuctionOperationAction.List) return request;
            if (request.OrderId == 0) request = request with { OrderId = after.OrderId };
            if (request.DurationHours != 0 && request.RequestedExpiryUnix == 0)
                request = request with { RequestedExpiryUnix = after.ExpiresAtUnix };
            return request;
        }

        private bool AuthorizedListing(AuctionListingEligibilityResult result)
        {
            if (result?.Status != AuctionListingEligibilityStatus.Allowed || result.Source == null
                || result.NativeProof == null
                || !(result.NativeProof.Kind == AuctionNativeProofKind.VerifiedA21
                    || (allowFixtureListingProofs && result.NativeProof.Kind == AuctionNativeProofKind.Fixture))) return false;
            var core = codec.DecodeStrict(result.Source);
            return result.Capability switch {
                AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount
                    => (result.List is InventoryListType.Main or InventoryListType.Pet or InventoryListType.GuildMedal)
                        && AuctionListingEligibility.IsBagSlot(result.List, result.Slot)
                        && AuctionListingEligibility.MatchesBagKind(result.List, core) && core.ItemKind != ItemCore.KindAvatar,
                AuctionListingTransferCapability.PermanentOrdinaryAvatarUidRebased
                    => result.List == InventoryListType.Avatar && result.Quantity == 1 && core.ItemKind == ItemCore.KindAvatar,
                _ => false
            };
        }

        private static bool ListingTermsMatch(AuctionMarketRequest request, AuctionVerifiedQuote quote,
            long timestamp, AuctionEntitlementSnapshot premium)
        {
            if (quote == null) return false;
            try
            {
                var duration = checked(request.RequestedExpiryUnix - timestamp);
                if (duration % 3600 != 0) return false;
                // Quota is reserved atomically by Store.Prepare. This independent
                // policy quote verifies the public rights underlying locked terms.
                var expected = AuctionEconomyPolicy.QuoteListing(new(request.Actor.AccountId, request.Actor.CharacterId,
                    0, timestamp, premium.ExpiresAtUnix, checked((int)(duration / 3600)), request.Quantity,
                    request.OpeningBidGold, request.BuyoutGold, quote.UnitPriceGold.HasValue));
                return quote.PolicyVersion == expected.PolicyVersion && quote.FeeRatePercent == expected.LockedFeePercent
                    && quote.MaxActiveListings == expected.MaximumAccountListings && quote.DepositDebitGold == expected.DepositDebitGold
                    && quote.UnitPriceGold == expected.FixedUnitPriceGold && request.RequestedExpiryUnix == expected.ExpiresAtUnix;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is OverflowException)
            { return false; }
        }

        private static string FreezeEligibility(AuctionListingEligibilityResult result, AuctionEntitlementSnapshot premium)
            => JsonSerializer.Serialize(new { formatVersion = 1, result.SessionId, result.CharacterId, result.AccountId,
                result.List, result.Slot, result.Quantity, result.ObservedAtUnix, result.SourcePvfSha256, result.SourceContentHash,
                result.NativeProof, premiumExpiresAtUnix = premium.ExpiresAtUnix });

        private static AuctionOrderTransition FrozenTransition(AuctionOperationIntent intent)
        {
            using var document = JsonDocument.Parse(intent.IntentJson);
            if (document.RootElement.EnumerateObject().Count(p => p.Name == "orderTransition") != 1)
                throw new AuctionReceiptConflictException("Missing or duplicate frozen transition.");
            return AuctionOrderTransition.DeserializeStrict(document.RootElement.GetProperty("orderTransition").GetString());
        }
    }
}
