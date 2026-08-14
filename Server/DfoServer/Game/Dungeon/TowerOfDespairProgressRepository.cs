using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Dungeon
{
    public sealed class TowerOfDespairProgressRepository
    {
        private const int MaximumFloor = 100;
        private readonly string _connectionString;

        public TowerOfDespairProgressRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public TowerOfDespairProgressRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public int GetNextFloor(int characterId)
        {
            if (characterId <= 0)
                return 1;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT highest_cleared_floor
FROM character_tower_of_despair_progress
WHERE character_id = @characterId;";
                    command.Parameters.AddWithValue("@characterId", characterId);
                    var value = command.ExecuteScalar();
                    if (value == null || value == DBNull.Value)
                        return 1;

                    var highestClearedFloor = Math.Max(0, Math.Min(MaximumFloor, Convert.ToInt32(value)));
                    return Math.Min(MaximumFloor, highestClearedFloor + 1);
                }
            }
        }

        public int RecordClear(int characterId, int clearedFloor)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (clearedFloor < 1 || clearedFloor > MaximumFloor)
                throw new ArgumentOutOfRangeException(nameof(clearedFloor));

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var nextFloor = RecordClearInTransaction(
                        connection,
                        transaction,
                        characterId,
                        clearedFloor);
                    transaction.Commit();
                    return nextFloor;
                }
            }
        }

        internal static int RecordClearInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int clearedFloor)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (clearedFloor < 1 || clearedFloor > MaximumFloor)
                throw new ArgumentOutOfRangeException(nameof(clearedFloor));

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_tower_of_despair_progress(
    character_id,
    highest_cleared_floor,
    updated_at)
VALUES(@characterId, @clearedFloor, CURRENT_TIMESTAMP)
ON CONFLICT(character_id) DO UPDATE SET
    highest_cleared_floor = MAX(
        character_tower_of_despair_progress.highest_cleared_floor,
        excluded.highest_cleared_floor),
    updated_at = CASE
        WHEN excluded.highest_cleared_floor
            > character_tower_of_despair_progress.highest_cleared_floor
        THEN CURRENT_TIMESTAMP
        ELSE character_tower_of_despair_progress.updated_at
    END;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@clearedFloor", clearedFloor);
                command.ExecuteNonQuery();
            }

            int highestClearedFloor;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT highest_cleared_floor
FROM character_tower_of_despair_progress
WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", characterId);
                highestClearedFloor = Convert.ToInt32(command.ExecuteScalar());
            }

            return Math.Min(MaximumFloor, highestClearedFloor + 1);
        }
    }
}
