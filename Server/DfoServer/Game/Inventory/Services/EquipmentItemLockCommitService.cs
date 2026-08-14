namespace DfoServer.Game.Inventory
{
    internal static class EquipmentItemLockCommitService
    {
        internal static bool TryLock(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            out EquipmentItemLockResult result)
        {
            EquipmentItemLockResult mutationResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "equipment-item-lock",
                (connection, transaction) =>
                {
                    var equipmentLockId =
                        InventoryEquipmentLockTableService.AllocateLockId(
                            connection,
                            transaction,
                            lease.CharacterId,
                            lease.Inventory);
                    if (!InventoryLockService.TryLockEquipmentItem(
                            lease.Inventory,
                            listType,
                            slotIndex,
                            equipmentLockId,
                            out mutationResult))
                    {
                        return false;
                    }

                    if (!InventoryEquipmentLockTableService.UpsertLock(
                            connection,
                            transaction,
                            lease.CharacterId,
                            mutationResult.EquipmentLockId,
                            mutationResult.ListType,
                            mutationResult.SlotIndex,
                            state: 1,
                            remainingSeconds: null))
                    {
                        MarkPersistenceFailed(mutationResult);
                        return false;
                    }

                    lease.Inventory.EquipmentLocks.Attach(new EquipmentItemLock
                    {
                        EquipmentLockId = mutationResult.EquipmentLockId,
                        State = 1,
                        RemainingSeconds = 0,
                    });
                    return true;
                });
            if (!committed && mutationResult?.Success == true)
                MarkPersistenceFailed(mutationResult);
            result = mutationResult;
            return committed;
        }

        internal static bool TryUnlock(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            out EquipmentItemLockResult result)
        {
            EquipmentItemLockResult mutationResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "equipment-item-unlock",
                (connection, transaction) =>
                {
                    if (!InventoryLockService.TryUnlockEquipmentItem(
                            lease.Inventory,
                            listType,
                            slotIndex,
                            out mutationResult))
                    {
                        return false;
                    }

                    if (!InventoryEquipmentLockTableService.DeleteLock(
                            connection,
                            transaction,
                            lease.CharacterId,
                            mutationResult.EquipmentLockId))
                    {
                        MarkPersistenceFailed(mutationResult);
                        return false;
                    }

                    lease.Inventory.EquipmentLocks.Remove(
                        mutationResult.EquipmentLockId);
                    return true;
                });
            if (!committed && mutationResult?.Success == true)
                MarkPersistenceFailed(mutationResult);
            result = mutationResult;
            return committed;
        }

        internal static bool TryCancelUnlock(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            out EquipmentItemLockResult result)
        {
            EquipmentItemLockResult mutationResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "equipment-item-unlock-cancel",
                (connection, transaction) =>
                {
                    if (!InventoryLockService.TryCancelEquipmentItemUnlock(
                            lease.Inventory,
                            listType,
                            slotIndex,
                            out mutationResult))
                    {
                        return false;
                    }

                    if (!InventoryEquipmentLockTableService.UpsertLock(
                            connection,
                            transaction,
                            lease.CharacterId,
                            mutationResult.EquipmentLockId,
                            mutationResult.ListType,
                            mutationResult.SlotIndex,
                            state: 1,
                            remainingSeconds: null))
                    {
                        MarkPersistenceFailed(mutationResult);
                        return false;
                    }

                    lease.Inventory.EquipmentLocks.Attach(new EquipmentItemLock
                    {
                        EquipmentLockId = mutationResult.EquipmentLockId,
                        State = 1,
                        RemainingSeconds = 0,
                    });
                    return true;
                });
            if (!committed && mutationResult?.Success == true)
                MarkPersistenceFailed(mutationResult);
            result = mutationResult;
            return committed;
        }

        private static void MarkPersistenceFailed(
            EquipmentItemLockResult result)
        {
            if (result == null)
                return;

            result.Success = false;
            result.ErrorCode = 19;
        }
    }
}
