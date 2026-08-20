using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Game.SecretShop
{
    internal sealed class SecretShopPurchaseResult
    {
        internal int ItemId { get; init; }
        internal int ItemCount { get; init; }
        internal int GoldCost { get; init; }
        internal int RequiredItemId { get; init; }
        internal int RequiredItemCount { get; init; }
        internal short CostItemSlot { get; init; }
        internal short AssignedSlot { get; init; }
        internal int ItemValue { get; init; }
        internal byte ExtData0 { get; init; }
        internal ushort Durability { get; init; }
        internal int UpdatedGold { get; init; }
        internal int CostItemRemainingCount { get; init; }
        internal int OfferRemainingCount { get; init; }
    }

    internal sealed class SecretShopPurchaseService
    {
        internal SecretShopPurchaseService()
        {
        }

        internal bool TryPurchase(
            InventoryLease lease,
            SecretShopOffer offer,
            int itemId,
            int requestedCount,
            out SecretShopPurchaseResult result)
        {
            result = null;
            if (lease?.Inventory == null
                || offer == null
                || !offer.IsSecretShop
                || itemId <= 0)
            {
                return false;
            }

            InventoryMutationResult mutation = null;
            try
            {
                if (!offer.TryCompletePurchase(
                        itemId,
                        requestedCount,
                        (item, purchaseCount) =>
                        {
                            if (item.Count <= 0 || item.Price < 0 || item.RawFlag is not (0 or 1))
                                return false;

                            var usesItemCurrency = item.RawFlag == 1;
                            var totalPrice = checked(item.Price * purchaseCount);
                            return OnlineInventoryMutationCommitCoordinator.TryCommit(
                                lease,
                                "secret-shop-buy",
                                (connection, transaction) =>
                                    InventoryShopRuntimeService.TryBuySecretShopItem(
                                        lease.Inventory,
                                        item.ItemId,
                                        purchaseCount,
                                        usesItemCurrency ? 0 : totalPrice,
                                        usesItemCurrency ? item.RequiredItemId : 0,
                                        usesItemCurrency ? totalPrice : 0,
                                        out mutation));
                        },
                        out var purchased,
                        out var purchasedCount,
                        out var remainingCount))
                    return false;

                var totalCost = checked(purchased.Price * purchasedCount);
                result = new SecretShopPurchaseResult
                {
                    ItemId = purchased.ItemId,
                    ItemCount = purchasedCount,
                    GoldCost = purchased.RawFlag == 0 ? totalCost : 0,
                    RequiredItemId = purchased.RawFlag == 1 ? purchased.RequiredItemId : 0,
                    RequiredItemCount = purchased.RawFlag == 1 ? totalCost : 0,
                    CostItemSlot = mutation.CostItemSlotIndex,
                    AssignedSlot = mutation.SlotIndex,
                    ItemValue = mutation.InstanceValue,
                    ExtData0 = mutation.ExtData0,
                    Durability = mutation.Durability,
                    UpdatedGold = mutation.UpdatedGold,
                    CostItemRemainingCount = mutation.CostItemRemainingCount,
                    OfferRemainingCount = remainingCount,
                };
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[SecretShop] purchase transaction failed: char={lease?.CharacterId ?? 0} item={itemId} error={ex.Message}");
                result = null;
                return false;
            }
        }
    }
}
