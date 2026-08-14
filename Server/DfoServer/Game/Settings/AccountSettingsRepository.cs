using System;
using Microsoft.Data.Sqlite;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Settings
{
    public sealed class AccountSettingsRepository
    {
        private readonly string _connStr;

        public AccountSettingsRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public AccountSettingsRepository(IGameDatabase database)
        {
            _connStr = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public AccountSettings Load(int accountId)
        {
            using (var conn = new SqliteConnection(_connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT main_game_option, quickchat_bank0, quickchat_bank1, hotkey_key_type, hotkey_slots FROM account_settings WHERE account_id=@aid", conn))
                {
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        return new AccountSettings
                        {
                            MainGameOption = r.IsDBNull(0) ? null : (byte[])r[0],
                            QuickchatBank0 = r.IsDBNull(1) ? null : (byte[])r[1],
                            QuickchatBank1 = r.IsDBNull(2) ? null : (byte[])r[2],
                            HotkeyKeyType = (byte)r.GetInt32(3),
                            HotkeySlots = r.IsDBNull(4) ? null : (byte[])r[4],
                        };
                    }
                }
            }
        }

        public void SaveMainOption(int accountId, byte[] blob)
        {
            Upsert(accountId, "main_game_option", blob);
        }

        internal static void SaveMainOption(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            byte[] blob)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO account_settings (account_id, main_game_option)
                    VALUES (@aid, @val)
                    ON CONFLICT(account_id) DO UPDATE SET main_game_option=@val";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@val", (object)blob ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        public void SaveHotkeySlots(int accountId, byte[] slots)
        {
            Upsert(accountId, "hotkey_slots", slots);
        }

        public void SaveQuickchatBank(int accountId, int bankIndex, byte[] blob)
        {
            var col = bankIndex == 0 ? "quickchat_bank0" : "quickchat_bank1";
            Upsert(accountId, col, blob);
        }

        private void Upsert(int accountId, string column, byte[] blob)
        {
            using (var conn = new SqliteConnection(_connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $@"
                        INSERT INTO account_settings (account_id, {column})
                        VALUES (@aid, @val)
                        ON CONFLICT(account_id) DO UPDATE SET {column}=@val";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@val", (object)blob ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
