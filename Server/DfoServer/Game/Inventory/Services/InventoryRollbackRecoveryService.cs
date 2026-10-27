using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryRollbackRecoveryService
    {
        internal static bool ReloadOnlineInventory(
            string connectionString,
            InventoryLease lease)
        {
            if (string.IsNullOrWhiteSpace(connectionString) || lease?.Inventory == null)
                return false;

            lock (lease.SyncRoot)
            {
                lease.Inventory.MarkPersistenceUntrusted("RollbackReload");
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                var reloaded = InventoryService.LoadFromDb(
                    connection,
                    lease.CharacterId,
                    lease.AccountId,
                    lease.Inventory.Database);
                return InventoryContext.TryReplaceCurrentLease(
                    lease,
                    reloaded,
                    out _);
            }
        }
    }
}
