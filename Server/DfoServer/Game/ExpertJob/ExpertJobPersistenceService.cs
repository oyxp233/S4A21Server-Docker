using System;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.ExpertJob
{
    public sealed class ExpertJobPersistenceService
    {
        private readonly string _connectionString;

        internal ExpertJobPersistenceService(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        internal ExpertJobPersistenceService(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        internal bool Save(
            InventoryLease requesterLease,
            InventoryLease ownerLease,
            Func<SqliteConnection, SqliteTransaction, bool> saveExpertJobState)
        {
            if (requesterLease == null
                || ownerLease == null
                || saveExpertJobState == null)
                return false;

            try
            {
                bool saved;
                if (ReferenceEquals(requesterLease, ownerLease))
                {
                    lock (requesterLease.SyncRoot)
                        saved = SaveLocked(requesterLease, null, saveExpertJobState);
                }
                else
                {
                    var first = requesterLease.CharacterId < ownerLease.CharacterId
                        ? requesterLease
                        : ownerLease;
                    var second = ReferenceEquals(first, requesterLease)
                        ? ownerLease
                        : requesterLease;
                    lock (first.SyncRoot)
                    lock (second.SyncRoot)
                        saved = SaveLocked(requesterLease, ownerLease, saveExpertJobState);
                }

                if (!saved)
                    ReloadOnlineInventoriesAfterRollback(requesterLease, ownerLease);
                return saved;
            }
            catch (Exception ex)
            {
                ReloadOnlineInventoriesAfterRollback(requesterLease, ownerLease);
                FileLogger.Log(
                    $"[ExpertJobPersistence] save failed requester={requesterLease.CharacterId} " +
                    $"owner={ownerLease.CharacterId}: {ex.Message}");
                return false;
            }
        }

        private bool SaveLocked(
            InventoryLease requesterLease,
            InventoryLease ownerLease,
            Func<SqliteConnection, SqliteTransaction, bool> saveExpertJobState)
        {
            var persisted = false;
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var saved = InventoryPersistenceService.SaveDirtyInTransaction(
                            connection,
                            transaction,
                            requesterLease)
                        && (ownerLease == null
                            || InventoryPersistenceService.SaveDirtyInTransaction(
                                connection,
                                transaction,
                                ownerLease))
                        && saveExpertJobState(connection, transaction);
                    if (saved)
                    {
                        transaction.Commit();
                        persisted = true;
                    }
                }
            }

            if (!persisted)
                return false;

            requesterLease.Inventory.ClearDirtyState();
            ownerLease?.Inventory.ClearDirtyState();
            return true;
        }

        private void ReloadOnlineInventoriesAfterRollback(
            InventoryLease requesterLease,
            InventoryLease ownerLease)
        {
            TryReloadOnlineInventory(requesterLease);
            if (!ReferenceEquals(requesterLease, ownerLease))
                TryReloadOnlineInventory(ownerLease);
        }

        private void TryReloadOnlineInventory(InventoryLease lease)
        {
            try
            {
                InventoryRollbackRecoveryService.ReloadOnlineInventory(
                    _connectionString,
                    lease);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ExpertJobPersistence] inventory rollback reload failed " +
                    $"cid={lease?.CharacterId ?? 0}: {ex.Message}");
            }
        }
    }
}
