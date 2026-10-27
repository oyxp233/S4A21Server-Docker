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
            Func<SqliteConnection, SqliteTransaction, bool> apply,
            bool immediate = false)
        {
            if (lease == null) return false;
            lock (lease.SyncRoot)
                return TryCommitLocked(lease, operation, apply, immediate);
        }

        private static bool TryCommitLocked(InventoryLease lease, string operation,
            Func<SqliteConnection, SqliteTransaction, bool> apply, bool immediate)
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
            var attemptedInventory = lease.Inventory;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                attemptedInventory.MarkPersistenceUntrusted("InventoryCommitHasNoDatabase");
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
                    // Auction asset receipts must read and write under the same
                    // reserved writer. Other callers retain deferred behavior.
                    using (var transaction = connection.BeginTransaction(deferred: !immediate))
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
                // Still under the outer lease lock: no periodic/disconnect save
                // can observe the possibly mutated model before it is fenced.
                attemptedInventory.MarkPersistenceUntrusted("InventoryCommitFailed");
                FileLogger.Log(
                    $"[InventoryMutationCommit] commit failed "
                    + $"operation={operation ?? "unknown"} "
                    + $"cid={lease.CharacterId}: {ex.Message}");
            }

            try
            {
                if (!InventoryRollbackRecoveryService.ReloadOnlineInventory(
                    connectionString,
                    lease)) return false;
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
