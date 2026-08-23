using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryEmancipateService
    {
        internal static bool TryConvert(
            InventoryService inventory,
            ChronicleGrowthCommand command,
            out ChronicleGrowthResult result)
        {
            result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInvalidTarget);
            if (inventory == null || command == null)
                return false;

            var ticket = inventory.GetItem(InventoryListType.Main, command.TicketSlotIndex);
            var target = inventory.GetItem(InventoryListType.Main, command.TargetSlotIndex);
            if (ticket == null || target == null
                || ticket.ItemId != command.TicketItemTemplateId
                || target.ItemId != command.TargetItemTemplateId
                || ticket.Count < 1
                || InventoryItemLifecycleService.IsExpired(
                    ticket,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                || target.ItemKind != ItemCore.KindEquipment
                || command.TicketSlotIndex == command.TargetSlotIndex
                || !ItemMetadataResolver.TryLoadStackableFile(ticket.ItemId, out var ticketFile)
                || ticketFile.EmancipateTicket < 0
                || !ItemMetadataResolver.TryLoadEquipmentFile(target.ItemId, out var sourceFile)
                || sourceFile.Emancipate == null
                || sourceFile.Emancipate.Type != ticketFile.EmancipateTicket
                || sourceFile.Emancipate.Inputs.Count == 0
                || sourceFile.Emancipate.Outputs.Count != 1)
                return false;

            var output = sourceFile.Emancipate.Outputs[0];
            if (output.ItemId <= 0 || output.Count != 1)
                return false;

            var requirements = sourceFile.Emancipate.Inputs
                .GroupBy(x => x.ItemId)
                .Select(x => new InventoryMaterialRequirement(x.Key, x.Sum(y => y.Count)))
                .ToList();
            requirements.Add(new InventoryMaterialRequirement(ticket.ItemId, 1));
            if (!InventoryMaterialConsumptionService.HasEnough(inventory, requirements))
            {
                result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInsufficientMaterial);
                return false;
            }

            if (!InventoryCreateService.TryCreateCore(output.ItemId, ItemCreateReason.Unknown, 1, out var converted))
                return false;

            CopyConvertedAttributes(target, converted, ticketFile);
            var consumed = new List<InventoryMaterialConsumptionEntry>();
            foreach (var requirement in requirements)
            {
                if (!InventoryMaterialConsumptionService.TryConsume(
                        inventory,
                        new[] { requirement },
                        consumed))
                    return false;
            }

            if (!inventory.SetItem(InventoryListType.Main, command.TargetSlotIndex, converted))
                return false;

            result = new ChronicleGrowthResult
            {
                Command = command,
                ErrorCode = 0,
                GrowthSucceeded = true,
                OldLevel = 0,
                NewLevel = 0,
            };
            foreach (var entry in consumed)
            {
                result.Consumptions.Add(new ChronicleGrowthConsumption
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = entry.SlotIndex,
                    ItemTemplateId = entry.ItemTemplateId,
                    ConsumedCount = entry.Count,
                    RemainingCount = -1,
                });
            }
            return true;
        }

        private static void CopyConvertedAttributes(ItemCore source, ItemCore target, StackableItemFile ticket)
        {
            // Conversion creates a new core, but the source equipment's
            // sealed/unsealed state is an instance state and must survive it.
            target.SealFlag = source.SealFlag;

            // Only the relic/heritage conversion tickets whose PVF text
            // explicitly says "cannot be traded" become character-bound.
            // Other conversions keep the output equipment's PVF default.
            if (RequiresCharacterBinding(ticket?.EmancipateTicket ?? -1))
                target.TradeRestriction = 1;
            if (ticket == null)
                return;

            // These PVF conversion tickets explicitly preserve the source
            // upgrade, amplification, forging, enchantment and purified
            // dimensions. They do not declare *_max caps.
            if (PreservesEquipmentAttributes(ticket.EmancipateTicket))
            {
                CopyRetainedAttributes(source, target);
                return;
            }

            // The conversion ticket owns the retention limits. A negative
            // limit means the corresponding equipment state is discarded.
            if (ticket.EmancipateGradeMax >= 0)
                target.Upgrade = (byte)Math.Min(ticket.EmancipateGradeMax, (int)source.Upgrade);
            if (ticket.EmancipateAmplifyMax >= 0)
            {
                target.AmplifyType = source.AmplifyType;
                target.AmplifyValue = (ushort)Math.Min(ticket.EmancipateAmplifyMax, (int)source.AmplifyValue);
            }
            if (ticket.EmancipateGenuineGradeMax >= 0)
                target.GenuineUpgrade = (byte)Math.Min(ticket.EmancipateGenuineGradeMax, (int)source.GenuineUpgrade);

            // A ticket that declares a retention cap also retains the
            // purified/enchanted state; otherwise those fields stay at the
            // newly-created output defaults.
            if (ticket.EmancipateGradeMax >= 0
                || ticket.EmancipateAmplifyMax >= 0
                || ticket.EmancipateGenuineGradeMax >= 0)
            {
                target.EnchantCardId = source.EnchantCardId;
                target.EnchantUpgradeCount = source.EnchantUpgradeCount;
            }
        }

        private static bool PreservesEquipmentAttributes(int ticket)
        {
            // PVF evidence: itemupgrade_high (1), dragon weapon growth (3),
            // ancient legendary evolution (6), arena conversion (7), and
            // soul-truth conversion (19) all state that these attributes stay.
            return ticket == 1 || ticket == 3 || ticket == 6 || ticket == 7 || ticket == 19;
        }

        private static bool RequiresCharacterBinding(int ticket)
        {
            // PVF evidence: 10149520..10149529 (tickets 8..17),
            // 10149519 (18), and 10150749 (19) explicitly state that the
            // converted equipment cannot be traded.
            return ticket >= 8 && ticket <= 19;
        }

        private static void CopyRetainedAttributes(ItemCore source, ItemCore target)
        {
            target.Upgrade = source.Upgrade;
            target.GenuineUpgrade = source.GenuineUpgrade;
            target.EnchantCardId = source.EnchantCardId;
            target.EnchantUpgradeCount = source.EnchantUpgradeCount;
            target.AmplifyType = source.AmplifyType;
            target.AmplifyValue = source.AmplifyValue;
        }
    }
}
