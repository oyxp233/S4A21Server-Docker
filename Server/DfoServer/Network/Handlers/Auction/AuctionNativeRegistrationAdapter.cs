using System;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld.Auction;
using DfoServer.Network.Parsers.Auction;

namespace DfoServer.Network.Handlers.Auction;

internal static class AuctionNativeRegistrationAdapter
{
    // A21 sender0232F484..F5C3 preserves the per-item second input separately
    // from its optional quantity scaling. Normalize to domain totals once.
    // Current owned bag state and transfer eligibility remain Market authority.
    internal static bool TryResolveSource(AuctionRegisterRequest request,
        out InventoryListType list, out short slot)
    {
        list = default; slot = -1;
        if (request == null) return false;
        list = (InventoryListType)request.Space;
        if (!AuctionListingEligibility.IsBagSlot(list, request.Slot)) return false;
        slot = checked((short)request.Slot);
        return true;
    }

    internal static AuctionNativeMutationResult Normalize(AuctionParty actor, Guid operation,
        AuctionRegisterRequest request, ItemCore actual, AuctionTemplateProjection template)
    {
        AuctionNativeMutationResult Reject(string reason) => new(AuctionNativeMutationStatus.Rejected, reason, null);
        if (actor == null || actor.AccountId <= 0 || actor.CharacterId <= 0 || operation == Guid.Empty
            || request == null || actual == null || actual.IsEmpty || template == null
            || actual.ItemKind < ItemCore.KindEquipment || actual.ItemKind > ItemCore.KindGuardianGem)
            return Reject("AuthenticatedOwnedSourceRequired");
        if (!TryResolveSource(request, out var sourceList, out var sourceSlot))
            return new(AuctionNativeMutationStatus.Unavailable, "NativeSourceSpaceUnverified", null);
        bool avatar = sourceList == InventoryListType.Avatar;
        bool stack = InventoryStackRuleService.IsStackable(actual);
        bool avatarTemplate = template.Kind == "equipment"
            && EquipmentTypeInfo.TryParse(template.EquipmentType, out var avatarType)
            && EquipmentTypeInfo.IsAvatarPart(avatarType);
        if (request.ItemKey != actual.ItemId || template.TemplateId != actual.ItemId
            || !AuctionListingEligibility.MatchesBagKind(sourceList, actual)
            || avatar != avatarTemplate
            || (stack ? template.Kind != "stackable"
                : actual.ItemKind == ItemCore.KindCreature || template.Kind != "equipment")
            || request.Quantity == 0 || request.Quantity > int.MaxValue
            || (stack ? request.Quantity > actual.Count : request.Quantity != 1)
            || request.Wire24 is not (24 or 48)) return Reject("RegistrationSourceOrQuantityMismatch");
        long? bid = request.Wire12 == uint.MaxValue ? null : request.Wire12;
        long? buyout = request.Wire16 == uint.MaxValue ? null : request.Wire16;
        if ((!bid.HasValue && !buyout.HasValue) || bid <= 0 || bid > int.MaxValue
            || buyout <= 0 || buyout > int.MaxValue || bid >= buyout)
            return Reject("RegistrationPriceMismatch");
        long? unit = null;
        if (stack)
        {
            // A21 sender 0232F484..0232F5C3: wire20 retains the second
            // input. Query1=true sends per-item prices; false sends prices*Q.
            // Validate the relation before choosing the normalization. No item
            // ID or resource fingerprint participates in price interpretation.
            bool perItem = !buyout.HasValue || buyout.Value == request.Wire20;
            if (buyout.HasValue && (request.Wire20 == 0
                || (!perItem && buyout.Value != (long)request.Wire20 * request.Quantity)))
                return Reject("RegistrationPriceMismatch");
            if (!buyout.HasValue && request.Wire20 != 0) return Reject("RegistrationPriceMismatch");
            unit = !bid.HasValue ? request.Wire20 : null;
            if (perItem)
            {
                bid = bid.HasValue ? bid.Value * request.Quantity : null;
                buyout = buyout.HasValue ? buyout.Value * request.Quantity : null;
            }
            if (bid > int.MaxValue || buyout > int.MaxValue) return Reject("RegistrationAggregatePriceOutOfRange");
        }
        else if (request.Wire20 != (buyout ?? 0)) return Reject("RegistrationPriceMismatch");
        // Client tuples are search hints, never authority to rewrite the owned instance.
        return new(AuctionNativeMutationStatus.Ready, "", new(operation, AuctionOperationAction.List,
            0, 0, actor, (int)request.Quantity, 0, 0, bid, buyout, unit,
            sourceList, sourceSlot, (int)request.Wire24));
    }
}
