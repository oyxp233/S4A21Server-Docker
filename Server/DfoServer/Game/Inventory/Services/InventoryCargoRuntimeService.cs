using System;
using DfoServer.Game.Currency;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryCargoRuntimeService
    {
        private const int CargoInitialCapacity = 1;
        private const int AccountCargoCreateGoldCost = 100000;
        private const int AccountCargoUpgradeVoidMagicStoneItemId = 3299;
        private const int AccountCargoUpgradeVoidMagicStoneCost = 250;
        private static readonly int[] AccountCargoCapacityTiers = { 1, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120 };
        private static readonly ushort[] PersonalCargoCapacityTiers =
        {
            24, 40, 56, 72, 88, 104, 120, 136, 152, 168, 184, 200
        };

        internal static bool TryDepositCargoGold(
            InventoryService inventory,
            int amount,
            out int newCharGold,
            out int newCargoGold,
            out InventoryMutationResult mutation)
        {
            newCharGold = 0;
            newCargoGold = 0;
            mutation = null;
            if (inventory == null
                || amount <= 0
                || inventory.AccountCargo.Money > int.MaxValue - amount)
                return false;

            if (!inventory.TryConsumeMainItem(0, amount, out var consumeResult) || !consumeResult.Success)
                return false;

            inventory.AccountCargo.Money += amount;
            newCharGold = consumeResult.RemainingCount;
            newCargoGold = inventory.AccountCargo.Money;
            mutation = BuildMainVirtualCountMutation(0, newCharGold);
            return true;
        }

        internal static bool TryWithdrawCargoGold(
            InventoryService inventory,
            int amount,
            out int newCharGold,
            out int newCargoGold,
            out InventoryMutationResult mutation)
        {
            newCharGold = 0;
            newCargoGold = 0;
            mutation = null;
            if (inventory == null || amount <= 0 || inventory.AccountCargo.Money < amount)
                return false;

            var currentGold = inventory.CountMainItem(0);
            if (currentGold > int.MaxValue - amount)
                return false;
            if (!inventory.SetMainVirtualCount(0, currentGold + amount))
                return false;

            inventory.AccountCargo.Money -= amount;
            newCharGold = currentGold + amount;
            newCargoGold = inventory.AccountCargo.Money;
            mutation = BuildMainVirtualCountMutation(0, newCharGold);
            return true;
        }

        internal static bool TryCreateAccountCargo(
            InventoryService inventory,
            out InventoryMutationResult costResult,
            out byte errorCode)
        {
            costResult = null;
            errorCode = 0;
            if (inventory == null)
            {
                errorCode = 0x14;
                return false;
            }

            if (inventory.AccountCargo.SelectionKey > 0)
            {
                errorCode = 0x14;
                return false;
            }

            if (!inventory.TryConsumeMainItem(0, AccountCargoCreateGoldCost, out var consumeResult) || !consumeResult.Success)
            {
                errorCode = 0x14;
                return false;
            }

            inventory.AccountCargo.SelectionKey = CargoInitialCapacity;
            costResult = BuildGoldCostResult(consumeResult.RemainingCount);
            return true;
        }

        internal static bool TryUpgradeAccountCargo(
            InventoryService inventory,
            out InventoryMutationResult costResult,
            out byte errorCode)
        {
            return TryUpgradeAccountCargo(
                inventory,
                null,
                null,
                out costResult,
                out errorCode);
        }

        internal static bool TryUpgradeAccountCargo(
            InventoryService inventory,
            SqliteConnection connection,
            SqliteTransaction transaction,
            out InventoryMutationResult costResult,
            out byte errorCode)
        {
            costResult = null;
            errorCode = 0;
            if (inventory == null)
            {
                errorCode = 0x15;
                return false;
            }

            var previous = inventory.AccountCargo.SelectionKey;
            if (previous <= 0)
            {
                errorCode = 0x15;
                return false;
            }

            if (!TryGetNextAccountCargoTier(previous, out var next))
            {
                errorCode = 0x13;
                return false;
            }

            if (!TryApplyAccountCargoUpgradeCost(
                    inventory,
                    previous,
                    connection,
                    transaction,
                    out costResult,
                    out errorCode))
                return false;

            inventory.AccountCargo.SelectionKey = (ushort)next;
            return true;
        }

        internal static bool TryUpgradePersonalCargo(
            InventoryService inventory,
            out ushort newListParam16,
            out byte errorCode)
        {
            newListParam16 = 0;
            errorCode = 0;
            if (inventory == null)
                return false;

            var current = NormalizePersonalCargoCapacity(inventory.Cargo.Capacity);
            if (!TryGetNextPersonalCargoTier(current, out newListParam16))
            {
                errorCode = 0x13;
                return false;
            }

            inventory.SetListParam16(InventoryListType.PersonalCargo, newListParam16);
            return true;
        }

        internal static bool TryUsePersonalCargoUpgradeTicket(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            out PersonalCargoUpgradeTicketResult result)
        {
            result = new PersonalCargoUpgradeTicketResult
            {
                Status = PersonalCargoUpgradeTicketStatus.NotApplicable,
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = expectedItemTemplateId,
            };

            if (inventory == null || !InventoryListRule.IsSupportedDeleteOrSellListType(listType))
                return false;

            var item = inventory.GetItem(listType, slotIndex);
            if (item == null)
            {
                if (!IsPersonalCargoUpgradeTicket(expectedItemTemplateId)
                    && !InventoryCargoUpgradeRule.TryResolvePersonalCargoUpgradeTarget(expectedItemTemplateId, out _))
                    return false;

                result.Status = PersonalCargoUpgradeTicketStatus.MissingItem;
                return true;
            }

            result.ItemTemplateId = item.ItemId;
            var hasExplicitTarget = InventoryCargoUpgradeRule.TryResolvePersonalCargoUpgradeTarget(item.ItemId, out var targetCapacity);
            if (!hasExplicitTarget && !IsPersonalCargoUpgradeTicket(item.ItemId))
                return false;

            if (IsEquipmentItemLocked(inventory, item))
            {
                result.Status = PersonalCargoUpgradeTicketStatus.Locked;
                return true;
            }

            if (InventoryStackRuleService.IsStackable(item) && item.Count <= 0)
            {
                result.Status = PersonalCargoUpgradeTicketStatus.MissingItem;
                return true;
            }

            var previous = NormalizePersonalCargoCapacity(inventory.Cargo.Capacity);
            ushort next;
            if (hasExplicitTarget)
            {
                targetCapacity = NormalizePersonalCargoCapacity(targetCapacity);
                if (!IsPersonalCargoCapacityTier(targetCapacity) || targetCapacity <= previous)
                {
                    result.Status = PersonalCargoUpgradeTicketStatus.Maxed;
                    result.PreviousListParam16 = previous;
                    result.NewListParam16 = previous;
                    return true;
                }

                next = targetCapacity;
            }
            else if (!TryGetNextPersonalCargoTier(previous, out next))
            {
                result.Status = PersonalCargoUpgradeTicketStatus.Maxed;
                result.PreviousListParam16 = previous;
                result.NewListParam16 = previous;
                return true;
            }

            if (!InventoryDeleteService.TryDecreaseStack(inventory, listType, slotIndex, 1, out var consumed)
                || !consumed.Success)
            {
                result.Status = PersonalCargoUpgradeTicketStatus.MissingItem;
                return true;
            }

            inventory.SetListParam16(InventoryListType.PersonalCargo, next);
            result.Status = PersonalCargoUpgradeTicketStatus.Upgraded;
            result.PreviousListParam16 = previous;
            result.NewListParam16 = next;
            result.ConsumedItem = BuildConsumedItemResult(listType, slotIndex, item.ItemId, consumed);
            return true;
        }

        internal static bool TryUseAccountCargoUpgradeTool(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            out AccountCargoUpgradeToolResult result)
        {
            result = new AccountCargoUpgradeToolResult
            {
                Status = AccountCargoUpgradeToolStatus.NotApplicable,
                ListType = listType,
                SlotIndex = slotIndex,
            };

            if (inventory == null || !InventoryListRule.IsSupportedDeleteOrSellListType(listType))
                return false;

            var item = inventory.GetItem(listType, slotIndex);
            if (item == null)
                return false;

            result.ItemTemplateId = item.ItemId;
            if (!InventoryCargoUpgradeRule.IsAccountCargoUpgradeToolItem(item.ItemId))
                return false;

            if (IsEquipmentItemLocked(inventory, item))
            {
                result.Status = AccountCargoUpgradeToolStatus.Locked;
                return true;
            }

            if (InventoryStackRuleService.IsStackable(item) && item.Count <= 0)
            {
                result.Status = AccountCargoUpgradeToolStatus.MissingItem;
                return true;
            }

            var previous = inventory.AccountCargo.SelectionKey;
            if (previous <= 0)
            {
                result.Status = AccountCargoUpgradeToolStatus.NotOpened;
                result.PreviousSelectionKey = previous;
                result.NewSelectionKey = previous;
                return true;
            }

            if (!TryGetNextAccountCargoTier(previous, out var next))
            {
                result.Status = AccountCargoUpgradeToolStatus.Maxed;
                result.PreviousSelectionKey = previous;
                result.NewSelectionKey = previous;
                return true;
            }

            if (!InventoryDeleteService.TryDecreaseStack(inventory, listType, slotIndex, 1, out var consumed)
                || !consumed.Success)
            {
                result.Status = AccountCargoUpgradeToolStatus.MissingItem;
                result.PreviousSelectionKey = previous;
                result.NewSelectionKey = previous;
                return true;
            }

            inventory.AccountCargo.SelectionKey = (ushort)next;
            result.Status = AccountCargoUpgradeToolStatus.Upgraded;
            result.PreviousSelectionKey = previous;
            result.NewSelectionKey = next;
            result.ConsumedItem = BuildConsumedItemResult(listType, slotIndex, item.ItemId, consumed);
            return true;
        }

        private static bool TryApplyAccountCargoUpgradeCost(
            InventoryService inventory,
            int previousSelectionKey,
            SqliteConnection connection,
            SqliteTransaction transaction,
            out InventoryMutationResult costResult,
            out byte errorCode)
        {
            costResult = null;
            errorCode = 0;

            if (TryResolveAccountCargoUpgradeGoldCost(previousSelectionKey, out var goldCost))
            {
                if (!inventory.TryConsumeMainItem(0, goldCost, out var consumeResult) || !consumeResult.Success)
                {
                    errorCode = 0x14;
                    return false;
                }

                costResult = BuildGoldCostResult(consumeResult.RemainingCount);
                return true;
            }

            if (TryResolveAccountCargoUpgradeCeraCost(previousSelectionKey, out var ceraCost))
                return TrySpendCera(inventory, ceraCost, connection, transaction, out costResult, out errorCode);

            if (TryResolveAccountCargoUpgradeMaterialCost(previousSelectionKey, out var materialItemId, out var materialCount))
            {
                if (!inventory.TryConsumeMainItem(materialItemId, materialCount, out var consumeResult) || !consumeResult.Success)
                {
                    errorCode = 0x14;
                    return false;
                }

                costResult = new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = consumeResult.SlotIndex,
                    ItemTemplateId = materialItemId,
                    RemainingStackCount = consumeResult.RemainingCount,
                    InstanceValue = consumeResult.RemainingCount,
                    UpdatedGold = inventory.CountMainItem(0),
                    CostItemTemplateId = materialItemId,
                    CostItemRemainingCount = consumeResult.RemainingCount,
                    CostItemSlotIndex = consumeResult.SlotIndex,
                    RequestedCount = (short)Math.Min(short.MaxValue, materialCount),
                    AppliedCount = (short)Math.Min(short.MaxValue, consumeResult.ConsumedCount),
                };
                return true;
            }

            return true;
        }

        private static bool TrySpendCera(
            InventoryService inventory,
            int ceraCost,
            SqliteConnection sharedConnection,
            SqliteTransaction sharedTransaction,
            out InventoryMutationResult costResult,
            out byte errorCode)
        {
            costResult = null;
            errorCode = 0;
            try
            {
                if (sharedConnection != null || sharedTransaction != null)
                {
                    if (sharedConnection == null || sharedTransaction == null)
                    {
                        errorCode = 0x14;
                        return false;
                    }

                    var wallet = CurrencyService.LoadWallet(
                        sharedConnection,
                        sharedTransaction,
                        inventory.CharacterId);
                    if (!CurrencyService.TrySpendCera(
                        sharedConnection,
                        sharedTransaction,
                        inventory.CharacterId,
                        ceraCost))
                    {
                        errorCode = 0x14;
                        return false;
                    }

                    costResult = new InventoryMutationResult
                    {
                        UpdatedGold = inventory.CountMainItem(0),
                        UpdatedSp = wallet.Sp,
                        UpdatedCoin = wallet.Cera - ceraCost,
                        UpdatedTokenCera = wallet.TokenCera,
                        UpdatedHappyTokenCera = wallet.HappyTokenCera,
                    };
                    return true;
                }

                var database = inventory?.Database
                    ?? GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        var wallet = CurrencyService.LoadWallet(connection, transaction, inventory.CharacterId);
                        if (!CurrencyService.TrySpendCera(connection, transaction, inventory.CharacterId, ceraCost))
                        {
                            errorCode = 0x14;
                            return false;
                        }

                        transaction.Commit();
                        costResult = new InventoryMutationResult
                        {
                            UpdatedGold = inventory.CountMainItem(0),
                            UpdatedSp = wallet.Sp,
                            UpdatedCoin = wallet.Cera - ceraCost,
                            UpdatedTokenCera = wallet.TokenCera,
                            UpdatedHappyTokenCera = wallet.HappyTokenCera,
                        };
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryCargo] spend cera failed cid={inventory?.CharacterId}: {ex.Message}");
                errorCode = 0x14;
                return false;
            }
        }

        private static bool TryGetNextAccountCargoTier(int current, out int next)
        {
            next = current;
            var index = Array.IndexOf(AccountCargoCapacityTiers, current);
            if (index < 0 || index + 1 >= AccountCargoCapacityTiers.Length)
                return false;

            next = AccountCargoCapacityTiers[index + 1];
            return true;
        }

        private static bool TryGetNextPersonalCargoTier(ushort current, out ushort next)
        {
            current = NormalizePersonalCargoCapacity(current);
            foreach (var tier in PersonalCargoCapacityTiers)
            {
                if (tier > current)
                {
                    next = tier;
                    return true;
                }
            }

            next = current;
            return false;
        }

        private static bool TryResolveAccountCargoUpgradeGoldCost(int previousSelectionKey, out int goldCost)
        {
            goldCost = previousSelectionKey == 1 ? 2000000 : 0;
            return goldCost > 0;
        }

        private static bool TryResolveAccountCargoUpgradeCeraCost(int previousSelectionKey, out int ceraCost)
        {
            switch (previousSelectionKey)
            {
                case 8:
                case 32:
                    ceraCost = 2000;
                    return true;
                case 40:
                    ceraCost = 2500;
                    return true;
                case 48:
                    ceraCost = 3000;
                    return true;
                case 56:
                    ceraCost = 5000;
                    return true;
                default:
                    ceraCost = 0;
                    return false;
            }
        }

        private static bool TryResolveAccountCargoUpgradeMaterialCost(int previousSelectionKey, out int itemTemplateId, out int count)
        {
            itemTemplateId = 0;
            count = 0;
            if (previousSelectionKey != 16 && previousSelectionKey != 24)
                return false;

            itemTemplateId = AccountCargoUpgradeVoidMagicStoneItemId;
            count = AccountCargoUpgradeVoidMagicStoneCost;
            return true;
        }

        private static bool IsPersonalCargoCapacityTier(ushort capacity)
        {
            foreach (var tier in PersonalCargoCapacityTiers)
            {
                if (tier == capacity)
                    return true;
            }

            return false;
        }

        private static ushort NormalizePersonalCargoCapacity(ushort capacity)
        {
            return capacity == 0 ? CargoModel.DefaultCapacity : capacity;
        }

        private static bool IsPersonalCargoUpgradeTicket(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return false;

            var stackable = StackableItemProvider.Load(itemTemplateId);
            return string.Equals(
                NormalizeStackableTag(stackable?.ActionTypeName),
                "upgrade cargo",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeStackableTag(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value.Replace("`", string.Empty).Trim().Trim('[', ']').Trim();
        }

        private static bool IsEquipmentItemLocked(InventoryService inventory, ItemCore item)
        {
            return inventory != null
                && item != null
                && item.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(item.EquipmentLockId, out var itemLock)
                && itemLock.State == 1;
        }

        private static InventoryMutationResult BuildGoldCostResult(int updatedGold)
        {
            var result = BuildMainVirtualCountMutation(0, updatedGold);
            result.GoldSpent = true;
            return result;
        }

        private static InventoryMutationResult BuildMainVirtualCountMutation(
            short slotIndex,
            int updatedCount)
        {
            InventoryService.TryResolveMainVirtualItemId(
                slotIndex,
                out var itemId);
            return new InventoryMutationResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = slotIndex,
                ItemTemplateId = itemId,
                RemainingStackCount = updatedCount,
                InstanceValue = updatedCount,
                UpdatedGold = slotIndex == 0 ? updatedCount : 0,
                MainVirtualCountChanged = true,
            };
        }

        private static InventoryMutationResult BuildConsumedItemResult(
            InventoryListType listType,
            short slotIndex,
            int itemId,
            InventoryDeleteResult consumed)
        {
            return new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = itemId,
                RemainingStackCount = consumed != null ? consumed.RemainingCount : 0,
                InstanceValue = consumed != null ? consumed.RemainingCount : 0,
                RequestedCount = 1,
                AppliedCount = 1,
            };
        }
    }
}
