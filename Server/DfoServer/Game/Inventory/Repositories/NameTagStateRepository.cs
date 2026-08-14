using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class NameTagStateRepository
    {
        internal static NameTagState Load(SqliteConnection connection, int characterId)
        {
            var state = new NameTagState();
            if (connection == null || characterId <= 0)
                return state;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_id, expire_time
FROM character_name_tag_state
WHERE character_id = @characterId
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return state;

                    state.Set(reader.GetInt32(0), reader.GetInt32(1));
                }
            }

            return state;
        }

        internal static void Upsert(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int itemId,
            int expireTime)
        {
            if (connection == null || transaction == null || characterId <= 0)
                return;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_name_tag_state (
    character_id, item_id, expire_time, updated_at
) VALUES (
    @characterId, @itemId, @expireTime, CURRENT_TIMESTAMP
)
ON CONFLICT(character_id) DO UPDATE SET
    item_id = excluded.item_id,
    expire_time = excluded.expire_time,
    updated_at = CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@itemId", Math.Max(0, itemId));
                command.Parameters.AddWithValue("@expireTime", Math.Max(0, expireTime));
                command.ExecuteNonQuery();
            }
        }

        internal static bool ClearExpired(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            uint now)
        {
            if (connection == null || transaction == null || characterId <= 0)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_name_tag_state
SET item_id = 0,
    expire_time = 0,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @characterId
  AND item_id > 0
  AND expire_time > 0
  AND expire_time <= @now;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@now", now);
                return command.ExecuteNonQuery() > 0;
            }
        }

    }
}
