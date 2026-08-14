using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestNotifySelectionRepository
    {
        private readonly string _connectionString;

        internal QuestNotifySelectionRepository(string connectionString)
        {
            _connectionString = connectionString
                ?? throw new ArgumentNullException(nameof(connectionString));
        }

        internal IReadOnlyList<int> Load(int characterId)
        {
            var result = new List<int>(QuestNotifySelectionService.MaxSlots);
            if (characterId <= 0)
                return result;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT quest_id
FROM character_quest_notify_selections
WHERE character_id = @characterId
ORDER BY slot_index;";
                    command.Parameters.AddWithValue("@characterId", characterId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            result.Add(reader.GetInt32(0));
                    }
                }
            }
            return result;
        }

        internal void Replace(int characterId, IReadOnlyList<int> questIds)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (questIds == null)
                throw new ArgumentNullException(nameof(questIds));
            if (questIds.Count > QuestNotifySelectionService.MaxSlots)
                throw new ArgumentOutOfRangeException(nameof(questIds));

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    using (var delete = connection.CreateCommand())
                    {
                        delete.Transaction = transaction;
                        delete.CommandText = @"
DELETE FROM character_quest_notify_selections
WHERE character_id = @characterId;";
                        delete.Parameters.AddWithValue("@characterId", characterId);
                        delete.ExecuteNonQuery();
                    }

                    for (var slot = 0; slot < questIds.Count; slot++)
                    {
                        using (var insert = connection.CreateCommand())
                        {
                            insert.Transaction = transaction;
                            insert.CommandText = @"
INSERT INTO character_quest_notify_selections
    (character_id, slot_index, quest_id)
VALUES (@characterId, @slotIndex, @questId);";
                            insert.Parameters.AddWithValue("@characterId", characterId);
                            insert.Parameters.AddWithValue("@slotIndex", slot);
                            insert.Parameters.AddWithValue("@questId", questIds[slot]);
                            insert.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                }
            }
        }
    }
}
