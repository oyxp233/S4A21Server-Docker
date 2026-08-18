using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class OnlineInventoryMutationCommitCoordinator
    {
        internal static bool TryCommit(
            InventoryLease lease,
            string operation)
        {
            return TryCommit(
                lease,
                operation,
                (connection, transaction) => true);
        }

        internal static bool TryCommit(
            InventoryLease lease,
            string operation,
            Func<SqliteConnection, SqliteTransaction, bool> apply)
        {
            if (lease == null
                || lease.Inventory == null
                || !InventoryContext.IsCurrentLease(
                    lease,
                    lease.SessionId,
                    lease.CharacterId))
            {
                return false;
            }

            var connectionString = lease.Inventory.Database?.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                FileLogger.Log(
                    $"[InventoryMutationCommit] rollback unavailable "
                    + $"operation={operation ?? "unknown"} "
                    + $"cid={lease.CharacterId}: inventory has no database");
                return false;
            }

            try
            {
                var database = lease.Inventory.Database;
                using (var connection = database.OpenConnection())
                {
                    // 让 UID 序列表分配复用同一个库存提交事务，避免同链路里再开写连接。
                    using (var transaction = connection.BeginTransaction(deferred: true))
                    {
                        using (InventoryUidAllocationContext.Enter(connection, transaction))
                        {
                            bool applied;
                            lock (lease.SyncRoot)
                            {
                                applied = apply == null || apply(connection, transaction);
                                if (!applied
                                    || !InventoryPersistenceService.SaveDirtyInTransaction(
                                        connection,
                                        transaction,
                                        lease))
                                {
                                    throw new InvalidOperationException("inventory mutation was not committed");
                                }

                                transaction.Commit();
                                lease.Inventory.ClearDirtyState();
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[InventoryMutationCommit] commit failed "
                    + $"operation={operation ?? "unknown"} "
                    + $"cid={lease.CharacterId}: {ex.Message}");
            }

            try
            {
                InventoryRollbackRecoveryService.ReloadOnlineInventory(
                    connectionString,
                    lease);
                FileLogger.Log(
                    $"[InventoryMutationCommit] reloaded after failed commit "
                    + $"operation={operation ?? "unknown"} "
                    + $"cid={lease.CharacterId}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[InventoryMutationCommit] rollback reload failed "
                    + $"operation={operation ?? "unknown"} "
                    + $"cid={lease.CharacterId}: {ex.Message}");
            }

            return false;
        }
    }
}
