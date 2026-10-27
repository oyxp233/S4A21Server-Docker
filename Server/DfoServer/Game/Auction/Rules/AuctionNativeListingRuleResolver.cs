using System;
using System.Buffers.Binary;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld.Auction;

namespace DfoServer.Game.Auction.Rules
{
    internal enum AuctionCouponGateStatus { Unavailable, Passed, Rejected }
    // A component result deliberately has no proof kind or transfer capability.
    internal sealed record AuctionCouponGateEvaluation(AuctionCouponGateStatus Status,
        bool RequiresPremium, string Reason);

    // Coupon passage is not full native eligibility. M5 positively separates
    // packagable, common virtual+8/+C8, class/feature and coupon conditions.
    internal static class AuctionNativeListingRuleResolver
    {
        private const string SupportedPvf = "963cd331342a0066c01cb189ec2cd822b8b8206065b109ec63ca664adb3da8d8";
        private const string Evidence = "Docs/\u62cd\u5356\u884c\u4e1a\u52a1\u4e0e\u8fd0\u8425\u63a5\u53e3.md; "
            + "A21 VA0232FD90; auction-free CB4/2E0A->model flag15->item2C9; "
            + "equipment-type B77->model3C0->item60C; cash C3A/2E07->model1C->item6C; "
            + "RTTI0395FC40/0396A8B4; finite material/free-equipment/waist-avatar profiles";

        internal static AuctionNativeListingProof Resolve(AuctionNativeListingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var template = context.Template;
            AuctionNativeListingProof Proof(AuctionListingEligibilityStatus status, bool requiresPremium, string reason,
                AuctionListingTransferCapability capability = AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount)
                => new(status != AuctionListingEligibilityStatus.Unavailable ? AuctionNativeProofKind.VerifiedA21 : AuctionNativeProofKind.Unknown,
                    status, template?.TemplateId ?? 0, context.SourceContentHash,
                    template?.SourcePvfSha256, status == AuctionListingEligibilityStatus.Allowed
                        ? capability : AuctionListingTransferCapability.None,
                    requiresPremium, Evidence, reason);
            var coupon = EvaluateCoupon(context);
            if (coupon.Status == AuctionCouponGateStatus.Rejected)
                return Proof(AuctionListingEligibilityStatus.Rejected, coupon.RequiresPremium, coupon.Reason);
            if (coupon.Status != AuctionCouponGateStatus.Passed)
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, coupon.Reason);
            if (template.PackagableGate == AuctionPackagableGate.Rejected)
                return Proof(AuctionListingEligibilityStatus.Rejected, false, "NativePackagableZero");
            if (template.PackagableGate != AuctionPackagableGate.Passed)
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "PackagableFieldUnavailable");
            if (!template.TemporalTagsAbsent)
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "TemplateTemporalTagsPresentUnsupported");
            if (template.ImpossibleContents.Count != 0)
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "AuctionImpossibleContentsConsumerUnverified");
            if (template.Kind == "equipment")
            {
                // M9: four exact native7/free templates; permanent Source1,
                // strict owned detail and cross-account transfer remain with
                // Eligibility/Market. Their native Avatar class requires premium.
                if (SupportsFreePermanentWaistAvatar(template))
                {
                    var avatar = ItemCore.FromBytes(context.Source.NativeCore99);
                    if (context.Quantity != 1 || avatar.ItemKind != ItemCore.KindAvatar || avatar.ExpireTime != 0)
                        return Proof(AuctionListingEligibilityStatus.Unavailable, true, "NativeFreeWaistAvatarInstanceUnsupported");
                    return Proof(AuctionListingEligibilityStatus.Allowed, true, "",
                        AuctionListingTransferCapability.PermanentOrdinaryAvatarUidRebased);
                }
                // M8: native12 + the finite generic-factory ID interval only.
                // Normal constructed object/registry and stable VM frame are
                // the native preconditions, not a per-session/instance permit.
                if (!SupportsFreeOrdinaryWeapon(template))
                    return Proof(AuctionListingEligibilityStatus.Unavailable, false, "EquipmentCommonProtectedPredicateUnverified");
                var equipment = ItemCore.FromBytes(context.Source.NativeCore99);
                if (context.Quantity != 1 || equipment.ItemKind != ItemCore.KindEquipment || equipment.ExpireTime != 0)
                    return Proof(AuctionListingEligibilityStatus.Unavailable, false, "NativeFreeWeaponInstanceUnsupported");
                // Main placement, owned lease, locks/seal/transfer policy and
                // full99/detail/private integrity remain with their old owners.
                return Proof(AuctionListingEligibilityStatus.Allowed, false, "");
            }
            if (template.TemplateId is 3033 or 3034 or 3035 or 3036 or 3037 or 3262)
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "StackFeature10DependencyUnverified");
            var attach = template.AttachType?.Trim().Trim('`').Trim();
            if (!string.Equals(attach, "[free]", StringComparison.OrdinalIgnoreCase))
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "StackCommonAttachClassUnverified");
            // M5 closes the complete possible factory/vtable union, temporal
            // defaults and iteminfo membership for these three exact resources.
            // A matching kind or material label alone never grants this proof.
            var expected = NativeMaterialProfile(template.TemplateId);
            if (expected.Path == null)
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "StackFactoryPredicateUnverified");
            if (!SupportsPerItemRegistrationPrice(template))
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "NativeMaterialSupportProfileMismatch");
            var core = ItemCore.FromBytes(context.Source.NativeCore99);
            if (core.ItemKind != ItemCore.KindMaterial)
                return Proof(AuctionListingEligibilityStatus.Unavailable, false, "NativeMaterialInstanceKindUnverified");
            // Source ownership, Main placement, locks, count and permanent
            // instance checks remain mandatory in AuctionListingEligibility.
            // The proof binds those observed bytes, never a future source item.
            return Proof(AuctionListingEligibilityStatus.Allowed, false, "");
        }

        private static bool SupportsFreeOrdinaryWeapon(AuctionTemplateProjection template)
        {
            // 013F8900..891D: IDs27567..27673 skip every special-key constructor
            // and reach 013FB822/EAX0; normal fallback is016DAE30. The two
            // earlier alternative constructors share +8/+C8/+D0/model methods.
            // The required iteminfo membership further narrows this to27600..27673:
            // all74 keys are unique members in the same963c resource. Earlier
            // keys in the factory interval lack complete membership proof.
            // Other proven default intervals are deliberately not enabled here.
            return template.SourcePvfSha256 == SupportedPvf
                && template.TemplateId is >= 27600 and <= 27673
                && template.HasSingleNativeWeaponDeclaration && template.HasSingleNativeFreeAttachDeclaration
                && EquipmentTypeInfo.TryParse(template.EquipmentType, out var type) && type == EquipmentType.Weapon
                && string.Equals(template.AttachType?.Trim().Trim('`').Trim(), "[free]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SupportsFreePermanentWaistAvatar(AuctionTemplateProjection template)
        {
            // Four unique iteminfo members and selector default-zero keys.
            // The flag22/string/base primary-table union shares +8/+C8/+D0/
            // +144/+17C; free0 and actual permanent FD0=0 reuse those predicates.
            // This is not a path heuristic, per-instance permit or generic Avatar grant.
            return template.SourcePvfSha256 == SupportedPvf
                && template.TemplateId is >= 53585 and <= 53588
                && template.ResourcePath == $"equipment/character/mage/avatar/belt/abelt_{template.TemplateId}.equ"
                && template.HasSingleNativeWaistAvatarZeroDeclaration && template.HasSingleNativeFreeAttachDeclaration
                && EquipmentTypeInfo.TryParse(template.EquipmentType, out var type) && type == EquipmentType.WaistAvatar
                && string.Equals(template.AttachType?.Trim().Trim('`').Trim(), "[free]", StringComparison.OrdinalIgnoreCase)
                // The exact four resources have neither cash nor auction-free.
                // Do not silently create a different coupon/cash profile.
                && template.CashInteger == 0 && !template.HasAuctionFree;
        }

        // Price compatibility is a separate component, not listing permission.
        // M6 UI P22/Q2 shows gross44; fresh native witnesses bind these exact
        // resources to flags04=1 / virtual+2C=00EE6B40. Equal wire16/20 are
        // the entered PER-ITEM price on that branch, not the aggregate.
        internal static bool SupportsPerItemRegistrationPrice(AuctionTemplateProjection template)
        {
            if (template == null || template.Kind != "stackable") return false;
            var expected = NativeMaterialProfile(template.TemplateId);
            return expected.Path != null && template.SourcePvfSha256 == SupportedPvf
                && template.ResourcePath == expected.Path && template.SingleMaterialArgument == expected.Argument;
        }

        private static (string Path, int Argument) NativeMaterialProfile(int id) => id switch {
            3028 => ("stackable/material/piece_leather.stk", 1),
            3053 => ("stackable/material/seal_mire.stk", 8),
            3156 => ("stackable/material/rubstone_1.stk", 1),
            _ => (null, 0)
        };

        internal static AuctionCouponGateEvaluation EvaluateCoupon(AuctionNativeListingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var template = context.Template;
            AuctionCouponGateEvaluation Unavailable(string reason) => new(AuctionCouponGateStatus.Unavailable, false, reason);
            AuctionCouponGateEvaluation Passed() => new(AuctionCouponGateStatus.Passed, false, "");
            AuctionCouponGateEvaluation Rejected() => new(AuctionCouponGateStatus.Rejected, true, "NativeClassRequiresActiveAuctionPremium");
            if (template == null || template.TemplateId <= 0 || context.Source == null || context.AccountId <= 0 || context.CharacterId <= 0
                || context.Quantity <= 0 || context.ObservedAtUnix < 0 || context.RequestedExpiryUnix <= context.ObservedAtUnix
                || context.SourceContentHash != context.Source.ContentHashHex)
                return Unavailable("NativeContextBindingUnavailable");
            context.Source.VerifyIntegrity();
            if (BinaryPrimitives.ReadInt32LittleEndian(context.Source.NativeCore99.AsSpan(ItemCore.ItemIdOffset, 4)) != template.TemplateId)
                return Unavailable("NativeTemplateSourceMismatch");
            if (template.Kind != "equipment" && template.Kind != "stackable")
                return Unavailable("NativeClassUnverified");
            EquipmentType type = EquipmentType.Unknown;
            if (template.Kind == "equipment" && (!EquipmentTypeInfo.TryParse(template.EquipmentType, out type)
                || (int)type < 0 || (int)type > (int)EquipmentType.GuildMedal))
                return Unavailable("NativeEquipmentClassUnverified");

            if (context.HasActiveAuctionPremium || template.HasAuctionFree)
                return Passed();
            // RTTI proves ordinary stackable is not the dynamic_cast target.
            // This is this gate's behavior, not an inference from a cash path.
            if (template.Kind == "stackable")
                return Passed();
            if (EquipmentTypeInfo.IsAvatarPart(type))
                return Rejected();
            // This is a conservative input policy only in the branch which
            // actually reads cash. Native itself accepts signed cash <= 0.
            if (template.CashInteger < 0)
                return Unavailable("NegativeNativeCashFieldUnsupported");
            if (template.CashInteger > 0)
                return Rejected();
            return Passed();
        }
    }
}
