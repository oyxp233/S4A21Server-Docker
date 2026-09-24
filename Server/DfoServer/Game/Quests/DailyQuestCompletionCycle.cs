using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using DfoServer.Game.DailyReset;

namespace DfoServer.Game.Quests
{
    // The day counter is the authority; character_quest_completions is the
    // client-facing projection for the current game day only.
    internal static class DailyQuestCompletionCycle
    {
        private const string CompletedPrefix = "quest_daily_completed_";

        internal static string CompletedKey(int questId)
            => CompletedPrefix + questId;

        internal static bool TryClaim(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int questId,
            DateTime utcNow)
            => new DailyResetService(connection.ConnectionString).TryClaimFlag(
                connection,
                transaction,
                characterId,
                CompletedKey(questId),
                DailyResetService.PeriodDay,
                utcNow);

        internal static long GetCurrentCounter(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int questId,
            DateTime utcNow)
            => new DailyResetService(connection.ConnectionString).GetCounter(
                connection,
                transaction,
                characterId,
                CompletedKey(questId),
                DailyResetService.PeriodDay,
                utcNow);

        internal static bool IsClaimedToday(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int questId,
            DateTime utcNow)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 1
FROM character_daily_reset AS reset
JOIN character_daily_counters AS counter
  ON counter.character_id = reset.character_id
WHERE reset.character_id = @cid
  AND reset.day_id = @day
  AND counter.counter_key = @key
  AND counter.period = 'day'
  AND counter.value > 0
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue(
                    "@day",
                    DailyResetService.TodayId(utcNow));
                command.Parameters.AddWithValue("@key", CompletedKey(questId));
                return command.ExecuteScalar() != null;
            }
        }

        internal static HashSet<int> LoadClaimedQuestIds(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            DateTime utcNow)
        {
            var result = new HashSet<int>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT counter.counter_key
FROM character_daily_reset AS reset
JOIN character_daily_counters AS counter
  ON counter.character_id = reset.character_id
WHERE reset.character_id = @cid
  AND reset.day_id = @day
  AND counter.period = 'day'
  AND counter.value > 0
  AND substr(counter.counter_key, 1, @prefixLength) = @prefix;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue(
                    "@day",
                    DailyResetService.TodayId(utcNow));
                command.Parameters.AddWithValue("@prefix", CompletedPrefix);
                command.Parameters.AddWithValue("@prefixLength", CompletedPrefix.Length);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var key = reader.GetString(0);
                        if (int.TryParse(
                                key.Substring(CompletedPrefix.Length),
                                out var questId)
                            && questId > 0)
                        {
                            result.Add(questId);
                        }
                    }
                }
            }
            return result;
        }
    }
}
