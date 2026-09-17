using DfoServer.Game.DailyReset;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class AntonAwakeningDailyProgressRepository
    {
        private const string LegacyMarkerKey =
            "anton_awakening_progress_initialized";

        private readonly IGameDatabase _database;
        private readonly DailyResetService _dailyReset;
        private readonly SequentialDungeonDefinitionCatalog _catalog;

        internal AntonAwakeningDailyProgressRepository(
            IGameDatabase database,
            DailyResetService dailyReset,
            SequentialDungeonDefinitionCatalog catalog = null)
        {
            _database = database
                ?? throw new ArgumentNullException(nameof(database));
            _dailyReset = dailyReset
                ?? throw new ArgumentNullException(nameof(dailyReset));
            _catalog = catalog ?? SequentialDungeonDefinitionCatalog.Current;
        }

        internal List<DungeonPermissionEntrySnapshot>
            EnsureCurrentDayAndLoad(
                int characterId,
                SequentialDungeonDefinition definition,
                DateTime utcNow)
        {
            ValidateCharacterId(characterId);
            ValidateDefinition(definition);
            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                EnsureCurrentDay(
                    connection,
                    transaction,
                    characterId,
                    definition,
                    utcNow);
                var snapshot = Load(
                    connection,
                    transaction,
                    characterId,
                    definition);
                transaction.Commit();
                return snapshot;
            }
        }

        internal List<DungeonPermissionEntrySnapshot> RecordClearAndLoad(
            int characterId,
            SequentialDungeonDefinition definition,
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> updates,
            DateTime utcNow,
            out List<DungeonPermissionEntrySnapshot> changes)
        {
            ValidateCharacterId(characterId);
            ValidateDefinition(definition);
            var normalized = NormalizeUpdates(definition, updates);
            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                EnsureCurrentDay(
                    connection,
                    transaction,
                    characterId,
                    definition,
                    utcNow);
                changes = new List<DungeonPermissionEntrySnapshot>();
                foreach (var update in normalized)
                {
                    if (!Upsert(
                            connection,
                            transaction,
                            characterId,
                            update.DungeonId,
                            update.ClearState))
                    {
                        continue;
                    }

                    changes.Add(new DungeonPermissionEntrySnapshot
                    {
                        DungeonId = update.DungeonId,
                        ClearState = update.ClearState,
                    });
                }

                var snapshot = Load(
                    connection,
                    transaction,
                    characterId,
                    definition);
                transaction.Commit();
                return snapshot;
            }
        }

        private void EnsureCurrentDay(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            SequentialDungeonDefinition definition,
            DateTime utcNow)
        {
            var markerKey = BuildMarkerKey(definition.GroupKey);
            var resetDayId = ReadResetDayId(
                connection,
                transaction,
                characterId);
            var hasCurrentDayAnchor = resetDayId.HasValue
                && resetDayId.Value == DailyResetService.TodayId(utcNow);
            var hasLegacyMarker = HasCounter(
                connection,
                transaction,
                characterId,
                LegacyMarkerKey,
                DailyResetService.PeriodDay);
            if (hasLegacyMarker
                && (!resetDayId.HasValue || resetDayId.Value == 0))
            {
                // A legacy marker without a same-day reset anchor cannot be
                // dated reliably. Preserve permissions and fail closed before
                // DailyResetService can roll over or mutate any rows.
                throw new InvalidOperationException(
                    "Legacy Anton Awakening progress marker has no current-day reset anchor.");
            }

            var marker = _dailyReset.GetCounter(
                connection,
                transaction,
                characterId,
                markerKey,
                DailyResetService.PeriodDay,
                utcNow);
            if (marker == 1)
                return;
            if (marker != 0)
            {
                throw new InvalidOperationException(
                    $"Invalid Anton Awakening daily progress marker value: {marker}.");
            }

            // Deployments before the ETC catalog used a single legacy marker.
            // Migrate it only while it is still valid for this game day and
            // only when the catalog can identify one Anton awakening definition.
            var legacyMarker = 0L;
            if (hasCurrentDayAnchor
                && _catalog
                .IsUniqueAntonAwakeningDefinition(definition))
            {
                legacyMarker = _dailyReset.GetCounter(
                    connection,
                    transaction,
                    characterId,
                    LegacyMarkerKey,
                    DailyResetService.PeriodDay,
                    utcNow);
                if (legacyMarker != 0 && legacyMarker != 1)
                {
                    throw new InvalidOperationException(
                        $"Invalid legacy Anton Awakening daily progress marker value: {legacyMarker}.");
                }
            }

            if (legacyMarker == 1)
            {
                if (!_dailyReset.TryClaimFlag(
                        connection,
                        transaction,
                        characterId,
                        markerKey,
                        DailyResetService.PeriodDay,
                        utcNow)
                    || _dailyReset.GetCounter(
                        connection,
                        transaction,
                        characterId,
                        markerKey,
                        DailyResetService.PeriodDay,
                        utcNow) != 1)
                {
                    throw new InvalidOperationException(
                        "Unable to migrate the legacy Anton Awakening daily progress marker.");
                }
                return;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                var dungeonParameters = AddDungeonParameters(
                    command,
                    definition.DungeonIds);
                command.CommandText = $@"
DELETE FROM character_dungeon_permissions
WHERE character_id = @cid
  AND dungeon_id IN ({dungeonParameters});";
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }

            if (!_dailyReset.TryClaimFlag(
                    connection,
                    transaction,
                    characterId,
                    markerKey,
                    DailyResetService.PeriodDay,
                    utcNow)
                || _dailyReset.GetCounter(
                    connection,
                    transaction,
                    characterId,
                    markerKey,
                    DailyResetService.PeriodDay,
                    utcNow) != 1)
            {
                throw new InvalidOperationException(
                    "Unable to claim the Anton Awakening daily progress marker.");
            }
        }

        private static List<DungeonPermissionEntrySnapshot> NormalizeUpdates(
            SequentialDungeonDefinition definition,
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> updates)
        {
            if (updates == null)
                throw new ArgumentNullException(nameof(updates));

            var result = new List<DungeonPermissionEntrySnapshot>();
            var indexes = new Dictionary<ushort, int>();
            var configuredDungeonIds = new HashSet<int>(
                definition.DungeonIds);
            foreach (var update in updates)
            {
                if (update == null
                    || !configuredDungeonIds.Contains(update.DungeonId)
                    || update.ClearState == 0)
                {
                    throw new ArgumentException(
                        "Sequential progress updates require configured dungeon IDs and non-zero states.",
                        nameof(updates));
                }

                if (indexes.TryGetValue(update.DungeonId, out var index))
                {
                    if (result[index].ClearState < update.ClearState)
                        result[index].ClearState = update.ClearState;
                    continue;
                }

                indexes.Add(update.DungeonId, result.Count);
                result.Add(new DungeonPermissionEntrySnapshot
                {
                    DungeonId = update.DungeonId,
                    ClearState = update.ClearState,
                });
            }
            return result;
        }

        private static bool Upsert(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int dungeonId,
            byte clearState)
        {
            var existingRows = 0;
            var currentState = 0;
            using (var command = new SqliteCommand(@"
SELECT COUNT(*), COALESCE(MAX(clear_state), 0)
FROM character_dungeon_permissions
WHERE character_id = @cid AND dungeon_id = @did;",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@did", dungeonId);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        existingRows = reader.GetInt32(0);
                        currentState = reader.GetInt32(1);
                    }
                }
            }

            if (currentState >= clearState)
                return false;

            if (existingRows > 0)
            {
                using (var command = new SqliteCommand(@"
UPDATE character_dungeon_permissions
SET clear_state = @state
WHERE character_id = @cid AND dungeon_id = @did;",
                    connection,
                    transaction))
                {
                    command.Parameters.AddWithValue("@state", clearState);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@did", dungeonId);
                    command.ExecuteNonQuery();
                }
            }
            else
            {
                using (var command = new SqliteCommand(@"
INSERT INTO character_dungeon_permissions
    (character_id, sort_order, dungeon_id, clear_state)
VALUES
    (@cid,
     (SELECT COALESCE(MAX(sort_order), 0) + 1
      FROM character_dungeon_permissions
      WHERE character_id = @cid),
     @did,
     @state);",
                    connection,
                    transaction))
                {
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@did", dungeonId);
                    command.Parameters.AddWithValue("@state", clearState);
                    command.ExecuteNonQuery();
                }
            }
            return true;
        }

        private static List<DungeonPermissionEntrySnapshot> Load(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            SequentialDungeonDefinition definition)
        {
            var result = new List<DungeonPermissionEntrySnapshot>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                var dungeonParameters = AddDungeonParameters(
                    command,
                    definition.DungeonIds);
                command.CommandText = $@"
SELECT dungeon_id, clear_state
FROM character_dungeon_permissions
WHERE character_id = @cid
  AND dungeon_id IN ({dungeonParameters})
ORDER BY sort_order;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new DungeonPermissionEntrySnapshot
                        {
                            DungeonId = (ushort)reader.GetInt32(0),
                            ClearState = (byte)reader.GetInt32(1),
                        });
                    }
                }
            }
            return result;
        }

        private static int? ReadResetDayId(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT day_id
FROM character_daily_reset
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? (int?)null
                    : Convert.ToInt32(value);
            }
        }

        private static bool HasCounter(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            string counterKey,
            string period)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 1
FROM character_daily_counters
WHERE character_id = @cid
  AND counter_key = @key
  AND period = @period
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@key", counterKey);
                command.Parameters.AddWithValue("@period", period);
                return command.ExecuteScalar() != null;
            }
        }

        internal static string BuildMarkerKey(int groupKey)
        {
            if (groupKey <= 0)
                throw new ArgumentOutOfRangeException(nameof(groupKey));
            return "sequential_progress_v1:" + groupKey;
        }

        private static string AddDungeonParameters(
            SqliteCommand command,
            IReadOnlyList<int> dungeonIds)
        {
            var parameterNames = new string[dungeonIds.Count];
            for (var index = 0; index < dungeonIds.Count; index++)
            {
                var parameterName = "@did" + index;
                parameterNames[index] = parameterName;
                command.Parameters.AddWithValue(
                    parameterName,
                    dungeonIds[index]);
            }
            return string.Join(",", parameterNames);
        }

        private static void ValidateDefinition(
            SequentialDungeonDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (definition.DungeonIds.Count == 0)
            {
                throw new ArgumentException(
                    "A sequential progress definition requires dungeons.",
                    nameof(definition));
            }
        }

        private static void ValidateCharacterId(int characterId)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
        }
    }
}
