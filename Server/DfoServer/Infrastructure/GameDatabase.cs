using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.Infrastructure
{
    public sealed class GameDatabase : IGameDatabase
    {
        public GameDatabase(string databasePath, string schemaFilePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("databasePath is empty", nameof(databasePath));
            if (string.IsNullOrWhiteSpace(schemaFilePath))
                throw new ArgumentException("schemaFilePath is empty", nameof(schemaFilePath));

            DatabasePath = Path.GetFullPath(databasePath);
            SchemaFilePath = Path.GetFullPath(schemaFilePath);
            ConnectionString = SqliteDatabaseBootstrap.Initialize(
                DatabasePath,
                SchemaFilePath);
        }

        private GameDatabase(
            string databasePath,
            string schemaFilePath,
            string connectionString)
        {
            DatabasePath = databasePath;
            SchemaFilePath = schemaFilePath;
            ConnectionString = connectionString;
        }

        public string DatabasePath { get; }

        public string SchemaFilePath { get; }

        public string ConnectionString { get; }

        public static GameDatabase CreateDefault()
        {
            return new GameDatabase(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);
        }

        // Compatibility adapter for already-bootstrapped test/legacy call sites.
        // Formal server composition must pass the shared IGameDatabase instead.
        internal static GameDatabase AttachInitialized(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException(
                    "connectionString is empty",
                    nameof(connectionString));
            }

            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource))
            {
                throw new ArgumentException(
                    "connectionString has no data source",
                    nameof(connectionString));
            }

            var databasePath = builder.DataSource == ":memory:"
                ? builder.DataSource
                : Path.GetFullPath(builder.DataSource);
            return new GameDatabase(
                databasePath,
                schemaFilePath: string.Empty,
                connectionString);
        }

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA busy_timeout=5000;";
                command.ExecuteNonQuery();
            }

            return connection;
        }

        public T Read<T>(Func<SqliteConnection, T> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            using (var connection = OpenConnection())
                return action(connection);
        }

        public T Write<T>(
            Func<SqliteConnection, SqliteTransaction, T> action,
            bool immediate = true)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(deferred: !immediate))
            {
                var result = action(connection, transaction);
                transaction.Commit();
                return result;
            }
        }

        public void Write(
            Action<SqliteConnection, SqliteTransaction> action,
            bool immediate = true)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            Write(
                (connection, transaction) =>
                {
                    action(connection, transaction);
                    return true;
                },
                immediate);
        }
    }
}
