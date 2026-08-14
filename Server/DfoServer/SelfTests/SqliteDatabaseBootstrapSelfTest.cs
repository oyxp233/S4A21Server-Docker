using DfoServer.Infrastructure;
using DfoServer.Sqlite;
using DfoServer.Game.Accounts;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    internal static class SqliteDatabaseBootstrapSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo-bootstrap-" + Guid.NewGuid().ToString("N") + ".db");
            var currentDatabasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo-bootstrap-current-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                Check("database starts absent", !File.Exists(databasePath), ref failures);

                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);

                Check("new database file is created", File.Exists(databasePath), ref failures);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    Check(
                        "new database is marked as baseline v1",
                        ReadInt64(connection, "PRAGMA user_version;") == SqliteMigrations.CurrentVersion,
                        ref failures);
                    Check(
                        "new database has the expected baseline id",
                        ReadInt64(
                            connection,
                            "SELECT COUNT(*) FROM schema_metadata WHERE singleton_id=1 AND baseline_id='86jp-database-v1' AND schema_version=1;") == 1,
                        ref failures);
                    Check(
                        "current schema tables are present",
                        ReadInt64(
                            connection,
                            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';") == 67,
                        ref failures);
                    Check(
                        "current schema indexes are present",
                        ReadInt64(
                            connection,
                            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%';") == 22,
                        ref failures);
                    Check(
                        "sqlite integrity check passes",
                        string.Equals(ReadString(connection, "PRAGMA integrity_check;"), "ok", StringComparison.OrdinalIgnoreCase),
                        ref failures);
                    Check(
                        "sqlite foreign key check passes",
                        ReadInt64(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;") == 0,
                        ref failures);
                    Check(
                        "retired legacy tables are absent",
                        ReadInt64(connection, @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type='table'
  AND name IN (
      'character_items', 'account_cargo_items', 'item_audit_log',
      'character_titlebook', 'character_achievement_chunks',
      'character_equipped_entries', 'character_pet_welcome_cache',
      'character_sort_item_locks');") == 0,
                        ref failures);
                    Check(
                        "inventory owner columns are not persisted",
                        ReadInt64(connection, @"
SELECT COUNT(*)
FROM pragma_table_info('character_inventory_items')
WHERE name IN ('owner_scope', 'owner_id');") == 0
                        && ReadInt64(connection, @"
SELECT COUNT(*)
FROM pragma_table_info('account_inventory_items')
WHERE name IN ('character_id', 'list_type');") == 0,
                        ref failures);
                    Check(
                        "new database contains no seeded player account",
                        ReadInt64(
                            connection,
                            "SELECT COUNT(*) FROM accounts;") == 0,
                        ref failures);
                    Check(
                        "server protocol defaults are initialized",
                        ReadInt64(
                            connection,
                            "SELECT COUNT(*) FROM get_userinfo_template WHERE id=1 AND seed_character_id=0 AND gate_or_count1=32;") == 1,
                        ref failures);
                }

                var accountRepository = new SqliteAccountRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var accountId = accountRepository.Create("10038", string.Empty);
                Check(
                    "first login can create the local account",
                    accountId == 1 && accountRepository.GetByMid("10038") != null,
                    ref failures);

                var secondConnectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Check(
                    "repeated initialization reuses the same database",
                    string.Equals(connectionString, secondConnectionString, StringComparison.Ordinal),
                    ref failures);

                using (var currentConnection = new SqliteConnection(
                    SqliteDatabaseBootstrap.BuildConnectionString(currentDatabasePath)))
                {
                    currentConnection.Open();
                    using (var command = currentConnection.CreateCommand())
                    {
                        command.CommandText = "CREATE TABLE legacy_marker(id INTEGER PRIMARY KEY);";
                        command.ExecuteNonQuery();
                    }
                }

                var rejectedLegacyDatabase = false;
                try
                {
                    SqliteDatabaseBootstrap.Initialize(
                        currentDatabasePath,
                        ServerPaths.SchemaFilePath);
                }
                catch (InvalidOperationException ex)
                {
                    rejectedLegacyDatabase = ex.Message.Contains("新基线");
                }
                Check(
                    "database without baseline id is rejected",
                    rejectedLegacyDatabase,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] sqlite bootstrap threw: " + ex);
                failures++;
            }
            finally
            {
                DeleteIfExists(databasePath);
                DeleteIfExists(databasePath + "-wal");
                DeleteIfExists(databasePath + "-shm");
                DeleteIfExists(currentDatabasePath);
                DeleteIfExists(currentDatabasePath + "-wal");
                DeleteIfExists(currentDatabasePath + "-shm");
            }

            Console.WriteLine(
                failures == 0
                    ? "[PASS] sqlite database bootstrap self-test"
                    : $"[FAIL] sqlite database bootstrap self-test failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static long ReadInt64(SqliteConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static string ReadString(SqliteConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToString(command.ExecuteScalar());
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine(condition ? "[PASS] " + name : "[FAIL] " + name);
            if (!condition)
                failures++;
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
