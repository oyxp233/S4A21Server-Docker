using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryContainerStateRepository
    {
        internal static Dictionary<InventoryListType, ushort> LoadCharacterContainerState(
            SqliteConnection connection,
            int characterId)
        {
            var result = new Dictionary<InventoryListType, ushort>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT list_type, list_param16
FROM character_container_state
WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result[(InventoryListType)reader.GetInt32(0)] =
                            Convert.ToUInt16(reader.GetInt32(1), CultureInfo.InvariantCulture);
                    }
                }
            }

            return result;
        }

        internal static AccountCargoStateSnapshot LoadAccountCargoState(
            SqliteConnection connection,
            int accountId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT selection_key, value32, item_count
FROM account_cargo_state
WHERE account_id = @accountId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new AccountCargoStateSnapshot();

                    return new AccountCargoStateSnapshot
                    {
                        SelectionKey = Convert.ToUInt16(reader.GetInt32(0), CultureInfo.InvariantCulture),
                        Value32 = reader.GetInt32(1),
                        ItemCount = Convert.ToUInt16(reader.GetInt32(2), CultureInfo.InvariantCulture),
                    };
                }
            }
        }

        internal static void UpsertCharacterContainerState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType listType,
            ushort listParam16)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, @listType, @listParam16);";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@listParam16", listParam16);
                command.ExecuteNonQuery();
            }
        }

        internal static void UpsertAccountCargoState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            ushort selectionKey,
            int money,
            ushort itemCount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO account_cargo_state (account_id, selection_key, value32, item_count, updated_at)
VALUES (@accountId, @selectionKey, @money, @itemCount, CURRENT_TIMESTAMP);";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@selectionKey", selectionKey);
                command.Parameters.AddWithValue("@money", money);
                command.Parameters.AddWithValue("@itemCount", itemCount);
                command.ExecuteNonQuery();
            }
        }

    }
}
