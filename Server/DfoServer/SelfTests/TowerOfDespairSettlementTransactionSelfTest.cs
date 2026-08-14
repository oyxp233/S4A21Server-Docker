using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class TowerOfDespairSettlementTransactionSelfTest
    {
        private const int RewardItemId = 1252;
        private const int TowerFloorOneDungeonId = 11008;
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine(
                "=== TOWER_OF_DESPAIR_SETTLEMENT_TRANSACTION selftest ===");

            var path = Path.Combine(
                Path.GetTempPath(),
                $"tower-settlement-transaction-{Guid.NewGuid():N}.db");
            var leases = new List<InventoryLease>();
            IGameDatabase database = null;
            try
            {
                database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                CheckInventoryFailureRollback(database, leases);
                CheckOutboxFailureRollback(database, leases);
                CheckOfflineRecovery(database, leases);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] unexpected exception: " + ex);
                _fail++;
            }
            finally
            {
                DropTrigger(database, "fail_tower_inventory_insert");
                DropTrigger(database, "fail_tower_outbox_commit");
                DropTrigger(database, "fail_tower_recovery_inventory_insert");
                foreach (var lease in leases)
                    InventoryContext.Unregister(
                        lease.SessionId,
                        lease.CharacterId);
                SqliteConnection.ClearAllPools();
                DeleteDatabase(path);
            }

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void CheckInventoryFailureRollback(
            IGameDatabase database,
            ICollection<InventoryLease> leases)
        {
            const int accountId = 952000;
            const int characterId = 952001;
            var lease = CreateLease(database, accountId, characterId);
            leases.Add(lease);
            var effectId = CreateEffectId(runId: 51001);
            var rewards = CreateRewards();
            var service = new DungeonPersistentEffectApplicationService(
                database.ConnectionString,
                database: database);

            CreateAbortTrigger(
                database,
                "fail_tower_inventory_insert",
                "BEFORE INSERT ON character_inventory_items",
                $"NEW.character_id = {characterId}");
            var failed = service.TryApplyTowerOfDespairSettlement(
                effectId,
                lease,
                lease.SessionId,
                TowerFloorOneDungeonId,
                rewards,
                out _,
                out _);
            Check(
                "inventory failure rolls back progress, reward and dirty lease",
                !failed
                && LoadProgress(database, characterId) == 0
                && LoadItemCount(database, accountId, characterId) == 0
                && lease.Inventory.CountMainItem(RewardItemId) == 0
                && lease.Inventory.DirtyListTypes.Count == 0
                && lease.Inventory.DirtyMainVirtualCountSlots.Count == 0
                && lease.Inventory.DirtyListParams.Count == 0
                && service.Outbox.Get(effectId)?.State
                    == DungeonPersistentEffectState.Failed);

            DropTrigger(database, "fail_tower_inventory_insert");
            InventoryPersistenceService.SaveAllDirty();
            Check(
                "failed settlement cannot be persisted later by dirty flush",
                LoadItemCount(database, accountId, characterId) == 0
                && LoadProgress(database, characterId) == 0);

            var retried = service.TryApplyTowerOfDespairSettlement(
                effectId,
                lease,
                lease.SessionId,
                TowerFloorOneDungeonId,
                rewards,
                out var retryResult,
                out _);
            Check(
                "retry commits progress, reward and durable effect exactly once",
                retried
                && retryResult?.NextFloor == 2
                && retryResult.GrantedRewards.Count == 1
                && LoadProgress(database, characterId) == 1
                && LoadItemCount(database, accountId, characterId) == 1
                && lease.Inventory.CountMainItem(RewardItemId) == 1
                && service.Outbox.Get(effectId)?.State
                    == DungeonPersistentEffectState.Committed);

            var replayed = new DungeonPersistentEffectApplicationService(
                    database.ConnectionString,
                    database: database)
                .TryApplyTowerOfDespairSettlement(
                    effectId,
                    lease,
                    lease.SessionId,
                    TowerFloorOneDungeonId,
                    rewards,
                    out var replayResult,
                    out _);
            Check(
                "committed replay returns the stored slots without duplicate grant",
                replayed
                && replayResult?.GrantedRewards.Count == 1
                && replayResult.GrantedRewards[0].Slot
                    == retryResult.GrantedRewards[0].Slot
                && LoadItemCount(database, accountId, characterId) == 1
                && lease.Inventory.CountMainItem(RewardItemId) == 1);

            var mismatched = service.TryApplyTowerOfDespairSettlement(
                effectId,
                lease,
                lease.SessionId,
                TowerFloorOneDungeonId,
                new[]
                {
                    new ClearRewardGenerator.CardReward
                    {
                        ItemId = RewardItemId,
                        StackCount = 2,
                    },
                },
                out _,
                out _);
            Check(
                "reusing an effect id with different rewards is rejected",
                !mismatched
                && LoadItemCount(database, accountId, characterId) == 1);
        }

        private static void CheckOutboxFailureRollback(
            IGameDatabase database,
            ICollection<InventoryLease> leases)
        {
            const int accountId = 952010;
            const int characterId = 952011;
            var lease = CreateLease(database, accountId, characterId);
            leases.Add(lease);
            var effectId = CreateEffectId(runId: 51002);
            var service = new DungeonPersistentEffectApplicationService(
                database.ConnectionString,
                database: database);

            CreateAbortTrigger(
                database,
                "fail_tower_outbox_commit",
                "BEFORE UPDATE ON dungeon_persistent_effect_outbox",
                $"NEW.character_id = {characterId} AND NEW.state = " +
                (int)DungeonPersistentEffectState.Committed);
            var failed = service.TryApplyTowerOfDespairSettlement(
                effectId,
                lease,
                lease.SessionId,
                TowerFloorOneDungeonId,
                CreateRewards(),
                out _,
                out _);
            Check(
                "outbox commit failure rolls back progress and inventory",
                !failed
                && LoadProgress(database, characterId) == 0
                && LoadItemCount(database, accountId, characterId) == 0
                && lease.Inventory.CountMainItem(RewardItemId) == 0
                && lease.Inventory.DirtyListTypes.Count == 0
                && service.Outbox.Get(effectId)?.State
                    == DungeonPersistentEffectState.Failed);

            DropTrigger(database, "fail_tower_outbox_commit");
            var retried = service.TryApplyTowerOfDespairSettlement(
                effectId,
                lease,
                lease.SessionId,
                TowerFloorOneDungeonId,
                CreateRewards(),
                out _,
                out _);
            Check(
                "outbox failure retry commits one progress and one reward",
                retried
                && LoadProgress(database, characterId) == 1
                && LoadItemCount(database, accountId, characterId) == 1);
        }

        private static void CheckOfflineRecovery(
            IGameDatabase database,
            ICollection<InventoryLease> leases)
        {
            const int accountId = 952020;
            const int characterId = 952021;
            var lease = CreateLease(database, accountId, characterId);
            leases.Add(lease);
            var effectId = CreateEffectId(runId: 51003);
            var service = new DungeonPersistentEffectApplicationService(
                database.ConnectionString,
                database: database);

            CreateAbortTrigger(
                database,
                "fail_tower_recovery_inventory_insert",
                "BEFORE INSERT ON character_inventory_items",
                $"NEW.character_id = {characterId}");
            var failed = service.TryApplyTowerOfDespairSettlement(
                effectId,
                lease,
                lease.SessionId,
                TowerFloorOneDungeonId,
                CreateRewards(),
                out _,
                out _);
            DropTrigger(database, "fail_tower_recovery_inventory_insert");
            InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
            leases.Remove(lease);

            var recovery = new DungeonPersistentEffectApplicationService(
                    database.ConnectionString,
                    database: database)
                .RecoverCharacter(characterId);
            Check(
                "character recovery completes a failed tower settlement offline",
                !failed
                && recovery.CommittedCount == 1
                && recovery.FailedCount == 0
                && LoadProgress(database, characterId) == 1
                && LoadItemCount(database, accountId, characterId) == 1);
        }

        private static IReadOnlyList<ClearRewardGenerator.CardReward>
            CreateRewards()
            => new[]
            {
                new ClearRewardGenerator.CardReward
                {
                    ItemId = RewardItemId,
                    StackCount = 1,
                },
            };

        private static DungeonEffectId CreateEffectId(long runId)
            => new DungeonEffectId(
                Guid.NewGuid(),
                DungeonPersistentEffectKinds.TowerOfDespairSettlementCommit,
                DungeonEffectScope.Player,
                runId);

        private static InventoryLease CreateLease(
            IGameDatabase database,
            int accountId,
            int characterId)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, @memberId, '');
INSERT INTO characters(character_id, account_id, name, level)
VALUES(@cid, @aid, @name, 86);";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue(
                        "@memberId",
                        "tower-transaction-" + accountId);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        "tower-transaction-" + characterId);
                    command.ExecuteNonQuery();
                }
            });

            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return InventoryContext.Register(Guid.NewGuid(), inventory);
            }
        }

        private static int LoadProgress(
            IGameDatabase database,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COALESCE(MAX(highest_cleared_floor), 0)
FROM character_tower_of_despair_progress
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static int LoadItemCount(
            IGameDatabase database,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return inventory.CountMainItem(RewardItemId);
            }
        }

        private static void CreateAbortTrigger(
            IGameDatabase database,
            string name,
            string timingAndTable,
            string condition)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = $@"
CREATE TRIGGER {name}
{timingAndTable}
WHEN {condition}
BEGIN
    SELECT RAISE(ABORT, 'injected tower settlement failure');
END;";
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void DropTrigger(
            IGameDatabase database,
            string name)
        {
            if (database == null)
                return;
            try
            {
                database.Write((connection, transaction) =>
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText =
                            $"DROP TRIGGER IF EXISTS {name};";
                        command.ExecuteNonQuery();
                    }
                });
            }
            catch
            {
            }
        }

        private static void DeleteDatabase(string path)
        {
            foreach (var candidate in new[]
            {
                path,
                path + "-wal",
                path + "-shm",
            })
            {
                try
                {
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
                catch
                {
                }
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok)
                _pass++;
            else
                _fail++;
        }
    }
}
