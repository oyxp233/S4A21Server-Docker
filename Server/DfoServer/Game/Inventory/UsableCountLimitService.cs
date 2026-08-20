using DfoServer.Game.DailyReset;
using DfoServer.Game.SelectCharacter;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class UsableCountLimitState
    {
        public int ItemId { get; set; }

        public int UsedCount { get; set; }

        public int UsableCountLimit { get; set; }

        public int DayId { get; set; }
    }

    internal static class UsableCountLimitService
    {
        private const string TableName = "character_usable_count_limits";

        private static string CreateTableSql => $@"
CREATE TABLE IF NOT EXISTS {TableName} (
    character_id INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    used_count INTEGER NOT NULL DEFAULT 0,
    usable_count_limit INTEGER NOT NULL DEFAULT 0,
    day_id INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, item_id)
);
";

        private static string CreateIndexSql => $@"
CREATE INDEX IF NOT EXISTS idx_character_usable_count_limits_character_day
    ON {TableName}(character_id, day_id);";

        internal static bool IsUsableCountLimitItem(int itemTemplateId)
            => GetUsableCountLimit(itemTemplateId) >= 0;

        internal static int GetUsableCountLimit(int itemTemplateId)
        {
            var stackable = StackableItemProvider.Load(itemTemplateId);
            return stackable?.TotalUsableCount ?? -1;
        }

        internal static bool CanUse(
            string connectionString,
            int characterId,
            int itemTemplateId,
            int useCount = 1)
        {
            if (characterId <= 0
                || itemTemplateId <= 0
                || useCount <= 0)
                return false;

            var limit = GetUsableCountLimit(itemTemplateId);
            if (limit < 0)
                return true;
            if (limit <= 0 || useCount > limit)
                return false;

            if (string.IsNullOrWhiteSpace(connectionString))
                return false;

            if (!TryLoadCurrentState(
                    connectionString,
                    characterId,
                    itemTemplateId,
                    out var state))
            {
                return true;
            }

            return state.UsedCount <= limit - useCount;
        }

        internal static bool TryRecordUseIfLimited(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int itemTemplateId,
            int useCount,
            out UsableCountLimitState state)
        {
            state = null;
            if (connection == null
                || transaction == null
                || characterId <= 0
                || itemTemplateId <= 0
                || useCount <= 0)
            {
                return false;
            }

            var limit = GetUsableCountLimit(itemTemplateId);
            if (limit < 0)
                return true;
            if (limit <= 0 || useCount > limit)
                return false;

            var today = DailyResetService.TodayId();
            if (!EnsureSchema(connection, transaction))
                return false;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
INSERT INTO {TableName} (
    character_id, item_id, used_count, usable_count_limit, day_id, updated_at
) VALUES (
    @cid, @iid, @usedCount, @limit, @today, CURRENT_TIMESTAMP
)
ON CONFLICT(character_id, item_id) DO UPDATE SET
    used_count = CASE
        WHEN {TableName}.day_id = excluded.day_id
            THEN {TableName}.used_count + excluded.used_count
        ELSE excluded.used_count
    END,
    usable_count_limit = excluded.usable_count_limit,
    day_id = excluded.day_id,
    updated_at = CURRENT_TIMESTAMP
WHERE {TableName}.day_id <> excluded.day_id
   OR {TableName}.used_count <= excluded.usable_count_limit - excluded.used_count;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@iid", itemTemplateId);
                command.Parameters.AddWithValue("@usedCount", useCount);
                command.Parameters.AddWithValue("@limit", limit);
                command.Parameters.AddWithValue("@today", today);
                if (command.ExecuteNonQuery() == 0)
                    return false;
            }

            return TryLoadCurrentState(
                connection,
                transaction,
                characterId,
                itemTemplateId,
                out state);
        }

        internal static List<ItemValueEntrySnapshot> LoadCurrentDayItems(
            string connectionString,
            int characterId)
        {
            var result = new List<ItemValueEntrySnapshot>();
            if (characterId <= 0 || string.IsNullOrWhiteSpace(connectionString))
                return result;

            var today = DailyResetService.TodayId();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                if (!EnsureSchema(connection, null)
                    || !ResetExpiredRows(connection, null, characterId))
                    return result;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $@"
SELECT item_id, used_count
FROM {TableName}
WHERE character_id = @cid
  AND day_id = @today
  AND used_count > 0
ORDER BY item_id;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@today", today);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var itemId = reader.GetInt32(0);
                            if (!IsUsableCountLimitItem(itemId))
                                continue;

                            var usedCount = reader.GetInt32(1);
                            if (usedCount <= 0)
                                continue;

                            result.Add(new ItemValueEntrySnapshot
                            {
                                ItemId = itemId,
                                Value = usedCount,
                            });
                        }
                    }
                }
            }

            return result;
        }

        // 每日切换时先清掉前一日的限购物品记录，和每日补发走同一时刻。
        internal static bool ResetExpiredRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            if (connection == null || characterId <= 0)
                return false;

            if (!EnsureSchema(connection, transaction))
                return false;

            var today = DailyResetService.TodayId();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
DELETE FROM {TableName}
WHERE character_id = @cid
  AND day_id <> @today;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@today", today);
                command.ExecuteNonQuery();
                return true;
            }
        }

        internal static bool TryLoadCurrentState(
            string connectionString,
            int characterId,
            int itemTemplateId,
            out UsableCountLimitState state)
        {
            state = null;
            if (characterId <= 0
                || itemTemplateId <= 0
                || string.IsNullOrWhiteSpace(connectionString)
                || !IsUsableCountLimitItem(itemTemplateId))
            {
                return false;
            }

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                if (!EnsureSchema(connection, null))
                    return false;
                return TryLoadCurrentState(
                    connection,
                    null,
                    characterId,
                    itemTemplateId,
                    out state);
            }
        }

        private static bool TryLoadCurrentState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int itemTemplateId,
            out UsableCountLimitState state)
        {
            state = null;
            if (connection == null
                || characterId <= 0
                || itemTemplateId <= 0)
            {
                return false;
            }

            var today = DailyResetService.TodayId();
            if (!EnsureSchema(connection, transaction))
                return false;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
SELECT used_count, usable_count_limit, day_id
FROM {TableName}
WHERE character_id = @cid
  AND item_id = @iid
  AND day_id = @today;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@iid", itemTemplateId);
                command.Parameters.AddWithValue("@today", today);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    state = new UsableCountLimitState
                    {
                        ItemId = itemTemplateId,
                        UsedCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                        UsableCountLimit = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                        DayId = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    };
                    return true;
                }
            }
        }

        private static bool EnsureSchema(SqliteConnection connection, SqliteTransaction transaction)
        {
            if (connection == null)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = CreateTableSql;
                command.ExecuteNonQuery();
                command.CommandText = CreateIndexSql;
                command.ExecuteNonQuery();
                return true;
            }
        }
    }
}
