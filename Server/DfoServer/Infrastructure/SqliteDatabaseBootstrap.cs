using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Infrastructure
{
    public static class SqliteDatabaseBootstrap
    {
        private static readonly IReadOnlyList<(string ColumnName, string ColumnDefinition)> AccountExtraColumns =
            new[]
            {
                ("soul_10100115", "INTEGER NOT NULL DEFAULT 0"),
                ("soul_10100116", "INTEGER NOT NULL DEFAULT 0"),
                ("soul_10099773", "INTEGER NOT NULL DEFAULT 0"),
                ("soul_10099774", "INTEGER NOT NULL DEFAULT 0"),
                ("soul_10099775", "INTEGER NOT NULL DEFAULT 0"),
                ("epic_piece_counts", "BLOB NOT NULL DEFAULT X''"),
            };

        private static readonly object InitLock = new object();
        private static readonly HashSet<string> InitializedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 每个数据库文件每进程只初始化一次。
        // 文件不存在时创建当前完整基线；文件存在时只校验新基线并执行新体系增量迁移。
        // 没有正确 baseline_id 的历史数据库会被拒绝，不在服务启动路径中转换。
        public static string Initialize(string databasePath, string schemaFilePath)
        {
            var connectionString = BuildConnectionString(databasePath);
            var key = Path.GetFullPath(databasePath);

            lock (InitLock)
            {
                if (InitializedPaths.Contains(key))
                    return connectionString;

                var databaseExists = File.Exists(key);
                EnsureDatabaseDirectory(key);

                try
                {
                    using (var conn = new SqliteConnection(connectionString))
                    {
                        conn.Open();
                        if (databaseExists)
                            ValidateAndUpgradeCurrentDatabase(conn);
                        else
                            CreateCurrentDatabase(conn, File.ReadAllText(schemaFilePath));

                        EnsureAccountExtraColumns(conn);

                        // WAL 持久生效: 读写不互锁, 消除快速切角色时 database is locked
                        using (var walCmd = conn.CreateCommand())
                        {
                            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                            walCmd.ExecuteScalar();
                        }
                    }
                }
                catch
                {
                    if (!databaseExists)
                        DeleteDatabaseFiles(key);
                    throw;
                }

                InitializedPaths.Add(key);
            }

            return connectionString;
        }

        public static string BuildConnectionString(string databasePath)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                ForeignKeys = true,
                DefaultTimeout = 5
            }.ConnectionString;
        }

        private static void CreateCurrentDatabase(SqliteConnection connection, string schemaSql)
        {
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = schemaSql;
                    command.ExecuteNonQuery();
                }

                DfoServer.Sqlite.SqliteMigrations.MarkCurrent(connection, transaction);
                transaction.Commit();
            }

            FileLogger.Log(
                $"[Db] created current schema v{DfoServer.Sqlite.SqliteMigrations.CurrentVersion}: " +
                connection.DataSource);
        }

        private static void ValidateAndUpgradeCurrentDatabase(SqliteConnection connection)
        {
            DfoServer.Sqlite.SqliteMigrations.Apply(connection);
        }

        private static void EnsureAccountExtraColumns(SqliteConnection connection)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(accounts);";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        existingColumns.Add(reader.GetString(1));
                }
            }

            if (existingColumns.Count == 0)
                return;

            var missingColumns = new List<(string ColumnName, string ColumnDefinition)>();
            foreach (var column in AccountExtraColumns)
            {
                if (!existingColumns.Contains(column.ColumnName))
                    missingColumns.Add(column);
            }

            if (missingColumns.Count == 0)
                return;

            using (var transaction = connection.BeginTransaction())
            {
                foreach (var column in missingColumns)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = $"ALTER TABLE accounts ADD COLUMN {column.ColumnName} {column.ColumnDefinition};";
                        command.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }

            FileLogger.Log(
                "[Db] accounts extra columns added: " +
                string.Join(", ", missingColumns.ConvertAll(column => column.ColumnName)));
        }

        private static void EnsureDatabaseDirectory(string databasePath)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            DeleteIfExists(databasePath);
            DeleteIfExists(databasePath + "-wal");
            DeleteIfExists(databasePath + "-shm");
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
                // 保留原始初始化异常；残留文件可由下一次启动日志定位。
            }
        }
    }
}
