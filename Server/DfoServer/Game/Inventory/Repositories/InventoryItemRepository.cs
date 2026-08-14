using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryItemRepository
    {
        private const int EquippedListType = (int)InventoryListType.Equipment;

        internal static List<InventoryItem> LoadEquippedItems(
            SqliteConnection connection,
            int characterId)
        {
            return LoadCharacterItems(connection, characterId, InventoryListType.Equipment);
        }

        internal static List<InventoryItem> LoadEquippedItemsByAccount(
            SqliteConnection connection,
            int accountId)
        {
            var items = new List<InventoryItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT i.item_uid,
       'character' AS owner_scope,
       i.character_id AS owner_id,
       i.character_id,
       i.list_type,
       i.slot_index,
       i.item_core,
       i.created_at,
       i.updated_at
FROM character_inventory_items i
JOIN characters c ON c.character_id = i.character_id
WHERE c.account_id = @accountId
  AND i.list_type = @listType
ORDER BY i.character_id, i.slot_index;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@listType", EquippedListType);
                ReadItems(command, items);
            }

            return items;
        }

        internal static List<InventoryItem> LoadCharacterItems(
            SqliteConnection connection,
            int characterId,
            InventoryListType listType)
        {
            var items = new List<InventoryItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = CharacterSelect + @"
WHERE character_id = @characterId
  AND list_type = @listType
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                ReadItems(command, items);
            }

            return items;
        }

        internal static List<InventoryItem> LoadCharacterItems(
            SqliteConnection connection,
            int characterId)
        {
            var items = new List<InventoryItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = CharacterSelect + @"
WHERE character_id = @characterId
ORDER BY list_type, slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                ReadItems(command, items);
            }

            return items;
        }

        internal static List<InventoryItem> LoadAccountCargoItems(
            SqliteConnection connection,
            int accountId)
        {
            var items = new List<InventoryItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = AccountSelect + @"
WHERE account_id = @accountId
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue(
                    "@accountCargoListType",
                    (int)InventoryListType.AccountCargo);
                ReadItems(command, items);
            }

            return items;
        }

        internal static InventoryItem LoadCharacterSlot(
            SqliteConnection connection,
            int characterId,
            InventoryListType listType,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = CharacterSelect + @"
WHERE character_id = @characterId
  AND list_type = @listType
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? InvenItemCodec.ReadItem(reader) : null;
            }
        }

        internal static InventoryItem LoadAccountCargoSlot(
            SqliteConnection connection,
            int accountId,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = AccountSelect + @"
WHERE account_id = @accountId
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue(
                    "@accountCargoListType",
                    (int)InventoryListType.AccountCargo);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? InvenItemCodec.ReadItem(reader) : null;
            }
        }

        internal static long UpsertCharacterSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType listType,
            short slotIndex,
            ItemCore core)
        {
            if (core == null)
                throw new ArgumentNullException(nameof(core));

            var itemCore = core.ToBytes();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_inventory_items
SET item_core = @itemCore,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @characterId
  AND list_type = @listType
  AND slot_index = @slotIndex;";
                AddCharacterSlotParameters(
                    command,
                    characterId,
                    listType,
                    slotIndex,
                    itemCore);
                if (command.ExecuteNonQuery() > 0)
                {
                    return LoadCharacterSlotUid(
                        connection,
                        transaction,
                        characterId,
                        listType,
                        slotIndex);
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_inventory_items (
    character_id, list_type, slot_index, item_core, created_at, updated_at
) VALUES (
    @characterId, @listType, @slotIndex, @itemCore, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
);";
                AddCharacterSlotParameters(
                    command,
                    characterId,
                    listType,
                    slotIndex,
                    itemCore);
                command.ExecuteNonQuery();
            }

            return LoadLastInsertRowId(connection, transaction);
        }

        internal static void DeleteCharacterSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType listType,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM character_inventory_items
WHERE character_id = @characterId
  AND list_type = @listType
  AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.ExecuteNonQuery();
            }
        }

        internal static long UpsertAccountCargoSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            short slotIndex,
            ItemCore core)
        {
            if (core == null)
                throw new ArgumentNullException(nameof(core));

            _ = characterId;
            var itemCore = core.ToBytes();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE account_inventory_items
SET item_core = @itemCore,
    updated_at = CURRENT_TIMESTAMP
WHERE account_id = @accountId
  AND slot_index = @slotIndex;";
                AddAccountSlotParameters(command, accountId, slotIndex, itemCore);
                if (command.ExecuteNonQuery() > 0)
                    return LoadAccountCargoSlotUid(connection, transaction, accountId, slotIndex);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO account_inventory_items (
    account_id, slot_index, item_core, created_at, updated_at
) VALUES (
    @accountId, @slotIndex, @itemCore, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
);";
                AddAccountSlotParameters(command, accountId, slotIndex, itemCore);
                command.ExecuteNonQuery();
            }

            return LoadLastInsertRowId(connection, transaction);
        }

        internal static void DeleteAccountCargoSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM account_inventory_items
WHERE account_id = @accountId
  AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.ExecuteNonQuery();
            }
        }

        internal static Dictionary<int, InventoryItem> LoadEquippedItemMap(
            SqliteConnection connection,
            int characterId)
        {
            var result = new Dictionary<int, InventoryItem>();
            foreach (var item in LoadEquippedItems(connection, characterId))
                result[item.SlotIndex] = item;
            return result;
        }

        private const string CharacterSelect = @"
SELECT item_uid,
       'character' AS owner_scope,
       character_id AS owner_id,
       character_id,
       list_type,
       slot_index,
       item_core,
       created_at,
       updated_at
FROM character_inventory_items
";

        private const string AccountSelect = @"
SELECT item_uid,
       'account' AS owner_scope,
       account_id AS owner_id,
       NULL AS character_id,
       @accountCargoListType AS list_type,
       slot_index,
       item_core,
       created_at,
       updated_at
FROM account_inventory_items
";

        private static void ReadItems(
            SqliteCommand command,
            ICollection<InventoryItem> items)
        {
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    items.Add(InvenItemCodec.ReadItem(reader));
            }
        }

        private static void AddCharacterSlotParameters(
            SqliteCommand command,
            int characterId,
            InventoryListType listType,
            short slotIndex,
            byte[] itemCore)
        {
            command.Parameters.AddWithValue("@characterId", characterId);
            command.Parameters.AddWithValue("@listType", (int)listType);
            command.Parameters.AddWithValue("@slotIndex", slotIndex);
            command.Parameters.AddWithValue("@itemCore", itemCore);
        }

        private static void AddAccountSlotParameters(
            SqliteCommand command,
            int accountId,
            short slotIndex,
            byte[] itemCore)
        {
            command.Parameters.AddWithValue("@accountId", accountId);
            command.Parameters.AddWithValue("@slotIndex", slotIndex);
            command.Parameters.AddWithValue("@itemCore", itemCore);
        }

        private static long LoadCharacterSlotUid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType listType,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid
FROM character_inventory_items
WHERE character_id = @characterId
  AND list_type = @listType
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static long LoadAccountCargoSlotUid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid
FROM account_inventory_items
WHERE account_id = @accountId
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static long LoadLastInsertRowId(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT last_insert_rowid();";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }
    }
}
