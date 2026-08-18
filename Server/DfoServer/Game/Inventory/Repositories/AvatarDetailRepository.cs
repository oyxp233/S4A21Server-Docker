using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class AvatarDetailRepository
    {
        private const string AvatarUidSequenceTable = "character_avatar_uid_sequence";

        internal static Dictionary<long, AvatarDetail> LoadForCharacter(SqliteConnection connection, int characterId)
        {
            var result = new Dictionary<long, AvatarDetail>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_uid,
       owner_id,
       character_id,
       item_id,
       expire_date,
       clear_avatar_id,
       jewel_socket,
       color1,
       color2,
       delete_date
FROM character_avatar_detail
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var detail = AvatarDetailCodec.ReadDetail(reader);
                        result[detail.AvatarUid] = detail;
                    }
                }
            }

            return result;
        }

        internal static Dictionary<long, AvatarDetail> LoadForAccount(SqliteConnection connection, int accountId)
        {
            var result = new Dictionary<long, AvatarDetail>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT d.item_uid,
       d.owner_id,
       d.character_id,
       d.item_id,
       d.expire_date,
       d.clear_avatar_id,
       d.jewel_socket,
       d.color1,
       d.color2,
       d.delete_date
FROM character_avatar_detail d
JOIN characters c ON c.character_id = d.character_id
WHERE c.account_id = @accountId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var detail = AvatarDetailCodec.ReadDetail(reader);
                        result[detail.AvatarUid] = detail;
                    }
                }
            }

            return result;
        }

        internal static void Upsert(
            SqliteConnection connection,
            SqliteTransaction transaction,
            AvatarDetail detail)
        {
            if (detail == null)
                throw new ArgumentNullException(nameof(detail));

            var record = AvatarDetailCodec.ToRecord(detail);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO character_avatar_detail (
    item_uid, owner_id, character_id, item_id, expire_date, clear_avatar_id, jewel_socket, color1, color2, delete_date
) VALUES (
    @itemUid, @ownerId, @characterId, @itemId, @expireDate, @clearAvatarId, @jewelSocket, @color1, @color2, @deleteDate
);";
                command.Parameters.AddWithValue("@itemUid", record.AvatarUid);
                command.Parameters.AddWithValue("@ownerId", record.OwnerId);
                command.Parameters.AddWithValue("@characterId", record.CharacterId);
                command.Parameters.AddWithValue("@itemId", record.ItemId);
                command.Parameters.AddWithValue("@expireDate", record.ExpireDate);
                command.Parameters.AddWithValue("@clearAvatarId", record.ClearAvatarId);
                command.Parameters.AddWithValue("@jewelSocket", CopyFixed(record.JewelSocket, 30));
                command.Parameters.AddWithValue("@color1", record.Color1);
                command.Parameters.AddWithValue("@color2", record.Color2);
                command.Parameters.AddWithValue("@deleteDate", record.DeleteDate);
                command.ExecuteNonQuery();
            }
        }

        internal static long AllocateAvatarUid(IGameDatabase database = null)
        {
            if (InventoryUidAllocationContext.TryGet(
                    database,
                    out var ambientConnection,
                    out var ambientTransaction))
                return AllocateAvatarUid(ambientConnection, ambientTransaction);

            database ??= GameDatabase.CreateDefault();
            using (var connection = database.OpenConnection())
            {
                using (var transaction = connection.BeginTransaction())
                {
                    var avatarUid = AllocateAvatarUid(connection, transaction);
                    transaction.Commit();
                    return avatarUid;
                }
            }
        }

        internal static long AllocateAvatarUid(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            EnsureAvatarUidSequence(connection, transaction);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"INSERT INTO {AvatarUidSequenceTable} DEFAULT VALUES;";
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT last_insert_rowid();";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        internal static void EnsureAvatarUidSequence(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
CREATE TABLE IF NOT EXISTS {AvatarUidSequenceTable} (
    avatar_uid INTEGER PRIMARY KEY AUTOINCREMENT
);";
                command.ExecuteNonQuery();
            }

            var detailMax = ReadMaxUid(connection, transaction, "character_avatar_detail", "item_uid");
            if (detailMax <= 0)
                return;

            var sequenceMax = ReadMaxUid(connection, transaction, AvatarUidSequenceTable, "avatar_uid");
            if (sequenceMax >= detailMax)
                return;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
INSERT OR IGNORE INTO {AvatarUidSequenceTable} (avatar_uid)
VALUES (@avatarUid);";
                command.Parameters.AddWithValue("@avatarUid", detailMax);
                command.ExecuteNonQuery();
            }
        }

        internal static void Delete(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long avatarUid)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM character_avatar_detail WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@itemUid", avatarUid);
                command.ExecuteNonQuery();
            }
        }

        private static byte[] CopyFixed(byte[] source, int length)
        {
            var result = new byte[length];
            if (source != null)
                Buffer.BlockCopy(source, 0, result, 0, Math.Min(source.Length, length));
            return result;
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
