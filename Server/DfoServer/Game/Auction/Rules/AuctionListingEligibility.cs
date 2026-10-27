using System;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Auction;

namespace DfoServer.Game.Auction.Rules
{
    internal enum AuctionListingEligibilityStatus { Allowed, Rejected, Unavailable }
    internal enum AuctionNativeProofKind { Unknown, VerifiedA21, Fixture }
    // DRAFT: serialized value2 requires main's recovery/rollback contract first.
    internal enum AuctionListingTransferCapability {
        None, PermanentOrdinaryMainOnlyCount, PermanentOrdinaryAvatarUidRebased
    }
    internal sealed record AuctionNativeListingContext(int AccountId, int CharacterId,
        AuctionTemplateProjection Template, AuctionItemEnvelope Source, string SourceContentHash,
        int Quantity, long ObservedAtUnix, long RequestedExpiryUnix, bool HasActiveAuctionPremium);
    internal sealed record AuctionNativeListingProof(AuctionNativeProofKind Kind,
        AuctionListingEligibilityStatus Status, int TemplateId, string SourceContentHash,
        string SourcePvfSha256, AuctionListingTransferCapability Capability,
        bool RequiresPremium, string EvidenceReference, string Reason);
    internal sealed record AuctionListingEligibilityResult(AuctionListingEligibilityStatus Status,
        string Reason, Guid SessionId, int CharacterId, int AccountId,
        InventoryListType List, short Slot, int Quantity, long ObservedAtUnix,
        string SourcePvfSha256, AuctionItemEnvelope Source, AuctionNativeListingProof NativeProof)
    {
        internal string SourceContentHash => Source?.ContentHashHex;
        internal AuctionListingTransferCapability Capability => Status == AuctionListingEligibilityStatus.Allowed
            ? NativeProof.Capability : AuctionListingTransferCapability.None;
    }

    // Deliberately no default native resolver. A metadata catalog and a lossless
    // codec describe an item; neither grants native auction eligibility.
    internal sealed class AuctionListingEligibility
    {
        private readonly AuctionTemplateCatalog catalog;
        private readonly AuctionItemCodec codec;
        private readonly Func<AuctionNativeListingContext, AuctionNativeListingProof> nativeResolver;
        private readonly bool allowFixtureProofs;

        internal AuctionListingEligibility(AuctionTemplateCatalog catalog, AuctionItemCodec codec,
            Func<AuctionNativeListingContext, AuctionNativeListingProof> nativeResolver,
            bool allowFixtureProofs = false)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
            this.nativeResolver = nativeResolver ?? throw new ArgumentNullException(nameof(nativeResolver));
            this.allowFixtureProofs = allowFixtureProofs;
        }

        internal AuctionListingEligibilityResult Evaluate(InventoryLease lease, InventoryListType list,
            short slot, int quantity, long nowUnix, long requestedExpiryUnix,
            AuctionEntitlementSnapshot premium)
        {
            AuctionListingEligibilityResult Result(AuctionListingEligibilityStatus status, string reason,
                AuctionItemEnvelope source = null, AuctionNativeListingProof proof = null)
                => new(status, reason, lease?.SessionId ?? Guid.Empty, lease?.CharacterId ?? 0,
                    lease?.AccountId ?? 0, list, slot, quantity, nowUnix, catalog.SourcePvfSha256, source, proof);
            if (lease == null || lease.Inventory == null
                || !InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                return Result(AuctionListingEligibilityStatus.Rejected, "CurrentOwnedLeaseRequired");
            bool avatarSource = list == InventoryListType.Avatar;
            bool mainSource = list == InventoryListType.Main;
            if (!(mainSource && slot >= InventoryService.MainSlotStart && slot <= InventoryService.MainSlotEnd)
                && !(avatarSource && slot >= InventoryService.AvatarSlotStart && slot <= InventoryService.AvatarSlotEnd))
                return Result(AuctionListingEligibilityStatus.Unavailable, "NativeSourceSlotUnsupported");
            if (quantity <= 0 || nowUnix < 0 || requestedExpiryUnix <= nowUnix)
                return Result(AuctionListingEligibilityStatus.Rejected, "InvalidListingQuantityOrTime");
            if (premium == null || premium.AccountId != lease.AccountId || premium.ObservedAtUnix != nowUnix)
                return Result(AuctionListingEligibilityStatus.Unavailable, "SameAccountEntitlementSnapshotRequired");

            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                    return Result(AuctionListingEligibilityStatus.Rejected, "StaleOwnedLease");
                var actual = lease.Inventory.GetItem(list, slot);
                if (actual == null || actual.IsEmpty || actual.ItemId <= 0)
                    return Result(AuctionListingEligibilityStatus.Rejected, "MissingOwnedSource");
                if (actual.EquipmentLockId != 0 || actual.SortLockFlag != 0 || actual.TradeRestriction != 0)
                    return Result(AuctionListingEligibilityStatus.Rejected, "LockedOrRestrictedInstance");
                if (InventoryStackRuleService.IsStackable(actual) ? quantity > actual.Count : quantity != 1)
                    return Result(AuctionListingEligibilityStatus.Rejected, "QuantityExceedsOwnedSource");
                if (!catalog.TryGet(actual.ItemId, out var template)
                    || template.SourcePvfSha256 != catalog.SourcePvfSha256)
                    return Result(AuctionListingEligibilityStatus.Unavailable, "ExactTemplateUnavailable");
                if (template.PackagableGate == AuctionPackagableGate.Rejected)
                    return Result(AuctionListingEligibilityStatus.Rejected, "NativePackagableZero");
                if (template.PackagableGate != AuctionPackagableGate.Passed)
                    return Result(AuctionListingEligibilityStatus.Unavailable, "PackagableFieldUnavailable");
                if (!template.TemporalTagsAbsent)
                    return Result(AuctionListingEligibilityStatus.Unavailable, "TemplateTemporalTagsPresentUnsupported");
                bool avatarTemplate = template.Kind == "equipment"
                    && EquipmentTypeInfo.TryParse(template.EquipmentType, out var avatarType)
                    && EquipmentTypeInfo.IsAvatarPart(avatarType);
                if ((avatarSource && (actual.ItemKind != ItemCore.KindAvatar || quantity != 1))
                    || (!avatarSource && actual.ItemKind == ItemCore.KindAvatar)
                    || actual.ItemKind == ItemCore.KindCreature || actual.ItemKind == ItemCore.KindEpicPiece)
                    return Result(AuctionListingEligibilityStatus.Unavailable, "SourceDetailKindUnsupported");
                if ((avatarSource && !avatarTemplate) || (!avatarSource && avatarTemplate)
                    || (template.Kind == "equipment" && EquipmentTypeInfo.TryParse(template.EquipmentType, out var equipmentType)
                        && equipmentType == EquipmentType.Creature))
                    return Result(AuctionListingEligibilityStatus.Unavailable, "TemplateTransferDetailUnsupported");
                if ((actual.ItemKind is ItemCore.KindEquipment or ItemCore.KindAvatar) != (template.Kind == "equipment"))
                    return Result(AuctionListingEligibilityStatus.Unavailable, "TemplateAndInstanceKindMismatch");
                // Auction expiry/return mail semantics for timed items are not
                // established by direct-trade or mail's different time policies.
                if (actual.ExpireTime != 0)
                    return Result(AuctionListingEligibilityStatus.Unavailable, "TimedAuctionTransferUnverified");
                var metadata = new ItemMetadata { AttachType = template.AttachType };
                if (MailboxSendPolicy.IsTradeLimitItem(metadata))
                    return Result(actual.StackTradeCount == 0 ? AuctionListingEligibilityStatus.Rejected : AuctionListingEligibilityStatus.Unavailable,
                        actual.StackTradeCount == 0 ? "TradeCountExhausted" : "AuctionTradeCountTransformUnverified");
                // The market buyer is a different account. This pure check only
                // compares account identities; it does not query a fake recipient.
                var transfer = MailboxSendPolicy.ValidateAttachType(new MailboxSendRequest {
                    SenderAccountId = lease.AccountId, ReceiverAccountId = lease.AccountId == 1 ? 2 : 1
                }, template.AttachType, actual.SealFlag);
                if (transfer == MailboxSendError.AccountBound)
                    return Result(AuctionListingEligibilityStatus.Rejected, "AccountBoundCannotEnterCrossAccountMarket");
                if (transfer != MailboxSendError.None)
                    return Result(AuctionListingEligibilityStatus.Unavailable, "ExistingTransferPolicyDoesNotAllowTemplate");

                AuctionItemEnvelope source;
                try
                {
                    source = codec.Capture(lease.Inventory, list, slot);
                    if (avatarSource)
                    {
                        var detail = MailboxItemDetailCodec.BuildCreateOptionsStrict(actual, source.DetailJson)?.AvatarDetailTemplate;
                        if (detail == null || detail.ExpireDate != 0 || detail.DeleteDate != 0 || detail.ClearAvatarId != 0
                            || actual.ChronicleOptionCount != 0 || actual.SealFlag > 1)
                            return Result(AuctionListingEligibilityStatus.Unavailable, "PermanentOrdinaryAvatarDetailRequired");
                    }
                }
                catch (AuctionItemCodecException exception)
                { return Result(AuctionListingEligibilityStatus.Unavailable, exception.Error.ToString()); }
                catch (InvalidOperationException)
                { return Result(AuctionListingEligibilityStatus.Unavailable, "StrictSourceDetailUnavailable"); }
                var context = new AuctionNativeListingContext(lease.AccountId, lease.CharacterId, template,
                    source, source.ContentHashHex, quantity, nowUnix, requestedExpiryUnix, premium.IsActive);
                var proof = nativeResolver(context);
                if (proof == null || proof.Kind == AuctionNativeProofKind.Unknown)
                    return Result(AuctionListingEligibilityStatus.Unavailable, "NativeListingRuleUnavailable", source, proof);
                if (!Enum.IsDefined(proof.Kind) || !Enum.IsDefined(proof.Status)
                    || !Enum.IsDefined(proof.Capability) || proof.TemplateId != actual.ItemId
                    || proof.SourceContentHash != source.ContentHashHex || proof.SourcePvfSha256 != catalog.SourcePvfSha256
                    || string.IsNullOrWhiteSpace(proof.EvidenceReference)
                    || (proof.Kind == AuctionNativeProofKind.Fixture && !allowFixtureProofs))
                    return Result(AuctionListingEligibilityStatus.Unavailable, "NativeProofBindingOrAuthorityMismatch", source, proof);
                if (proof.Status != AuctionListingEligibilityStatus.Allowed)
                    return Result(proof.Status, string.IsNullOrWhiteSpace(proof.Reason) ? "NativeListingDeniedOrUnknown" : proof.Reason, source, proof);
                var expectedCapability = avatarSource
                    ? AuctionListingTransferCapability.PermanentOrdinaryAvatarUidRebased
                    : AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount;
                if (proof.Capability != expectedCapability)
                    return Result(AuctionListingEligibilityStatus.Unavailable, "NativeTransferCapabilityUnverified", source, proof);
                if (proof.RequiresPremium && !premium.IsActive)
                    return Result(AuctionListingEligibilityStatus.Rejected, "NativeClassRequiresActiveAuctionPremium", source, proof);
                // An injected resolver is read-only, but prove the source and
                // owner still match rather than trusting that implementation.
                if (!InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId)
                    || !codec.MatchesSource(lease.Inventory, list, slot, source))
                    return Result(AuctionListingEligibilityStatus.Rejected, "SourceChangedDuringEligibility", source, proof);
                return Result(AuctionListingEligibilityStatus.Allowed, "", source, proof);
            }
        }
    }
}
