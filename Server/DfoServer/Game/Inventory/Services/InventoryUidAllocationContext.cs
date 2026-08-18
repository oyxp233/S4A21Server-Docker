using System;
using System.IO;
using System.Threading;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryUidAllocationContext
    {
        private static readonly AsyncLocal<Scope> Current = new AsyncLocal<Scope>();

        internal static IDisposable Enter(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            var scope = new Scope(connection, transaction, Current.Value);
            Current.Value = scope;
            return scope;
        }

        internal static bool TryGet(
            IGameDatabase database,
            out SqliteConnection connection,
            out SqliteTransaction transaction)
        {
            var scope = Current.Value;
            if (scope == null || !MatchesDatabase(scope.Connection, database))
            {
                connection = null;
                transaction = null;
                return false;
            }

            connection = scope.Connection;
            transaction = scope.Transaction;
            return true;
        }

        private static bool MatchesDatabase(
            SqliteConnection connection,
            IGameDatabase database)
        {
            if (database == null)
                return true;
            if (connection == null)
                return false;
            if (string.IsNullOrWhiteSpace(database.DatabasePath))
                return true;

            var currentPath = connection.DataSource;
            if (string.IsNullOrWhiteSpace(currentPath))
                return false;
            if (string.Equals(database.DatabasePath, currentPath, StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                return string.Equals(
                    Path.GetFullPath(database.DatabasePath),
                    Path.GetFullPath(currentPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly Scope _previous;
            private bool _disposed;

            internal Scope(
                SqliteConnection connection,
                SqliteTransaction transaction,
                Scope previous)
            {
                Connection = connection;
                Transaction = transaction;
                _previous = previous;
            }

            internal SqliteConnection Connection { get; }

            internal SqliteTransaction Transaction { get; }

            public void Dispose()
            {
                if (_disposed)
                    return;

                Current.Value = _previous;
                _disposed = true;
            }
        }
    }
}
