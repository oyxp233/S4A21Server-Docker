using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Sqlite
{
    // 新数据库基线迁移器。
    // 旧项目 v1-v52 迁移链已经清理，只作为本次基线设计的历史依据。
    internal static class SqliteMigrations
    {
        internal const string BaselineId = "86jp-database-v1";
        internal const int BaselineVersion = 1;

        // 后续新增功能从 v2 开始追加。迁移只能依赖 SQL/数据库基础设施，不能调用业务 Service。
        private static readonly IReadOnlyList<MigrationStep> Steps =
            Array.Empty<MigrationStep>();

        internal static int CurrentVersion =>
            Steps.Count == 0 ? BaselineVersion : Steps[Steps.Count - 1].Version;

        internal static void MarkCurrent(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO schema_metadata (
    singleton_id, baseline_id, schema_version, created_at, updated_at
) VALUES (
    1, @baselineId, @schemaVersion, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
)
ON CONFLICT(singleton_id) DO UPDATE SET
    baseline_id = excluded.baseline_id,
    schema_version = excluded.schema_version,
    updated_at = CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@baselineId", BaselineId);
                command.Parameters.AddWithValue("@schemaVersion", CurrentVersion);
                command.ExecuteNonQuery();
            }

            SetUserVersion(connection, transaction, CurrentVersion);
        }

        internal static void Apply(SqliteConnection connection)
        {
            var metadata = ReadMetadata(connection);
            if (!string.Equals(metadata.BaselineId, BaselineId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"数据库不是 86JP 新基线（需要 baseline_id={BaselineId}）。" +
                    "请先备份并移走旧数据库，让服务端按当前代码创建新库。" +
                    "历史 v1-v52 迁移不会在服务启动时执行。");
            }

            var version = ReadVersion(connection);
            if (version > CurrentVersion || metadata.SchemaVersion > CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"数据库 schema v{Math.Max(version, metadata.SchemaVersion)} 高于当前服务支持的 " +
                    $"v{CurrentVersion}。");
            }

            if (version != metadata.SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"数据库 schema 元数据不一致: user_version={version}, " +
                    $"schema_metadata.schema_version={metadata.SchemaVersion}。");
            }

            foreach (var step in Steps)
            {
                if (step.Version <= version)
                    continue;
                if (step.Version != version + 1)
                {
                    throw new InvalidOperationException(
                        $"数据库迁移版本不连续: current={version}, next={step.Version}。");
                }

                using (var transaction = connection.BeginTransaction())
                {
                    step.Apply(connection, transaction);
                    WriteVersion(connection, transaction, step.Version);
                    transaction.Commit();
                }

                version = step.Version;
                FileLogger.Log($"[Db] migration v{step.Version} applied: {step.Name}");
            }
        }

        internal static long ReadVersion(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version;";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static (string BaselineId, int SchemaVersion) ReadMetadata(
            SqliteConnection connection)
        {
            using (var exists = connection.CreateCommand())
            {
                exists.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type = 'table' AND name = 'schema_metadata';";
                if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
                    return (string.Empty, 0);
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT baseline_id, schema_version
FROM schema_metadata
WHERE singleton_id = 1;";
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return (string.Empty, 0);

                    return (
                        reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
                }
            }
        }

        private static void SetUserVersion(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int version)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"PRAGMA user_version = {version};";
                command.ExecuteNonQuery();
            }
        }

        private static void WriteVersion(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int version)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE schema_metadata
SET schema_version = @schemaVersion,
    updated_at = CURRENT_TIMESTAMP
WHERE singleton_id = 1 AND baseline_id = @baselineId;";
                command.Parameters.AddWithValue("@schemaVersion", version);
                command.Parameters.AddWithValue("@baselineId", BaselineId);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("数据库基线元数据丢失，无法写入迁移版本。");
            }

            SetUserVersion(connection, transaction, version);
        }

        private sealed class MigrationStep
        {
            internal MigrationStep(
                int version,
                string name,
                Action<SqliteConnection, SqliteTransaction> apply)
            {
                Version = version;
                Name = name ?? throw new ArgumentNullException(nameof(name));
                Apply = apply ?? throw new ArgumentNullException(nameof(apply));
            }

            internal int Version { get; }

            internal string Name { get; }

            internal Action<SqliteConnection, SqliteTransaction> Apply { get; }
        }
    }
}
