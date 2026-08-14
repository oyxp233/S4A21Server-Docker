using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Infrastructure
{
    public interface IGameDatabase
    {
        string DatabasePath { get; }

        string SchemaFilePath { get; }

        string ConnectionString { get; }

        SqliteConnection OpenConnection();

        T Read<T>(Func<SqliteConnection, T> action);

        T Write<T>(
            Func<SqliteConnection, SqliteTransaction, T> action,
            bool immediate = true);

        void Write(
            Action<SqliteConnection, SqliteTransaction> action,
            bool immediate = true);
    }
}
