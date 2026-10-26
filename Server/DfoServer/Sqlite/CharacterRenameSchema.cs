using Microsoft.Data.Sqlite;

namespace DfoServer.Sqlite
{
    internal static class CharacterRenameSchema
    {
        internal const string Sql = @"
CREATE TABLE IF NOT EXISTS character_rename_history (
    character_id INTEGER NOT NULL,
    changed_at INTEGER NOT NULL,
    old_name BLOB NOT NULL,
    new_name BLOB NOT NULL,
    source_item INTEGER NOT NULL,
    PRIMARY KEY(character_id, changed_at),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_character_rename_old_name
    ON character_rename_history(old_name, changed_at);";

        internal static void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = Sql;
            cmd.ExecuteNonQuery();
        }
    }
}
