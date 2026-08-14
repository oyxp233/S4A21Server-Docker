using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.TitleBook
{
    public sealed class CharacterTitleBookRepository
    {
        private readonly string _connectionString;

        public CharacterTitleBookRepository(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public List<TitleBookCategorySnapshot> LoadSnapshots(int characterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return LoadModel(connection, characterId).BuildSnapshots();
            }
        }

        public TitleBookCategorySnapshot LoadSnapshot(int characterId, int category)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return LoadSnapshot(connection, null, characterId, category);
            }
        }

        public TitleBookCategorySnapshot LoadSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int category)
        {
            return LoadModel(connection, transaction, characterId).BuildSnapshot(category);
        }

        internal static TitleBookModel LoadModel(
            SqliteConnection connection,
            int characterId)
        {
            return LoadModel(connection, null, characterId);
        }

        internal static TitleBookModel LoadModel(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var model = new TitleBookModel();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT category, slot_index, item_core
FROM character_titlebook_items
WHERE character_id = @characterId
ORDER BY category, slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var data = reader.IsDBNull(2) ? null : (byte[])reader[2];
                        if (data == null || data.Length < ItemCore.Size)
                            continue;

                        model.AttachItem(
                            reader.GetInt32(0),
                            reader.GetInt32(1),
                            ItemCore.FromBytes(data));
                    }
                }
            }

            return model;
        }

        internal static void SaveSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int category,
            int slotIndex,
            ItemCore core)
        {
            if (category < 0 || category >= TitleBookStaticDataProvider.CategoryCapacities.Count)
                return;
            if (slotIndex < 0 || slotIndex >= TitleBookStaticDataProvider.CategoryCapacities[category])
                return;

            if (core == null || core.IsEmpty)
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
DELETE FROM character_titlebook_items
WHERE character_id = @characterId
  AND category = @category
  AND slot_index = @slotIndex;";
                    AddKeyParameters(command, characterId, category, slotIndex);
                    command.ExecuteNonQuery();
                }
                return;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_titlebook_items (
    character_id, category, slot_index, item_core, updated_at
) VALUES (
    @characterId, @category, @slotIndex, @itemCore, CURRENT_TIMESTAMP
)
ON CONFLICT(character_id, category, slot_index)
DO UPDATE SET
    item_core = excluded.item_core,
    updated_at = CURRENT_TIMESTAMP;";
                AddKeyParameters(command, characterId, category, slotIndex);
                command.Parameters.AddWithValue("@itemCore", core.ToBytes());
                command.ExecuteNonQuery();
            }
        }

        private static void AddKeyParameters(
            SqliteCommand command,
            int characterId,
            int category,
            int slotIndex)
        {
            command.Parameters.AddWithValue("@characterId", characterId);
            command.Parameters.AddWithValue("@category", category);
            command.Parameters.AddWithValue("@slotIndex", slotIndex);
        }
    }
}
