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
    // M6 actual UI: each entered price is per-item. Sender0232F484..F48F
    // leaves the proven stack profile's input unscaled; normalize it once to
    // aggregate domain prices. Equipment quantity1 has the same numeric value.
    // Source proof and all actual transfer rules remain the Market's authority.
    // Source1 is the Avatar BAG; Equipment3/body is never a numeric alias.
    internal static bool TryResolveSource(AuctionRegisterRequest request,
        out InventoryListType list, out short slot)
    {
        list = default; slot = -1;
        if (request == null) return false;
        if (request.Space == (byte)InventoryListType.Main
            && request.Slot >= InventoryService.MainSlotStart && request.Slot <= InventoryService.MainSlotEnd)
            list = InventoryListType.Main;
        else if (request.Space == (byte)InventoryListType.Avatar
            && request.Slot >= InventoryService.AvatarSlotStart && request.Slot <= InventoryService.AvatarSlotEnd)
            list = InventoryListType.Avatar;
        else return false;
        slot = checked((short)request.Slot);
        return true;
    }

    internal static AuctionNativeMutationResult Normalize(AuctionParty actor, Guid operation,
        AuctionRegisterRequest request, ItemCore actual, AuctionTemplateProjection template)
    {
        AuctionNativeMutationResult Reject(string reason) => new(AuctionNativeMutationStatus.Rejected, reason, null);
        if (actor == null || actor.AccountId <= 0 || actor.CharacterId <= 0 || operation == Guid.Empty
            || request == null || actual == null || actual.IsEmpty || template == null
            || actual.ItemKind < ItemCore.KindEquipment || actual.ItemKind > ItemCore.KindEpicPiece)
            return Reject("AuthenticatedOwnedSourceRequired");
        if (!TryResolveSource(request, out var sourceList, out var sourceSlot))
            return new(AuctionNativeMutationStatus.Unavailable, "NativeSourceSpaceUnverified", null);
        bool avatar = sourceList == InventoryListType.Avatar;
        bool stack = InventoryStackRuleService.IsStackable(actual);
        bool avatarTemplate = template.Kind == "equipment"
            && EquipmentTypeInfo.TryParse(template.EquipmentType, out var avatarType)
            && EquipmentTypeInfo.IsAvatarPart(avatarType);
        if (request.ItemKey != actual.ItemId || template.TemplateId != actual.ItemId
            || (avatar ? actual.ItemKind != ItemCore.KindAvatar || !avatarTemplate
                : stack ? template.Kind != "stackable"
                    : actual.ItemKind != ItemCore.KindEquipment || template.Kind != "equipment")
            || request.Quantity == 0 || request.Quantity > int.MaxValue
            || (stack ? request.Quantity > actual.Count : request.Quantity != 1)
            || request.Wire24 is not (24 or 48)) return Reject("RegistrationSourceOrQuantityMismatch");
        long? bid = request.Wire12 == uint.MaxValue ? null : request.Wire12;
        long? buyout = request.Wire16 == uint.MaxValue ? null : request.Wire16;
        if ((!bid.HasValue && !buyout.HasValue) || bid <= 0 || bid > int.MaxValue
            || buyout <= 0 || buyout > int.MaxValue || bid >= buyout
            || request.Wire20 != (buyout ?? 0)) return Reject("RegistrationPriceMismatch");
        long? unit = null;
        if (stack)
        {
            if (actual.ItemKind != ItemCore.KindMaterial
                || !AuctionNativeListingRuleResolver.SupportsPerItemRegistrationPrice(template))
                return new(AuctionNativeMutationStatus.Unavailable, "NativePerItemPriceProfileUnverified", null);
            // Do not infer another native branch just because a packet fits a
            // P*Q/P tuple. Only the independently proved equal-input profile is
            // accepted here. Full eligibility/ownership is rechecked by Market.
            unit = !bid.HasValue ? buyout : null;
            bid = bid.HasValue ? checked(bid.Value * (long)request.Quantity) : null;
            buyout = buyout.HasValue ? checked(buyout.Value * (long)request.Quantity) : null;
            if (bid > int.MaxValue || buyout > int.MaxValue) return Reject("RegistrationAggregatePriceOutOfRange");
        }
        // Client tuples are search hints, never authority to rewrite the owned instance.
        return new(AuctionNativeMutationStatus.Ready, "", new(operation, AuctionOperationAction.List,
            0, 0, actor, (int)request.Quantity, 0, 0, bid, buyout, unit,
            sourceList, sourceSlot, (int)request.Wire24));
    }
}
