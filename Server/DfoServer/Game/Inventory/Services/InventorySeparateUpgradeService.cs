using DfoServer.Game.ItemUpgrade;
using System;

namespace DfoServer.Game.Inventory
{
    internal static class InventorySeparateUpgradeService
    {
        private const byte NoticeMinimumLevel = 5;

        internal static bool TryUpgrade(
            InventoryService inventory,
            SeparateUpgradeCommand command,
            SeparateUpgradeTable table,
            ItemMetadata metadata,
            out SeparateUpgradeResult result)
        {
            return TryUpgrade(
                inventory, command, table,
                metadata,
                () => Infrastructure.ServerRandom.Next(10000),
                out result);
        }

        internal static bool TryApplyTicket(
            InventoryService inventory,
            SeparateUpgradeCommand command,
            SeparateUpgradeTicketDefinition ticket,
            SeparateUpgradeTable table,
            ItemMetadata metadata,
            out SeparateUpgradeResult result)
        {
            return TryApplyTicket(
                inventory,
                command,
                ticket,
                table,
                metadata,
                () => Infrastructure.ServerRandom.Next(10000),
                out result);
        }

        internal static bool TryApplyTicket(
            InventoryService inventory,
            SeparateUpgradeCommand command,
            SeparateUpgradeTicketDefinition ticket,
            SeparateUpgradeTable table,
            ItemMetadata metadata,
            Func<int> rollProvider,
            out SeparateUpgradeResult result)
        {
            result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorInvalidTarget);
            if (inventory == null || command == null || ticket == null || table == null
                || metadata == null || rollProvider == null
                || (!ticket.IsFixed && !ticket.IsAdditional)
                || (command.TargetListType != InventoryListType.Main
                    && command.TargetListType != InventoryListType.Equipment)
                || (command.TargetListType == InventoryListType.Main
                    && command.TargetSlotIndex == command.MaterialSlotIndex))
            {
                return false;
            }

            var target = inventory.GetItem(command.TargetListType, command.TargetSlotIndex);
            if (target == null || target.ItemKind != ItemCore.KindEquipment
                || target.ItemId != command.TargetItemTemplateId || IsLocked(inventory, target))
            {
                return false;
            }

            if (!EquipmentTypeInfo.IsWeapon(EquipmentTypeInfo.ParseOrUnknown(metadata.EquipmentType)))
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorNotWeapon);
                return false;
            }

            var ticketItem = inventory.GetItem(InventoryListType.Main, command.MaterialSlotIndex);
            if (ticketItem == null || ticketItem.Count < 1
                || !InventoryStackRuleService.IsStackable(ticketItem))
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorInvalidMaterial);
                return false;
            }

            if (ticketItem.ItemId != ticket.ItemTemplateId)
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorInvalidMaterial);
                return false;
            }

            var oldLevel = target.GenuineUpgrade;
            var targetLevel = ticket.TargetLevel;
            if (ticket.IsAdditional)
            {
                var maximumSourceLevel = ticket.ApplyValue >= 0
                    ? Math.Min(ticket.ApplyValue, table.MaxLevel - ticket.TargetLevel)
                    : table.MaxLevel - ticket.TargetLevel;
                if (oldLevel > maximumSourceLevel)
                {
                    result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorMaxLevel);
                    return false;
                }
                targetLevel = checked((byte)(oldLevel + ticket.TargetLevel));
            }
            var roll = Math.Max(0, Math.Min(9999, rollProvider()));
            var succeeded = roll < ticket.SuccessWeight;
            var updatedTarget = target.Copy();
            if (succeeded)
                updatedTarget.GenuineUpgrade = targetLevel;
            var ticketItemId = ticketItem.ItemId;
            var ticketSnapshot = ticketItem.Copy();

            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    command.MaterialSlotIndex,
                    ticketItemId,
                    1,
                    out var deletion))
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorMaterialCommit);
                return false;
            }

            if (succeeded && !inventory.SetItem(command.TargetListType, command.TargetSlotIndex, updatedTarget))
            {
                inventory.SetItem(InventoryListType.Main, command.MaterialSlotIndex, ticketSnapshot);
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorMaterialCommit);
                return false;
            }

            result = new SeparateUpgradeResult
            {
                Command = command,
                UpgradeSucceeded = succeeded,
                OldLevel = oldLevel,
                NewLevel = succeeded ? targetLevel : oldLevel,
                TargetReinforceLevel = target.Upgrade,
                SuccessWeight = ticket.SuccessWeight,
                MaterialItemTemplateId = ticketItemId,
                MaterialCost = 1,
                MaterialRemainingCount = deletion.RemainingCount,
                NoticeRequired = succeeded && targetLevel >= NoticeMinimumLevel,
                TargetItemSnapshot = updatedTarget,
            };
            return true;
        }

        internal static bool TryUpgrade(
            InventoryService inventory,
            SeparateUpgradeCommand command,
            SeparateUpgradeTable table,
            ItemMetadata metadata,
            Func<int> rollProvider,
            out SeparateUpgradeResult result)
        {
            result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorInvalidTarget);
            if (inventory == null || command == null || table == null || metadata == null || rollProvider == null
                || (command.TargetListType != InventoryListType.Main
                    && command.TargetListType != InventoryListType.Equipment)
                || (command.TargetListType == InventoryListType.Main
                    && command.TargetSlotIndex == command.MaterialSlotIndex))
                return false;

            var target = inventory.GetItem(command.TargetListType, command.TargetSlotIndex);
            if (target == null || target.ItemKind != ItemCore.KindEquipment
                || target.ItemId != command.TargetItemTemplateId || IsLocked(inventory, target))
                return false;

            if (!EquipmentTypeInfo.IsWeapon(EquipmentTypeInfo.ParseOrUnknown(metadata.EquipmentType)))
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorNotWeapon);
                return false;
            }
            if (target.Durability != metadata.Durability)
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorDurability);
                return false;
            }

            var oldLevel = target.GenuineUpgrade;
            if (oldLevel >= table.MaxLevel)
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorMaxLevel);
                return false;
            }
            if (!table.TryGetLevel(oldLevel + 1, out var level)
                || metadata.Rarity < 0 || metadata.Rarity >= table.ItemWeightsByRarity.Count)
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorUnsupported);
                return false;
            }

            var equipmentGrade = metadata.Grade;
            if (!table.MaterialsByGrade.TryGetValue(equipmentGrade, out var materialRule))
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorUnsupported);
                return false;
            }
            var rawMaterialCost = materialRule.BaseCount
                * table.ItemWeightsByRarity[metadata.Rarity]
                * level.MaterialWeight + 0.0000001;
            if (double.IsNaN(rawMaterialCost) || double.IsInfinity(rawMaterialCost)
                || rawMaterialCost > int.MaxValue)
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorUnsupported);
                return false;
            }
            var materialCost = Math.Max(1, (int)rawMaterialCost);
            var material = inventory.GetItem(InventoryListType.Main, command.MaterialSlotIndex);
            if (material == null || material.ItemId != materialRule.ItemTemplateId
                || material.Count < materialCost || !InventoryStackRuleService.IsStackable(material))
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorInvalidMaterial);
                return false;
            }

            var roll = Math.Max(0, Math.Min(9999, rollProvider()));
            var succeeded = roll < level.SuccessWeight;
            var updatedTarget = target.Copy();
            if (succeeded)
                updatedTarget.GenuineUpgrade = checked((byte)(oldLevel + 1));
            var materialSnapshot = material.Copy();

            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    command.MaterialSlotIndex,
                    materialRule.ItemTemplateId,
                    materialCost,
                    out var deletion))
            {
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorMaterialCommit);
                return false;
            }
            if (succeeded && !inventory.SetItem(command.TargetListType, command.TargetSlotIndex, updatedTarget))
            {
                inventory.SetItem(InventoryListType.Main, command.MaterialSlotIndex, materialSnapshot);
                result = SeparateUpgradeResult.Error(command, SeparateUpgradeResult.ErrorMaterialCommit);
                return false;
            }

            result = new SeparateUpgradeResult
            {
                Command = command,
                UpgradeSucceeded = succeeded,
                OldLevel = oldLevel,
                NewLevel = succeeded ? (byte)(oldLevel + 1) : oldLevel,
                TargetReinforceLevel = target.Upgrade,
                SuccessWeight = level.SuccessWeight,
                MaterialItemTemplateId = materialRule.ItemTemplateId,
                MaterialCost = materialCost,
                MaterialRemainingCount = deletion.RemainingCount,
                NoticeRequired = (succeeded ? oldLevel + 1 : oldLevel) >= NoticeMinimumLevel,
                TargetItemSnapshot = updatedTarget,
            };
            return true;
        }

        private static bool IsLocked(InventoryService inventory, ItemCore item)
        {
            return item.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(item.EquipmentLockId, out var itemLock)
                && itemLock != null && itemLock.State != 0;
        }
    }
}
