using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace DfoServer.Infrastructure;

// Explicit composition-time bootstrap for independent feature databases.
// Existing unmarked files and unknown schemas are never adopted or rewritten.
internal static class FeatureDatabaseBootstrap
{
    internal static IGameDatabase Open(string path, string baseline, string schema)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Database path required.");
        path = Path.GetFullPath(path);
        bool existed = File.Exists(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var builder = new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true
        };
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(builder.ToString()) {
            Mode = existed ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: existed);
        const string metadata = "CREATE TABLE feature_metadata (singleton INTEGER PRIMARY KEY CHECK(singleton=1), baseline_id TEXT NOT NULL)";
        void Execute(string sql)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = sql; command.ExecuteNonQuery();
        }
        if (!existed)
        {
            Execute(metadata + ";" + schema + ";PRAGMA user_version=1;");
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO feature_metadata VALUES(1,@baseline);";
            insert.Parameters.AddWithValue("@baseline", baseline); insert.ExecuteNonQuery();
        }
        using var check = connection.CreateCommand(); check.Transaction = transaction;
        check.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(check.ExecuteScalar()) != 1) throw new InvalidDataException("Unsupported feature database version.");
        check.CommandText = "SELECT baseline_id FROM feature_metadata WHERE singleton=1;";
        if ((string)check.ExecuteScalar() != baseline) throw new InvalidDataException("Feature database baseline mismatch.");
        var expected = schema.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
            .Where(s => s.StartsWith("CREATE ", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        expected.Add(metadata);
        check.CommandText = "SELECT sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';";
        var actual = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = check.ExecuteReader()) while (reader.Read()) actual.Add(reader.GetString(0).Trim());
        if (!actual.SetEquals(expected)) throw new InvalidDataException("Feature database schema mismatch.");
        check.CommandText = "PRAGMA foreign_key_check;";
        using (var reader = check.ExecuteReader()) if (reader.Read()) throw new InvalidDataException("Feature database foreign key violation.");
        transaction.Commit();
        return GameDatabase.AttachInitialized(builder.ToString());
    }
}
