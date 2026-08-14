using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryRollbackRecoveryService
    {
        internal static void ReloadOnlineInventory(
            string connectionString,
            InventoryLease lease)
        {
            if (string.IsNullOrWhiteSpace(connectionString) || lease?.Inventory == null)
                return;

            lock (lease.SyncRoot)
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var reloaded = InventoryService.LoadFromDb(
                    connection,
                    lease.CharacterId,
                    lease.AccountId,
                    lease.Inventory.Database);
                InventoryContext.TryReplaceCurrentLease(
                    lease,
                    reloaded,
                    out _);
            }
        }
    }
}
