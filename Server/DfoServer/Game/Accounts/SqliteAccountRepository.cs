using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace DfoServer.Game.Accounts
{
    public sealed class SqliteAccountRepository : IAccountRepository
    {
        internal const int SeriaLuckValueMax = 8;

        private readonly IGameDatabase _database;

        public SqliteAccountRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public SqliteAccountRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public AccountRecord GetById(int accountId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT account_id, m_id, password_hash, last_login_ip, last_login_at, created_at
                                    FROM accounts WHERE account_id = @id;";
                cmd.Parameters.AddWithValue("@id", accountId);
                using (var reader = cmd.ExecuteReader())
                    return reader.Read() ? Map(reader) : null;
            }
        }

        public AccountRecord GetByMid(string mId)
        {
            if (string.IsNullOrEmpty(mId)) return null;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT account_id, m_id, password_hash, last_login_ip, last_login_at, created_at
                                    FROM accounts WHERE m_id = @mid;";
                cmd.Parameters.AddWithValue("@mid", mId);
                using (var reader = cmd.ExecuteReader())
                    return reader.Read() ? Map(reader) : null;
            }
        }

        public int Create(string mId, string passwordHash)
        {
            if (string.IsNullOrEmpty(mId)) throw new ArgumentException("mId is empty", nameof(mId));

            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO accounts (m_id, password_hash) VALUES (@mid, @pwd);
                                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@mid", mId);
                cmd.Parameters.AddWithValue("@pwd", passwordHash ?? string.Empty);
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        public void UpdateLastLogin(int accountId, string ip, DateTime when)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE accounts SET last_login_ip = @ip, last_login_at = @at
                                    WHERE account_id = @id;";
                cmd.Parameters.AddWithValue("@ip", ip ?? string.Empty);
                cmd.Parameters.AddWithValue("@at", when.ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@id", accountId);
                cmd.ExecuteNonQuery();
            }
        }

        internal static int LoadSeriaLuckValue(SqliteConnection connection, SqliteTransaction transaction, int accountId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT seria_luck_value
FROM accounts
WHERE account_id = @accountId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                var raw = command.ExecuteScalar();
                return NormalizeSeriaLuckValue(raw == null || raw == DBNull.Value ? 0 : Convert.ToInt32(raw));
            }
        }

        internal static void UpdateSeriaLuckValue(SqliteConnection connection, SqliteTransaction transaction, int accountId, int value)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE accounts
SET seria_luck_value = @value
WHERE account_id = @accountId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@value", NormalizeSeriaLuckValue(value));
                command.ExecuteNonQuery();
            }
        }

        internal static int NormalizeSeriaLuckValue(int value)
        {
            if (value < 0)
                return 0;
            if (value > SeriaLuckValueMax)
                return SeriaLuckValueMax;
            return value;
        }

        private static AccountRecord Map(IDataRecord r)
        {
            return new AccountRecord
            {
                AccountId = r.GetInt32(0),
                MId = r.GetString(1),
                PasswordHash = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                LastLoginIp = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                LastLoginAt = r.IsDBNull(4) ? (DateTime?)null : ParseDate(r.GetString(4)),
                CreatedAt = ParseDate(r.GetString(5)),
            };
        }

        private static DateTime ParseDate(string text)
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;
            return DateTime.MinValue;
        }

        private SqliteConnection Open()
        {
            return _database.OpenConnection();
        }
    }
}
