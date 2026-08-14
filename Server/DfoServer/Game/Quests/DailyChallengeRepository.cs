using System;
using System.Collections.Generic;
using DfoServer.Game.SelectCharacter;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class DailyChallengeRepository
    {
        private readonly string _connectionString;

        internal DailyChallengeRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        internal DailyChallengeStoreResult ApplyMutation(
            int characterId,
            ushort questId,
            Func<uint, uint, uint> mutation)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var entry = FindEntry(connection, transaction, characterId, questId);
                    if (entry == null)
                    {
                        var missingSnapshot = LoadSnapshot(connection, transaction, characterId);
                        transaction.Commit();
                        return DailyChallengeStoreResult.Missing(missingSnapshot);
                    }

                    var nextValue = mutation(entry.ValueA, entry.ValueB);
                    if (nextValue != entry.ValueB)
                    {
                        using (var command = new SqliteCommand(@"
UPDATE character_daily_challenge_entries
SET value_b = @next
WHERE character_id = @cid
  AND group_index = @groupIndex
  AND entry_index = @entryIndex
  AND track_like_id = @questId
  AND value_b = @expected;", connection, transaction))
                        {
                            command.Parameters.AddWithValue("@next", (long)nextValue);
                            command.Parameters.AddWithValue("@cid", characterId);
                            command.Parameters.AddWithValue("@groupIndex", entry.GroupIndex);
                            command.Parameters.AddWithValue("@entryIndex", entry.EntryIndex);
                            command.Parameters.AddWithValue("@questId", (int)questId);
                            command.Parameters.AddWithValue("@expected", (long)entry.ValueB);
                            if (command.ExecuteNonQuery() != 1)
                                throw new InvalidOperationException("DailyChallenge value_b CAS failed inside immediate transaction.");
                        }
                    }

                    var snapshot = LoadSnapshot(connection, transaction, characterId);
                    transaction.Commit();
                    return new DailyChallengeStoreResult(
                        found: true,
                        entry.GroupIndex,
                        entry.EntryIndex,
                        entry.ValueA,
                        entry.ValueB,
                        nextValue,
                        snapshot);
                }
            }
        }

        internal DailyChallengeResetResult ResetCharacter(int characterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    int changedEntries;
                    using (var command = new SqliteCommand(@"
UPDATE character_daily_challenge_entries
SET value_b = value_a
WHERE character_id = @cid
  AND value_b <> value_a;", connection, transaction))
                    {
                        command.Parameters.AddWithValue("@cid", characterId);
                        changedEntries = command.ExecuteNonQuery();
                    }

                    int clearedClaims;
                    using (var command = new SqliteCommand(@"
DELETE FROM character_daily_challenge_claims
WHERE character_id = @cid;", connection, transaction))
                    {
                        command.Parameters.AddWithValue("@cid", characterId);
                        clearedClaims = command.ExecuteNonQuery();
                    }

                    var snapshot = LoadSnapshot(connection, transaction, characterId);
                    transaction.Commit();
                    return new DailyChallengeResetResult(changedEntries, clearedClaims, snapshot);
                }
            }
        }

        internal DailyChallengeRewardStoreState LoadRewardState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int groupIndex)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            var state = new DailyChallengeRewardStoreState
            {
                GroupIndex = groupIndex,
            };

            using (var command = new SqliteCommand(@"
SELECT group_id
FROM character_daily_challenge_groups
WHERE character_id = @cid AND group_index = @groupIndex;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupIndex", groupIndex);
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return state;

                state.Found = true;
                state.GroupId = Convert.ToInt32(value);
            }

            using (var command = new SqliteCommand(@"
SELECT COUNT(*),
       COALESCE(SUM(CASE WHEN value_b = 0 THEN 1 ELSE 0 END), 0)
FROM character_daily_challenge_entries
WHERE character_id = @cid AND group_index = @groupIndex;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupIndex", groupIndex);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        state.EntryCount = reader.GetInt32(0);
                        state.CompletedEntryCount = reader.GetInt32(1);
                    }
                }
            }

            using (var command = new SqliteCommand(@"
SELECT 1
FROM character_daily_challenge_claims
WHERE character_id = @cid AND group_index = @groupIndex;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupIndex", groupIndex);
                state.Claimed = command.ExecuteScalar() != null;
            }

            return state;
        }

        internal bool TryMarkRewardClaimed(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int groupIndex)
        {
            using (var command = new SqliteCommand(@"
INSERT INTO character_daily_challenge_claims (character_id, group_index)
VALUES (@cid, @groupIndex)
ON CONFLICT(character_id, group_index) DO NOTHING;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupIndex", groupIndex);
                return command.ExecuteNonQuery() == 1;
            }
        }

        private static DailyChallengeEntryRecord FindEntry(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ushort questId)
        {
            using (var command = new SqliteCommand(@"
SELECT group_index, entry_index, value_a, value_b
FROM character_daily_challenge_entries
WHERE character_id = @cid AND track_like_id = @questId
ORDER BY group_index, entry_index
LIMIT 1;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@questId", (int)questId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new DailyChallengeEntryRecord
                    {
                        GroupIndex = reader.GetInt32(0),
                        EntryIndex = reader.GetInt32(1),
                        ValueA = (uint)reader.GetInt64(2),
                        ValueB = (uint)reader.GetInt64(3),
                    };
                }
            }
        }

        internal static SelectCharacterInitializationSnapshot LoadSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var snapshot = new SelectCharacterInitializationSnapshot();
            using (var command = new SqliteCommand(@"
SELECT level
FROM characters
WHERE character_id = @cid;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                var value = command.ExecuteScalar();
                snapshot.DailyChallengeCharacterLevel = value == null || value == DBNull.Value
                    ? 1u
                    : (uint)Convert.ToInt64(value);
            }

            var groupsByIndex = new Dictionary<int, RacingDungeonGroupSnapshot>();
            using (var command = new SqliteCommand(@"
SELECT group_index, group_id
FROM character_daily_challenge_groups
WHERE character_id = @cid
ORDER BY group_index;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var group = new RacingDungeonGroupSnapshot
                        {
                            GroupId = (uint)reader.GetInt64(1),
                        };
                        groupsByIndex[reader.GetInt32(0)] = group;
                        snapshot.RacingDungeonGroups.Add(group);
                    }
                }
            }

            using (var command = new SqliteCommand(@"
SELECT group_index, track_like_id, value_a, value_b
FROM character_daily_challenge_entries
WHERE character_id = @cid
ORDER BY group_index, entry_index;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!groupsByIndex.TryGetValue(reader.GetInt32(0), out var group))
                            continue;

                        group.Entries.Add(new RacingDungeonEntrySnapshot
                        {
                            TrackLikeId = (uint)reader.GetInt64(1),
                            ValueA = (uint)reader.GetInt64(2),
                            ValueB = (uint)reader.GetInt64(3),
                        });
                    }
                }
            }

            snapshot.DailyChallengeRewardClaimFlags = new byte[6];
            using (var command = new SqliteCommand(@"
SELECT group_index
FROM character_daily_challenge_claims
WHERE character_id = @cid
ORDER BY group_index;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var groupIndex = reader.GetInt32(0);
                        if (groupIndex >= 0 && groupIndex < snapshot.DailyChallengeRewardClaimFlags.Length)
                            snapshot.DailyChallengeRewardClaimFlags[groupIndex] = 1;
                    }
                }
            }

            using (var command = new SqliteCommand(@"
SELECT id_value
FROM character_daily_challenge_tail_ids
WHERE character_id = @cid
ORDER BY sort_order;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        snapshot.RacingDungeonTailIds.Add((uint)reader.GetInt64(0));
                }
            }

            return snapshot;
        }

        private sealed class DailyChallengeEntryRecord
        {
            internal int GroupIndex;
            internal int EntryIndex;
            internal uint ValueA;
            internal uint ValueB;
        }
    }

    internal sealed class DailyChallengeRewardStoreState
    {
        internal bool Found { get; set; }
        internal int GroupIndex { get; set; }
        internal int GroupId { get; set; }
        internal int EntryCount { get; set; }
        internal int CompletedEntryCount { get; set; }
        internal bool Claimed { get; set; }
    }

    internal sealed class DailyChallengeStoreResult
    {
        internal DailyChallengeStoreResult(
            bool found,
            int groupIndex,
            int entryIndex,
            uint targetValue,
            uint previousValue,
            uint currentValue,
            SelectCharacterInitializationSnapshot snapshot)
        {
            Found = found;
            GroupIndex = groupIndex;
            EntryIndex = entryIndex;
            TargetValue = targetValue;
            PreviousValue = previousValue;
            CurrentValue = currentValue;
            Snapshot = snapshot;
        }

        internal bool Found { get; }
        internal int GroupIndex { get; }
        internal int EntryIndex { get; }
        internal uint TargetValue { get; }
        internal uint PreviousValue { get; }
        internal uint CurrentValue { get; }
        internal SelectCharacterInitializationSnapshot Snapshot { get; }
        internal bool Changed => Found && PreviousValue != CurrentValue;

        internal static DailyChallengeStoreResult Missing(
            SelectCharacterInitializationSnapshot snapshot) =>
            new DailyChallengeStoreResult(false, -1, -1, 0, uint.MaxValue, uint.MaxValue, snapshot);
    }

    internal sealed class DailyChallengeResetResult
    {
        internal DailyChallengeResetResult(
            int changedEntries,
            int clearedClaims,
            SelectCharacterInitializationSnapshot snapshot)
        {
            ChangedEntries = changedEntries;
            ClearedClaims = clearedClaims;
            Snapshot = snapshot;
        }

        internal int ChangedEntries { get; }
        internal int ClearedClaims { get; }
        internal SelectCharacterInitializationSnapshot Snapshot { get; }
    }
}
