using System;
using System.Collections.Generic;
using DfoServer.Game.TitleBook;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryEquipmentLockTableService
    {
        public static byte AllocateLockId(int characterId, InventoryService inventory)
        {
            try
            {
                using (var connection = OpenConnection(inventory))
                    return AllocateLockId(
                        connection,
                        transaction: null,
                        characterId,
                        inventory);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryLock] allocate lock id failed cid={characterId}: {ex.Message}");
                return 0;
            }

        }

        internal static byte AllocateLockId(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryService inventory)
        {
            if (connection == null || characterId <= 0)
                return 0;

            var used = new HashSet<int>();
            CollectOnlineLockIds(inventory, used);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT equipment_lock_id
FROM character_item_locks
WHERE character_id = @cid
  AND equipment_lock_id > 0;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        used.Add(reader.GetInt32(0));
                }
            }

            for (var lockId = 1; lockId <= 255; lockId++)
                if (!used.Contains(lockId))
                    return (byte)lockId;

            return 0;
        }

        public static bool UpsertLock(
            int characterId,
            byte equipmentLockId,
            InventoryListType listType,
            short slotIndex,
            byte state,
            int? remainingSeconds,
            InventoryService inventory = null)
        {
            if (characterId <= 0 || equipmentLockId == 0)
                return false;

            try
            {
                using (var connection = OpenConnection(inventory))
                    return UpsertLock(
                        connection,
                        transaction: null,
                        characterId,
                        equipmentLockId,
                        listType,
                        slotIndex,
                        state,
                        remainingSeconds);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryLock] upsert lock failed cid={characterId} lockId={equipmentLockId}: {ex.Message}");
                return false;
            }
        }

        internal static bool UpsertLock(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte equipmentLockId,
            InventoryListType listType,
            short slotIndex,
            byte state,
            int? remainingSeconds)
        {
            if (connection == null || characterId <= 0 || equipmentLockId == 0)
                return false;

            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO character_item_locks (
    character_id, equipment_lock_id, inventory_list_type, slot, state, remaining_seconds)
VALUES (@cid, @lockId, @listType, @slot, @state, @remainingSeconds)
ON CONFLICT(character_id, equipment_lock_id)
DO UPDATE SET
    inventory_list_type = excluded.inventory_list_type,
    slot = excluded.slot,
    state = excluded.state,
    remaining_seconds = excluded.remaining_seconds;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@lockId", (int)equipmentLockId);
                    command.Parameters.AddWithValue("@listType", (int)listType);
                    command.Parameters.AddWithValue("@slot", (int)slotIndex);
                    command.Parameters.AddWithValue("@state", (int)state);
                    command.Parameters.AddWithValue("@remainingSeconds", remainingSeconds.HasValue ? (object)remainingSeconds.Value : DBNull.Value);
                    command.ExecuteNonQuery();
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryLock] upsert lock failed cid={characterId} lockId={equipmentLockId}: {ex.Message}");
                return false;
            }
        }

        public static bool DeleteLock(
            int characterId,
            byte equipmentLockId,
            InventoryService inventory = null)
        {
            if (characterId <= 0 || equipmentLockId == 0)
                return false;

            try
            {
                using (var connection = OpenConnection(inventory))
                    return DeleteLock(
                        connection,
                        transaction: null,
                        characterId,
                        equipmentLockId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryLock] delete lock failed cid={characterId} lockId={equipmentLockId}: {ex.Message}");
                return false;
            }
        }

        internal static bool DeleteLock(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte equipmentLockId)
        {
            if (connection == null || characterId <= 0 || equipmentLockId == 0)
                return false;

            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
DELETE FROM character_item_locks
WHERE character_id = @cid
  AND equipment_lock_id = @lockId;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@lockId", (int)equipmentLockId);
                    command.ExecuteNonQuery();
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryLock] delete lock failed cid={characterId} lockId={equipmentLockId}: {ex.Message}");
                return false;
            }
        }

        private static SqliteConnection OpenConnection(
            InventoryService inventory)
        {
            return (inventory?.Database ?? GameDatabase.CreateDefault())
                .OpenConnection();
        }

        private static void CollectOnlineLockIds(InventoryService inventory, HashSet<int> used)
        {
            if (inventory == null || used == null)
                return;

            foreach (var itemLock in inventory.EquipmentLocks.Locks)
            {
                if (itemLock.EquipmentLockId != 0)
                    used.Add(itemLock.EquipmentLockId);
            }

            foreach (var listType in EnumerateEquipmentLockListTypes())
            {
                foreach (var pair in inventory.GetItems(listType))
                {
                    if (pair.Value.EquipmentLockId != 0)
                        used.Add(pair.Value.EquipmentLockId);
                }
            }

            foreach (var pair in inventory.TitleBook.GetItems())
            {
                if (pair.Value.EquipmentLockId != 0)
                    used.Add(pair.Value.EquipmentLockId);
            }
        }

        private static IEnumerable<InventoryListType> EnumerateEquipmentLockListTypes()
        {
            yield return InventoryListType.Main;
            yield return InventoryListType.PersonalCargo;
            yield return InventoryListType.Equipment;
            yield return InventoryListType.Avatar;
            yield return InventoryListType.Pet;
        }
    }
}
