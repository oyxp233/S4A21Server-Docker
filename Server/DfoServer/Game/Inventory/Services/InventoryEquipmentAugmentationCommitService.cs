using System.Collections.Generic;
using DfoServer.Game.ExpertJob;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryEquipmentAugmentationCommitService
    {
        internal static bool TryCommitEnchant(
            InventoryLease lease,
            EnchantByBeadCommand command,
            bool petTarget,
            out EnchantByBeadResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            var applied = false;
            EnchantByBeadResult value = null;
            var committed = TryCommit(lease, petTarget ? "enchant-pet-by-bead" : "enchant-by-bead", () =>
            {
                applied = petTarget
                    ? PetCreatureEnchantService.TryEnchantByBead(lease.Inventory, command, out value)
                    : InventoryEquipmentMutationService.TryEnchantByBead(lease.Inventory, command, out value);
                return applied;
            });
            result = value;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }

        internal static bool TryCommitEquipmentSocket(InventoryLease lease, short slot, int itemId, short materialSlot, out EquipmentSocketMutationResult result, out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            var applied = false;
            EquipmentSocketMutationResult value = null;
            var committed = TryCommit(lease, "open-equipment-socket", () => applied = InventoryEquipmentMutationService.TryOpenEquipmentSocket(lease.Inventory, slot, itemId, materialSlot, out value));
            result = value;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }

        internal static bool TryCommitEquipmentEmblems(InventoryLease lease, short slot, int itemId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out EquipmentEmblemMutationResult result, out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            var applied = false;
            EquipmentEmblemMutationResult value = null;
            var committed = TryCommit(lease, "attach-equipment-emblems", () => applied = InventoryEquipmentMutationService.TrySetEquipmentEmblems(lease.Inventory, slot, itemId, emblems, out value));
            result = value;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }

        internal static bool TryCommitAvatarSocket(InventoryLease lease, short slot, int itemId, short materialSlot, out AvatarSocketMutationResult result, out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            var applied = false;
            AvatarSocketMutationResult value = null;
            var committed = TryCommit(lease, "open-avatar-socket", () => applied = InventoryEquipmentMutationService.TryOpenAvatarSocket(lease.Inventory, slot, itemId, materialSlot, out value));
            result = value;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }

        internal static bool TryCommitAvatarEmblems(InventoryLease lease, short slot, int itemId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out AvatarEmblemMutationResult result, out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            var applied = false;
            AvatarEmblemMutationResult value = null;
            var committed = TryCommit(lease, "attach-avatar-emblems", () => applied = InventoryEquipmentMutationService.TrySetAvatarEmblems(lease.Inventory, slot, itemId, emblems, out value));
            result = value;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }

        internal static bool TryCommitGuardianGem(InventoryLease lease, GuardianGemUseCommand command, out GuardianGemUseResult result, out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            var applied = false;
            GuardianGemUseResult value = null;
            var committed = TryCommit(lease, "use-guardian-gem", () => applied = InventoryEquipmentMutationService.TryUseGuardianGem(lease.Inventory, command, out value));
            result = value;
            persistenceFailed = applied && !committed;
            return applied && committed;
        }

        private static bool TryCommit(InventoryLease lease, string operation, System.Func<bool> apply)
        {
            if (lease?.Inventory == null || apply == null)
                return false;
            return OnlineInventoryMutationCommitCoordinator.TryCommit(lease, operation, (connection, transaction) => apply());
        }
    }
}
