using System;
using System.Linq;
using DfoServer.Game.ItemUpgrade;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    internal static class ChronicleGrowthService
    {
        internal static bool TryGrow(
            InventoryService inventory,
            ChronicleGrowthCommand command,
            out ChronicleGrowthResult result)
        {
            result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInvalidRequest);
            if (inventory == null
                || command == null
                || command.TicketSlotIndex < 0
                || command.TargetSlotIndex < 0
                || command.Materials.Count != 1)
                return false;

            var requestedMaterial = command.Materials[0];
            if (command.TicketSlotIndex == command.TargetSlotIndex
                || requestedMaterial.SlotIndex == command.TicketSlotIndex
                || requestedMaterial.SlotIndex == command.TargetSlotIndex
                || requestedMaterial.ItemTemplateId != ChronicleGrowthCostCalculator.FragmentItemTemplateId)
            {
                result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInsufficientMaterial);
                return false;
            }

            var ticket = inventory.GetItem(InventoryListType.Main, command.TicketSlotIndex);
            if (ticket == null
                || ticket.ItemId != command.TicketItemTemplateId
                || ticket.Count <= 0
                || !InventoryStackRuleService.IsStackable(ticket)
                || !TryResolveTicket(ticket.ItemId, out var ticketFile, out var growth))
            {
                result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInsufficientMaterial);
                return false;
            }

            var target = inventory.GetItem(InventoryListType.Main, command.TargetSlotIndex);
            if (target == null
                || target.ItemId != command.TargetItemTemplateId
                || target.ItemKind != ItemCore.KindEquipment
                || !ItemMetadataResolver.TryLoadEquipmentFile(target.ItemId, out var equipment))
            {
                result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInvalidTarget);
                return false;
            }

            if (IsItemLocked(inventory, target))
            {
                result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorLocked);
                return false;
            }

            var currentLevel = equipment.MinimumLevel + target.EmancipateEquipmentLevel;
            if (!AllowsTarget(growth, ticketFile, equipment, target, currentLevel))
            {
                result = ChronicleGrowthResult.Error(
                    command,
                    currentLevel >= ResolveMaximumLevel(growth)
                        ? ChronicleGrowthResult.ErrorMaximumLevel
                        : ChronicleGrowthResult.ErrorRestricted);
                return false;
            }

            var equipmentType = EquipmentTypeInfo.ParseOrUnknown(equipment.EquipmentType);
            var hasAmplification = (target.AmplifyType & 0x0F) != 0;
            var reinforceLevel = hasAmplification ? 0 : target.Upgrade;
            var amplifyLevel = hasAmplification ? target.Upgrade : 0;
            var genuineGrade = ChronicleGrowthCostCalculator.ResolveCostGenuineGrade(target.GenuineUpgrade);
            var requiredFragments = ChronicleGrowthCostCalculator.Calculate(
                currentLevel,
                equipmentType,
                reinforceLevel,
                amplifyLevel,
                genuineGrade);

            var fragments = inventory.GetItem(InventoryListType.Main, requestedMaterial.SlotIndex);
            if (fragments == null
                || fragments.ItemId != requestedMaterial.ItemTemplateId
                || !InventoryStackRuleService.IsStackable(fragments)
                || fragments.Count < requiredFragments)
            {
                result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInsufficientMaterial);
                return false;
            }

            var successWeight = ResolveSuccessWeight(growth, currentLevel);
            if (successWeight < 0)
            {
                result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorRestricted);
                return false;
            }

            var roll = Infrastructure.ServerRandom.Next(100000);
            var succeeded = roll < Math.Min(100000, successWeight);
            var maximumLevel = ResolveMaximumLevel(growth);
            var newLevel = succeeded
                ? Math.Min(maximumLevel, currentLevel + growth.UpgradeLevel)
                : currentLevel;

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    command.TicketSlotIndex,
                    1,
                    out var ticketDelete)
                || !InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    requestedMaterial.SlotIndex,
                    requiredFragments,
                    out var fragmentDelete))
            {
                result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInsufficientMaterial);
                return false;
            }

            if (succeeded)
            {
                var updated = target.Copy();
                updated.EmancipateEquipmentLevel = checked((byte)(newLevel - equipment.MinimumLevel));
                if (!inventory.SetItem(InventoryListType.Main, command.TargetSlotIndex, updated))
                {
                    result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInvalidTarget);
                    return false;
                }
            }

            result = new ChronicleGrowthResult
            {
                Command = command,
                ErrorCode = 0,
                GrowthSucceeded = succeeded,
                OldLevel = currentLevel,
                NewLevel = newLevel,
                RequiredFragmentCount = requiredFragments,
                SuccessWeight = successWeight,
                ProbabilityRoll = roll,
            };
            result.Consumptions.Add(new ChronicleGrowthConsumption
            {
                ListType = InventoryListType.Main,
                SlotIndex = command.TicketSlotIndex,
                ItemTemplateId = ticket.ItemId,
                ConsumedCount = 1,
                RemainingCount = ticketDelete.RemainingCount,
            });
            result.Consumptions.Add(new ChronicleGrowthConsumption
            {
                ListType = InventoryListType.Main,
                SlotIndex = requestedMaterial.SlotIndex,
                ItemTemplateId = fragments.ItemId,
                ConsumedCount = requiredFragments,
                RemainingCount = fragmentDelete.RemainingCount,
            });
            return true;
        }

        private static bool TryResolveTicket(
            int itemTemplateId,
            out StackableItemFile ticket,
            out EquipmentLevelEmancipateInfo growth)
        {
            growth = null;
            if (!ItemMetadataResolver.TryLoadStackableFile(itemTemplateId, out ticket)
                || ticket.EmancipateTicket < 0
                || ticket.EquipmentLevelEmancipate == null
                || ticket.EquipmentLevelEmancipate.Condition == null
                || ticket.EquipmentLevelEmancipate.UpgradeLevel <= 0)
                return false;

            growth = ticket.EquipmentLevelEmancipate;
            return true;
        }

        private static bool AllowsTarget(
            EquipmentLevelEmancipateInfo growth,
            StackableItemFile ticket,
            EquipmentFile equipment,
            ItemCore target,
            int currentLevel)
        {
            if (growth == null
                || equipment == null
                || target == null
                || growth.IgnoreIndexes.Contains(target.ItemId)
                || currentLevel < growth.Condition.MinimumLevel
                || currentLevel >= ResolveMaximumLevel(growth)
                || (growth.Condition.Rarities.Count > 0
                    && !growth.Condition.Rarities.Contains(equipment.Rarity)))
                return false;

            var amplified = (target.AmplifyType & 0x0F) != 0;
            if (!amplified
                && ticket.EmancipateGradeMax >= 0
                && target.Upgrade > ticket.EmancipateGradeMax)
                return false;
            if (amplified
                && ticket.EmancipateAmplifyMax >= 0
                && target.Upgrade > ticket.EmancipateAmplifyMax)
                return false;

            var genuineGrade = ChronicleGrowthCostCalculator.ResolveCostGenuineGrade(target.GenuineUpgrade);
            return ticket.EmancipateGenuineGradeMax < 0
                || genuineGrade <= ticket.EmancipateGenuineGradeMax;
        }

        private static int ResolveMaximumLevel(EquipmentLevelEmancipateInfo growth)
            => growth?.Condition?.MaximumLevel > 0
                ? growth.Condition.MaximumLevel
                : 86;

        private static int ResolveSuccessWeight(
            EquipmentLevelEmancipateInfo growth,
            int currentLevel)
        {
            if (growth?.Probabilities == null || growth.Probabilities.Count == 0)
                return -1;

            foreach (var entry in growth.Probabilities.OrderBy(entry => entry.MaximumLevel))
            {
                if (currentLevel <= entry.MaximumLevel)
                    return entry.Weight;
            }

            return -1;
        }

        private static bool IsItemLocked(InventoryService inventory, ItemCore core)
        {
            return core.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }
    }
}
