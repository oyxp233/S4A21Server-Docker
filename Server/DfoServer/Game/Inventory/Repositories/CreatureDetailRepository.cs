using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class CreatureDetailRepository
    {
        private const string CreatureUidSequenceTable = "character_creature_uid_sequence";

        internal static Dictionary<int, CreatureDetail> LoadForCharacter(SqliteConnection connection, int characterId)
        {
            var result = new Dictionary<int, CreatureDetail>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT character_id,
       sort_order,
       creature_key,
       field04,
       mode_flag,
       progress_value,
       mode1_field0a,
       mode1_field0b,
       field_after_value,
       creature_text,
       tail_flag,
       extra_json
FROM character_creatures
WHERE character_id = @cid
ORDER BY sort_order;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var detail = CreatureDetailCodec.ReadDetail(reader);
                        result[detail.Uid] = detail;
                    }
                }
            }

            return result;
        }

        internal static bool Update(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            CreatureDetail detail)
        {
            if (detail == null)
                throw new ArgumentNullException(nameof(detail));

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_creatures
SET field04 = @field04,
    mode_flag = @modeFlag,
    progress_value = @progressValue,
    mode1_field0a = @mode1Field0A,
    mode1_field0b = @mode1Field0B,
    field_after_value = @fieldAfterValue,
    creature_text = @creatureText,
    tail_flag = @tailFlag
WHERE character_id = @characterId
  AND creature_key = @creatureKey;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@creatureKey", detail.Uid);
                command.Parameters.AddWithValue("@field04", detail.Field04);
                command.Parameters.AddWithValue("@modeFlag", detail.ModeFlag);
                command.Parameters.AddWithValue("@progressValue", detail.ProgressValue32);
                command.Parameters.AddWithValue("@mode1Field0A", detail.Mode1Field0A);
                command.Parameters.AddWithValue("@mode1Field0B", detail.Mode1Field0B);
                command.Parameters.AddWithValue("@fieldAfterValue", detail.FieldAfterValue32);
                command.Parameters.AddWithValue("@creatureText", detail.NameBytes ?? Array.Empty<byte>());
                command.Parameters.AddWithValue("@tailFlag", detail.TailFlag);
                return command.ExecuteNonQuery() > 0;
            }
        }

        internal static bool Upsert(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            CreatureDetail detail)
        {
            if (Update(connection, transaction, characterId, detail))
                return true;

            var sortOrder = ResolveNextSortOrder(connection, transaction, characterId);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_creatures(
    character_id, sort_order, creature_key, field04, mode_flag, progress_value,
    mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag, extra_json)
VALUES(
    @characterId, @sortOrder, @creatureKey, @field04, @modeFlag, @progressValue,
    @mode1Field0A, @mode1Field0B, @fieldAfterValue, @creatureText, @tailFlag, '{}');";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@sortOrder", sortOrder);
                command.Parameters.AddWithValue("@creatureKey", detail.Uid);
                command.Parameters.AddWithValue("@field04", detail.Field04);
                command.Parameters.AddWithValue("@modeFlag", detail.ModeFlag);
                command.Parameters.AddWithValue("@progressValue", detail.ProgressValue32);
                command.Parameters.AddWithValue("@mode1Field0A", detail.Mode1Field0A);
                command.Parameters.AddWithValue("@mode1Field0B", detail.Mode1Field0B);
                command.Parameters.AddWithValue("@fieldAfterValue", detail.FieldAfterValue32);
                command.Parameters.AddWithValue("@creatureText", detail.NameBytes ?? Array.Empty<byte>());
                command.Parameters.AddWithValue("@tailFlag", detail.TailFlag);
                return command.ExecuteNonQuery() > 0;
            }
        }

        internal static void Delete(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int creatureKey)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM character_creatures
WHERE character_id = @characterId
  AND creature_key = @creatureKey;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@creatureKey", creatureKey);
                command.ExecuteNonQuery();
            }
        }

        internal static long AllocateCreatureUid(IGameDatabase database = null)
        {
            if (InventoryUidAllocationContext.TryGet(
                    database,
                    out var ambientConnection,
                    out var ambientTransaction))
                return AllocateCreatureUid(ambientConnection, ambientTransaction);

            database ??= GameDatabase.CreateDefault();
            using (var connection = database.OpenConnection())
            {
                using (var transaction = connection.BeginTransaction())
                {
                    var creatureUid = AllocateCreatureUid(connection, transaction);
                    transaction.Commit();
                    return creatureUid;
                }
            }
        }

        internal static long AllocateCreatureUid(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            EnsureCreatureUidSequence(connection, transaction);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"INSERT INTO {CreatureUidSequenceTable} DEFAULT VALUES;";
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT last_insert_rowid();";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        internal static void EnsureCreatureUidSequence(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
CREATE TABLE IF NOT EXISTS {CreatureUidSequenceTable} (
    creature_uid INTEGER PRIMARY KEY AUTOINCREMENT
);";
                command.ExecuteNonQuery();
            }

            var detailMax = ReadMaxUid(connection, transaction, "character_creatures", "creature_key");
            if (detailMax <= 0)
                return;

            var sequenceMax = ReadMaxUid(connection, transaction, CreatureUidSequenceTable, "creature_uid");
            if (sequenceMax >= detailMax)
                return;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
INSERT OR IGNORE INTO {CreatureUidSequenceTable} (creature_uid)
VALUES (@creatureUid);";
                command.Parameters.AddWithValue("@creatureUid", detailMax);
                command.ExecuteNonQuery();
            }
        }

        private static int ResolveNextSortOrder(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COALESCE(MAX(sort_order), -1) + 1
FROM character_creatures
WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", characterId);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static long ReadMaxUid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName,
            string columnName)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"SELECT COALESCE(MAX({columnName}), 0) FROM {tableName};";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }
    }
}
