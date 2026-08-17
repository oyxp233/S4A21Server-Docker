using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Sqlite
{
    internal static class LegacyInventoryItemCoreMigration
    {
        private static readonly string[] LegacyTables =
        {
            "character_items",
            "account_cargo_items",
            "character_titlebook",
            "character_equipped_entries",
            "character_new_items",
            "item_audit_log",
            "character_achievement_chunks",
            "character_pet_welcome_cache",
            "character_sort_item_locks",
        };

        internal sealed class Result
        {
            internal long BeforeVersion { get; set; }

            internal long AfterVersion { get; set; }

            internal long PaddedItemCoreRows { get; set; }

            internal int DroppedLegacyTables { get; set; }
        }

        internal static bool CanApply(SqliteConnection connection)
        {
            if (TableExists(connection, null, "schema_metadata"))
                return false;

            if (TableExists(connection, null, "mailbox_attachments"))
                return true;

            foreach (var tableName in LegacyTables)
            {
                if (TableExists(connection, null, tableName))
                    return true;
            }

            return false;
        }

        internal static Result Apply(
            SqliteConnection connection,
            string schemaSql)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrWhiteSpace(schemaSql))
                throw new ArgumentException("schema SQL is required.", nameof(schemaSql));
            if (!CanApply(connection))
                throw new InvalidOperationException("数据库不是可识别的旧库存结构，拒绝执行主动迁移。");

            var result = new Result
            {
                BeforeVersion = SqliteMigrations.ReadVersion(connection),
            };

            using (var transaction = connection.BeginTransaction())
            {
                ExecuteSql(connection, transaction, schemaSql);
                result.PaddedItemCoreRows = CountLegacyItemCoreRows(connection, transaction);

                SqliteMigrations.ApplyExpandItemCoreTo99(connection, transaction);
                result.DroppedLegacyTables = DropLegacyTables(connection, transaction);
                SqliteMigrations.MarkCurrent(connection, transaction);

                transaction.Commit();
            }

            result.AfterVersion = SqliteMigrations.ReadVersion(connection);
            return result;
        }

        private static long CountLegacyItemCoreRows(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            long count = 0;
            count += CountRows(connection, transaction, "character_new_items");
            count += CountRows(connection, transaction, "character_inventory_items");
            count += CountRows(connection, transaction, "account_inventory_items");
            count += CountRows(connection, transaction, "character_titlebook_items");
            count += CountRows(connection, transaction, "mailbox_attachments");
            return count;
        }

        private static long CountRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName)
        {
            if (!TableExists(connection, transaction, tableName))
                return 0;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    $"SELECT COUNT(*) FROM {tableName} WHERE item_core IS NOT NULL AND length(item_core) = 82;";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static int DropLegacyTables(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            var dropped = 0;
            foreach (var tableName in LegacyTables)
            {
                if (!TableExists(connection, transaction, tableName))
                    continue;

                ExecuteSql(connection, transaction, $"DROP TABLE IF EXISTS {tableName};");
                dropped++;
            }

            return dropped;
        }

        private static bool TableExists(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type = 'table' AND name = @name;";
                command.Parameters.AddWithValue("@name", tableName);
                return Convert.ToInt32(command.ExecuteScalar()) != 0;
            }
        }

        private static void ExecuteSql(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }
    }
}
