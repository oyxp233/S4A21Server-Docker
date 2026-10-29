using System;
using System.Buffers.Binary;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Auction;

namespace DfoServer.Game.Auction.Rules
{
    internal enum AuctionCouponGateStatus { Unavailable, Passed, Rejected }
    internal sealed record AuctionCouponGateEvaluation(AuctionCouponGateStatus Status,
        bool RequiresPremium, string Reason);

    // Resource facts and instance transfer rules determine eligibility.
    // A captured PVF fingerprint is provenance, never a global allowlist.
    internal static class AuctionNativeListingRuleResolver
    {
        private const string Evidence = "Docs/拍卖行业务与持久化.md; A21 VA0232FD90 coupon gate; "
            + "MailboxSendPolicy.ValidateAttachType; resource-driven ordinary auction policy";

        internal static AuctionNativeListingProof Resolve(AuctionNativeListingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var template = context.Template;
            AuctionNativeListingProof Proof(AuctionListingEligibilityStatus status, bool premium, string reason,
                AuctionListingTransferCapability capability = AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount)
                => new(status == AuctionListingEligibilityStatus.Unavailable ? AuctionNativeProofKind.Unknown : AuctionNativeProofKind.VerifiedA21,
                    status, template?.TemplateId ?? 0, context.SourceContentHash, template?.SourcePvfSha256,
                    status == AuctionListingEligibilityStatus.Allowed ? capability : AuctionListingTransferCapability.None,
                    premium, Evidence, reason);
            var coupon = EvaluateCoupon(context);
            if (coupon.Status != AuctionCouponGateStatus.Passed)
                return Proof(coupon.Status == AuctionCouponGateStatus.Rejected
                    ? AuctionListingEligibilityStatus.Rejected : AuctionListingEligibilityStatus.Unavailable,
                    coupon.RequiresPremium, coupon.Reason);
            if (template.PackagableGate == AuctionPackagableGate.Rejected)
                return Proof(AuctionListingEligibilityStatus.Rejected, false, "NativePackagableZero");
            if (template.PackagableGate != AuctionPackagableGate.Passed || !template.HasUnambiguousTradeDeclarations)
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "MalformedTradeDeclaration");
            if (HasAuctionRestriction(template))
                return Proof(AuctionListingEligibilityStatus.Rejected, false, "TemplateProhibitsAuction");
            var core = ItemCore.FromBytes(context.Source.NativeCore99);
            if (core.EquipmentLockId != 0 || core.SortLockFlag != 0 || core.TradeRestriction != 0)
                return Proof(AuctionListingEligibilityStatus.Rejected, false, "LockedOrRestrictedInstance");
            if (core.ExpireTime != 0 || !template.TemporalTagsAbsent)
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "TimedAuctionTransferUnverified");
            if (core.ItemKind == ItemCore.KindCreature || core.ItemKind == ItemCore.KindEpicPiece
                || core.IsEquipmentItem() != (template.Kind == "equipment"))
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "TemplateAndInstanceKindMismatch");
            if (InventoryStackRuleService.IsStackable(core) ? context.Quantity > core.Count : context.Quantity != 1)
                return Proof(AuctionListingEligibilityStatus.Rejected, false, "QuantityExceedsOwnedSource");
            var transfer = MailboxSendPolicy.ValidateAttachType(new MailboxSendRequest {
                SenderAccountId = context.AccountId, ReceiverAccountId = context.AccountId == 1 ? 2 : 1
            }, template.AttachType, core.SealFlag);
            if (transfer != MailboxSendError.None)
                return Proof(AuctionListingEligibilityStatus.Rejected, false, "TemplateNotTradable");
            bool avatar = core.ItemKind == ItemCore.KindAvatar;
            if (template.Kind == "equipment"
                && (EquipmentTypeInfo.IsAvatarPart(EquipmentTypeInfo.ParseOrUnknown(template.EquipmentType)) != avatar))
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "TemplateAndInstanceKindMismatch");
            return Proof(AuctionListingEligibilityStatus.Allowed, coupon.RequiresPremium, "",
                avatar ? AuctionListingTransferCapability.PermanentOrdinaryAvatarUidRebased
                    : AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount);
        }

        internal static bool HasAuctionRestriction(AuctionTemplateProjection template)
            => template.ImpossibleContents.Any(value => {
                var token = value.Replace("`", "").Replace("[", "").Replace("]", "").Trim();
                return token.Equals("auction", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("trade", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("all", StringComparison.OrdinalIgnoreCase);
            });

        internal static AuctionCouponGateEvaluation EvaluateCoupon(AuctionNativeListingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var template = context.Template;
            AuctionCouponGateEvaluation Unavailable(string reason) => new(AuctionCouponGateStatus.Unavailable, false, reason);
            if (template == null || template.TemplateId <= 0 || context.Source == null || context.AccountId <= 0 || context.CharacterId <= 0
                || context.Quantity <= 0 || context.ObservedAtUnix < 0 || context.RequestedExpiryUnix <= context.ObservedAtUnix
                || context.SourceContentHash != context.Source.ContentHashHex)
                return Unavailable("NativeContextBindingUnavailable");
            context.Source.VerifyIntegrity();
            if (BinaryPrimitives.ReadInt32LittleEndian(context.Source.NativeCore99.AsSpan(ItemCore.ItemIdOffset, 4)) != template.TemplateId)
                return Unavailable("NativeTemplateSourceMismatch");
            if (template.Kind != "equipment" && template.Kind != "stackable") return Unavailable("NativeClassUnverified");
            EquipmentType type = EquipmentType.Unknown;
            if (template.Kind == "equipment" && !EquipmentTypeInfo.TryParse(template.EquipmentType, out type))
                return Unavailable("NativeEquipmentClassUnverified");
            bool premium = template.Kind == "equipment" && !template.HasAuctionFree
                && (EquipmentTypeInfo.IsAvatarPart(type) || template.CashInteger > 0);
            if (premium && !context.HasActiveAuctionPremium)
                return new(AuctionCouponGateStatus.Rejected, true, "NativeClassRequiresActiveAuctionPremium");
            if (template.Kind == "equipment" && template.CashInteger < 0 && !context.HasActiveAuctionPremium && !template.HasAuctionFree)
                return Unavailable("NegativeNativeCashFieldUnsupported");
            return new(AuctionCouponGateStatus.Passed, premium, "");
        }
    }
}
