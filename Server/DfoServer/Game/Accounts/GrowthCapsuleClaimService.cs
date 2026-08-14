using System;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Accounts
{
    public enum GrowthCapsuleClaimStatus
    {
        Success,
        InsufficientExp,
        InventoryFull,
        PersistenceFailed,
        InvalidOwner,
    }

    public sealed class GrowthCapsuleClaimResult
    {
        public GrowthCapsuleClaimStatus Status { get; set; }
        public short AssignedSlot { get; set; } = -1;
        public int ItemId { get; set; }
        public int ItemCount { get; set; }
        public GrowthCapsuleSummary Summary { get; set; }
        public bool Success => Status == GrowthCapsuleClaimStatus.Success;
    }

    public sealed class GrowthCapsuleClaimService
    {
        private readonly string _connectionString;

        public GrowthCapsuleClaimService(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public GrowthCapsuleClaimService(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        internal GrowthCapsuleClaimResult Claim(InventoryLease lease)
        {
            var characterId = lease != null ? lease.CharacterId : 0;
            var accountId = lease != null ? lease.AccountId : 0;
            if (characterId <= 0
                || accountId <= 0
                || !InventoryContext.IsCurrentLease(
                    lease,
                    lease?.SessionId ?? Guid.Empty,
                    characterId))
            {
                return new GrowthCapsuleClaimResult
                {
                    Status = GrowthCapsuleClaimStatus.InvalidOwner,
                    Summary = GrowthCapsuleDataProvider.Calculate(0),
                };
            }

            try
            {
                lock (lease.SyncRoot)
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction())
                        {
                            if (!IsCharacterOwnedByAccount(connection, transaction, characterId, accountId))
                            {
                                return new GrowthCapsuleClaimResult
                                {
                                    Status = GrowthCapsuleClaimStatus.InvalidOwner,
                                    Summary = GrowthCapsuleDataProvider.Calculate(0),
                                };
                            }

                            var totalExp = GrowthCapsuleProgressRepository.LoadTotalExp(
                                connection, transaction, accountId);
                            var summary = GrowthCapsuleDataProvider.Calculate(totalExp);
                            if (totalExp < summary.RequiredExp)
                            {
                                return new GrowthCapsuleClaimResult
                                {
                                    Status = GrowthCapsuleClaimStatus.InsufficientExp,
                                    Summary = summary,
                                };
                            }

                            if (!InventoryRewardGrantService.TryCreateAndInsert(
                                    lease,
                                    GrowthCapsuleDataProvider.RewardItemId,
                                    ItemCreateReason.AdminGrant,
                                    GrowthCapsuleDataProvider.RewardItemCount,
                                    out var grant))
                            {
                                return new GrowthCapsuleClaimResult
                                {
                                    Status = GrowthCapsuleClaimStatus.InventoryFull,
                                    Summary = summary,
                                };
                            }

                            if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                throw new InvalidOperationException(
                                    "growth capsule reward inventory persistence returned false");
                            }

                            GrowthCapsuleProgressRepository.UpdateTotalExpInTransaction(
                                connection, transaction, accountId, 0);
                            if (!InventoryContext.IsCurrentLease(
                                    lease,
                                    lease.SessionId,
                                    characterId))
                            {
                                throw new InvalidOperationException(
                                    "growth capsule lease was replaced before commit");
                            }

                            transaction.Commit();
                            lease.Inventory.ClearDirtyState();
                            return new GrowthCapsuleClaimResult
                            {
                                Status = GrowthCapsuleClaimStatus.Success,
                                AssignedSlot = grant.SlotIndex,
                                ItemId = GrowthCapsuleDataProvider.RewardItemId,
                                ItemCount = GrowthCapsuleDataProvider.RewardItemCount,
                                Summary = GrowthCapsuleDataProvider.Calculate(0),
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GrowthCapsule] claim persistence failed "
                    + $"cid={characterId} aid={accountId}: {ex.Message}");
                try
                {
                    InventoryRollbackRecoveryService.ReloadOnlineInventory(
                        _connectionString,
                        lease);
                }
                catch (Exception reloadEx)
                {
                    FileLogger.Log(
                        $"[GrowthCapsule] claim rollback reload failed "
                        + $"cid={characterId} aid={accountId}: {reloadEx.Message}");
                }

                return new GrowthCapsuleClaimResult
                {
                    Status = GrowthCapsuleClaimStatus.PersistenceFailed,
                    Summary = LoadSummary(accountId),
                };
            }
        }

        private GrowthCapsuleSummary LoadSummary(int accountId)
        {
            try
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    return GrowthCapsuleDataProvider.Calculate(
                        GrowthCapsuleProgressRepository.LoadTotalExp(
                            connection,
                            null,
                            accountId));
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GrowthCapsule] claim summary reload failed "
                    + $"aid={accountId}: {ex.Message}");
                return GrowthCapsuleDataProvider.Calculate(0);
            }
        }

        private static bool IsCharacterOwnedByAccount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 1
FROM characters
WHERE character_id=@cid AND account_id=@aid AND delete_flag=0;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@aid", accountId);
                return command.ExecuteScalar() != null;
            }
        }
    }
}
