using System;
using DfoServer.Game.ExpertJob;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureEnchantService
    {
        internal static bool TryEnchantByBead(
            InventoryService inventory,
            EnchantByBeadCommand command,
            out EnchantByBeadResult result)
        {
            if (command == null)
            {
                result = EnchantByBeadResult.Error(null, EnchantByBeadResult.ErrorInvalidBead);
                return false;
            }

            result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidBead);
            if (inventory == null
                || command.BeadListType != InventoryListType.Main
                || command.TargetListType != InventoryListType.Pet)
            {
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorUnsupported);
                return false;
            }

            var bead = inventory.GetItem(InventoryListType.Main, command.BeadSlotIndex);
            if (bead == null || bead.Count <= 0)
            {
                FileLogger.Log($"[PetEnchantByBead] reject: invalid bead slot={command.BeadSlotIndex} item=0x{bead?.ItemId ?? 0:X8}");
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidBead);
                return false;
            }

            var target = inventory.GetItem(InventoryListType.Pet, command.TargetSlotIndex);
            if (target == null
                || target.ItemKind != ItemCore.KindCreature
                || target.Value <= 0
                || !IsCreatureItem(target.ItemId))
            {
                FileLogger.Log($"[PetEnchantByBead] reject: invalid target slot={command.TargetSlotIndex} item=0x{target?.ItemId ?? 0:X8} kind={target?.ItemKind ?? 0} key={target?.Value ?? 0}");
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
                return false;
            }

            var enchantUpgradeCount = bead.EnchantUpgradeCount;
            if (!ItemMetadataResolver.TryValidatePetEnchantByBeadTarget(
                    bead.ItemId,
                    target.ItemId,
                    enchantUpgradeCount,
                    out var enchantCardItemId,
                    out var rejectReason))
            {
                var errorCode = rejectReason != null && rejectReason.StartsWith("target", StringComparison.Ordinal)
                    ? EnchantByBeadResult.ErrorInvalidTarget
                    : EnchantByBeadResult.ErrorUnsupported;
                FileLogger.Log($"[PetEnchantByBead] reject: bead=0x{bead.ItemId:X8} target=0x{target.ItemId:X8} upgrade={enchantUpgradeCount} reason={rejectReason}");
                result = EnchantByBeadResult.Error(command, errorCode);
                return false;
            }

            var updatedTarget = target.Copy();
            updatedTarget.EnchantCardId = enchantCardItemId;
            updatedTarget.EnchantUpgradeCount = enchantUpgradeCount;
            if (!inventory.SetItem(InventoryListType.Pet, command.TargetSlotIndex, updatedTarget))
            {
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
                return false;
            }

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    command.BeadSlotIndex,
                    1,
                    out var delete))
            {
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidBead);
                return false;
            }

            result = EnchantByBeadResult.Ok(command, Math.Max(0, delete.RemainingCount), enchantCardItemId);
            FileLogger.Log($"[PetEnchantByBead] ok cid={inventory.CharacterId} bead=0x{bead.ItemId:X8}@{command.BeadSlotIndex} target=0x{target.ItemId:X8}@{command.TargetSlotIndex} key={target.Value} enchant=0x{enchantCardItemId:X8} upgrade={enchantUpgradeCount} beadLeft={delete.RemainingCount}");
            return true;
        }

        private static bool IsCreatureItem(int itemTemplateId)
        {
            return itemTemplateId > 0
                && ItemMetadataResolver.TryResolveItemKind(itemTemplateId, out var itemKind)
                && itemKind == ItemCore.KindCreature;
        }
    }
}
