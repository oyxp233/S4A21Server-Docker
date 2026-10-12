using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.Game.Progression
{
    // 已提交的纯领域事实。活动消费者使用自己的游标/账本，不能复用基础邮件的消费标记。
    internal sealed record CharacterLevelUpEvent(
        long EventId, int CharacterId, byte PreviousLevel, byte NewLevel,
        string Source, DateTimeOffset OccurredAtUtc);

    internal static class CharacterLevelUpEventRepository
    {
        internal static void Record(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, byte previousLevel, byte newLevel, string source)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO character_level_up_events
    (character_id, previous_level, new_level, source, occurred_at_utc)
VALUES (@cid, @previous, @new, @source, @now);";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@previous", previousLevel);
            command.Parameters.AddWithValue("@new", newLevel);
            command.Parameters.AddWithValue("@source", string.IsNullOrWhiteSpace(source) ? "experience" : source);
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        internal static IReadOnlyList<CharacterLevelUpEvent> LoadCommittedAfter(
            SqliteConnection connection, long afterEventId, int limit = 64)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT event_id, character_id, previous_level, new_level, source, occurred_at_utc
FROM character_level_up_events WHERE event_id > @after ORDER BY event_id LIMIT @limit;";
            command.Parameters.AddWithValue("@after", afterEventId);
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 256));
            var result = new List<CharacterLevelUpEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(Read(reader));
            return result;
        }

        internal static IReadOnlyList<long> LoadPendingIds(SqliteConnection connection, long nowUnix, int limit)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT event_id FROM character_level_up_events
WHERE base_rewards_processed_at_utc IS NULL AND retry_after_unix <= @now
ORDER BY retry_after_unix, event_id LIMIT @limit;";
            command.Parameters.AddWithValue("@now", nowUnix);
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 64));
            var result = new List<long>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetInt64(0));
            return result;
        }

        internal static CharacterLevelUpEvent LoadPending(SqliteConnection connection, SqliteTransaction transaction,
            long eventId, long nowUnix, out int accountId, out string characterName, out bool deleted)
        {
            accountId = 0;
            characterName = string.Empty;
            deleted = false;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT e.event_id, e.character_id, e.previous_level, e.new_level, e.source, e.occurred_at_utc,
       c.account_id, c.name, c.delete_flag
FROM character_level_up_events e JOIN characters c ON c.character_id = e.character_id
WHERE e.event_id = @id AND e.base_rewards_processed_at_utc IS NULL AND e.retry_after_unix <= @now;";
            command.Parameters.AddWithValue("@id", eventId);
            command.Parameters.AddWithValue("@now", nowUnix);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            accountId = reader.GetInt32(6);
            characterName = reader.GetValue(7) is byte[] nameBytes
                ? ClientTextEncoding.GetString(nameBytes)
                : reader.GetString(7);
            deleted = reader.GetInt32(8) != 0;
            return Read(reader);
        }

        internal static void Complete(SqliteConnection connection, SqliteTransaction transaction,
            long eventId, DateTimeOffset now)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE character_level_up_events SET base_rewards_processed_at_utc = @now WHERE event_id = @id;";
            command.Parameters.AddWithValue("@now", now.ToString("O"));
            command.Parameters.AddWithValue("@id", eventId);
            command.ExecuteNonQuery();
        }

        internal static void Defer(SqliteConnection connection, SqliteTransaction transaction,
            long eventId, DateTimeOffset now)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE character_level_up_events SET attempts = attempts + 1, retry_after_unix = @retry
WHERE event_id = @id AND base_rewards_processed_at_utc IS NULL;";
            command.Parameters.AddWithValue("@retry", now.AddMinutes(1).ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@id", eventId);
            command.ExecuteNonQuery();
        }

        private static CharacterLevelUpEvent Read(SqliteDataReader reader)
            => new(reader.GetInt64(0), reader.GetInt32(1), (byte)reader.GetInt32(2), (byte)reader.GetInt32(3),
                reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture));
    }
}
