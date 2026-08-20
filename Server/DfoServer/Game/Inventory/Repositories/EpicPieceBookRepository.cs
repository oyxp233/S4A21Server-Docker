using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    internal static class EpicPieceBookRepository
    {
        internal const string AccountColumnName = "epic_piece_counts";

        internal static byte[] LoadBlob(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId)
        {
            if (connection == null || accountId <= 0)
                return Array.Empty<byte>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
SELECT {AccountColumnName}
FROM accounts
WHERE account_id = @accountId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? Array.Empty<byte>()
                    : (byte[])value;
            }
        }

        internal static void SaveBlob(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            byte[] blob)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (accountId <= 0)
                return;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
UPDATE accounts
SET {AccountColumnName} = @blob
WHERE account_id = @accountId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@blob", blob ?? Array.Empty<byte>());
                command.ExecuteNonQuery();
            }
        }
    }
}
