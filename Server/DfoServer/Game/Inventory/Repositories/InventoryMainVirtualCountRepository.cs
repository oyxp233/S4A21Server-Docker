using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryMainVirtualCountRepository
    {
        internal static List<VirtualCountItem> LoadCurrencySlots(
            SqliteConnection connection,
            int characterId)
        {
            var result = new List<VirtualCountItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT slot_index, item_core
FROM character_inventory_items
WHERE character_id = @characterId
  AND list_type = 0
  AND slot_index IN (0, 1, 2)
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var slotIndex = Convert.ToInt16(reader.GetInt32(0));
                        if (!IsCurrencySlot(slotIndex))
                            continue;

                        var coreBytes = reader.IsDBNull(1) ? Array.Empty<byte>() : (byte[])reader[1];
                        if (coreBytes.Length < ItemCore.Size)
                            continue;

                        var core = ItemCore.FromBytes(coreBytes);
                        result.Add(new VirtualCountItem
                        {
                            SlotIndex = slotIndex,
                            ItemId = slotIndex,
                            Count = Math.Max(0, core.Count),
                        });
                    }
                }
            }

            return result;
        }

        internal static void UpsertCurrencySlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slotIndex,
            int count)
        {
            if (!IsCurrencySlot(slotIndex))
                throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "Main currency slot must be 0-2.");

            var normalizedCount = Math.Max(0, count);
            var itemCore = CreateCurrencyCore(slotIndex, normalizedCount).ToBytes();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_inventory_items
SET item_core = @itemCore,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @characterId
  AND list_type = 0
  AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue("@itemCore", itemCore);
                if (command.ExecuteNonQuery() > 0)
                    return;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_inventory_items (
    character_id, list_type, slot_index, item_core, created_at, updated_at
) VALUES (
    @characterId, 0, @slotIndex, @itemCore, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
);";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue("@itemCore", itemCore);
                command.ExecuteNonQuery();
            }
        }

        internal static int LoadCurrencyCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slotIndex)
        {
            if (!IsCurrencySlot(slotIndex))
                return 0;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_core
FROM character_inventory_items
WHERE character_id = @characterId
  AND list_type = 0
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return 0;

                var data = (byte[])value;
                if (data.Length < ItemCore.Size)
                    return 0;

                return Math.Max(0, ItemCore.FromBytes(data).Count);
            }
        }

        internal static int GrantCurrency(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slotIndex,
            int amount,
            int limit)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Grant amount must be >= 0.");

            var before = LoadCurrencyCount(connection, transaction, characterId, slotIndex);
            var normalizedLimit = Math.Max(0, limit);
            var target = before >= normalizedLimit
                ? before
                : (int)Math.Min(normalizedLimit, (long)before + amount);
            UpsertCurrencySlot(connection, transaction, characterId, slotIndex, target);
            return Math.Max(0, target - before);
        }

        internal static bool TrySpendCurrency(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slotIndex,
            int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Spend amount must be >= 0.");
            if (amount == 0)
                return true;

            var before = LoadCurrencyCount(connection, transaction, characterId, slotIndex);
            if (before < amount)
                return false;

            UpsertCurrencySlot(connection, transaction, characterId, slotIndex, before - amount);
            return true;
        }

        private static ItemCore CreateCurrencyCore(short slotIndex, int count)
        {
            return new ItemCore
            {
                ItemKind = ItemCore.KindSpecialMaterial,
                ItemId = slotIndex,
                Count = count,
            };
        }

        private static bool IsCurrencySlot(short slotIndex)
        {
            return slotIndex >= InventoryService.MainVirtualCurrencySlotStart
                && slotIndex <= InventoryService.MainVirtualCurrencySlotEnd;
        }
    }
}
