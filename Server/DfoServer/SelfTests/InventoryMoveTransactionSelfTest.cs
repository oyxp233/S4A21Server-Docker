using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class InventoryMoveTransactionSelfTest
    {
        private const int AccountId = 984700;
        private const int CharacterId = 984701;
        private const int ItemTemplateId = 1004;
        private const short SourceSlot = 105;
        private const short DestinationSlot = 106;

        public static int Run()
        {
            var failures = 0;
            try
            {
                CheckFullMoveFailuresAndRecovery(ref failures);
                CheckPartialMoveFailureAndRecovery(ref failures);
                CheckPetMoveProjection(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] inventory move transaction selftest threw: " + ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "InventoryMoveTransactionSelfTest OK"
                    : "InventoryMoveTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckFullMoveFailuresAndRecovery(
            ref int failures)
        {
            using (var fixture = new Fixture(initialCount: 2))
            {
                var invalid = InventoryMoveCommitService.TryCommit(
                    fixture.Lease,
                    BuildRequest(sourceSlot: 120, moveCount: 2),
                    characterJob: 0,
                    characterGrowType: 0,
                    out var invalidResult,
                    out var invalidPersistenceFailure);
                Check(
                    "inventory move preserves business rejection result",
                    !invalid
                    && invalidResult?.Error
                        == InventoryMoveServiceError.SourceNotFound
                    && !invalidPersistenceFailure
                    && fixture.HasOnlineAndPersistedState(2, 0),
                    ref failures);

                fixture.CreateDestinationInsertFailureTrigger();
                var insertFailed = InventoryMoveCommitService.TryCommit(
                    fixture.Lease,
                    BuildRequest(SourceSlot, 2),
                    0,
                    0,
                    out var insertResult,
                    out var insertPersistenceFailure);
                Check(
                    "inventory move destination INSERT failure rejects commit",
                    !insertFailed
                    && insertResult?.Success == true
                    && insertPersistenceFailure,
                    ref failures);
                Check(
                    "inventory move destination INSERT failure restores both slots",
                    fixture.HasOnlineAndPersistedState(2, 0)
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                fixture.CreateSourceDeleteFailureTrigger();
                var deleteFailed = InventoryMoveCommitService.TryCommit(
                    fixture.Lease,
                    BuildRequest(SourceSlot, 2),
                    0,
                    0,
                    out var deleteResult,
                    out var deletePersistenceFailure);
                Check(
                    "inventory move source DELETE failure rejects commit",
                    !deleteFailed
                    && deleteResult?.Success == true
                    && deletePersistenceFailure,
                    ref failures);
                Check(
                    "inventory move source DELETE failure restores both slots",
                    fixture.HasOnlineAndPersistedState(2, 0)
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                var recovered = InventoryMoveCommitService.TryCommit(
                    fixture.Lease,
                    BuildRequest(SourceSlot, 2),
                    0,
                    0,
                    out var recoveredResult,
                    out var recoveredPersistenceFailure);
                Check(
                    "inventory full move retries after persistence recovery",
                    recovered
                    && recoveredResult?.MoveCount == 2
                    && !recoveredPersistenceFailure
                    && fixture.HasOnlineAndPersistedState(0, 2)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void CheckPartialMoveFailureAndRecovery(
            ref int failures)
        {
            using (var fixture = new Fixture(initialCount: 2))
            {
                fixture.CreateSourceUpdateFailureTrigger();
                var failed = InventoryMoveCommitService.TryCommit(
                    fixture.Lease,
                    BuildRequest(SourceSlot, 1),
                    0,
                    0,
                    out var failedResult,
                    out var persistenceFailed);
                Check(
                    "inventory partial move source UPDATE failure rolls back",
                    !failed
                    && failedResult?.Success == true
                    && persistenceFailed
                    && fixture.HasOnlineAndPersistedState(2, 0)
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                var recovered = InventoryMoveCommitService.TryCommit(
                    fixture.Lease,
                    BuildRequest(SourceSlot, 1),
                    0,
                    0,
                    out var recoveredResult,
                    out var recoveredPersistenceFailure);
                Check(
                    "inventory partial move retries and applies once",
                    recovered
                    && recoveredResult?.MoveCount == 1
                    && !recoveredPersistenceFailure
                    && fixture.HasOnlineAndPersistedState(1, 1)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void CheckPetMoveProjection(ref int failures)
        {
            var request = new InventoryMoveRequest
            {
                SourceListType = InventoryListType.Pet,
                SourceSlotIndex = 48,
                DestinationListType = InventoryListType.Equipment,
                DestinationSlotIndex = PetInventoryLayout.CreatureEquipSlot,
                MoveCount = 1,
            };
            var creatureMove = new InventoryMoveServiceResult
            {
                Success = true,
                Mutated = true,
                MoveCount = 1,
                DestinationListType = InventoryListType.Equipment,
                DestinationSlotIndex = PetInventoryLayout.CreatureEquipSlot,
            };
            creatureMove.Changes.AddSlot(InventoryListType.Pet, 48);
            creatureMove.Changes.AddSlot(
                InventoryListType.Equipment,
                PetInventoryLayout.CreatureEquipSlot);
            var creatureProjection = InventoryHandler.CreateMoveAckResult(
                request,
                creatureMove);
            Check(
                "inventory move projects equipped creature refresh state",
                creatureProjection.PetCreatureStateChanged
                && !creatureProjection.PetItemStateChanged
                && creatureProjection.PetCreatureRefreshSlots.Contains(48)
                && creatureProjection.EquipmentRefreshSlots.Contains(
                    PetInventoryLayout.CreatureEquipSlot),
                ref failures);

            var artifactMove = new InventoryMoveServiceResult
            {
                Success = true,
                Mutated = true,
                MoveCount = 1,
                DestinationListType = InventoryListType.Equipment,
                DestinationSlotIndex = PetInventoryLayout.ArtifactRedEquipSlot,
            };
            artifactMove.Changes.AddSlot(InventoryListType.Pet, 49);
            artifactMove.Changes.AddSlot(
                InventoryListType.Equipment,
                PetInventoryLayout.ArtifactRedEquipSlot);
            request.SourceSlotIndex = 49;
            request.DestinationSlotIndex =
                PetInventoryLayout.ArtifactRedEquipSlot;
            var artifactProjection = InventoryHandler.CreateMoveAckResult(
                request,
                artifactMove);
            Check(
                "inventory move projects equipped pet artifact refresh state",
                !artifactProjection.PetCreatureStateChanged
                && artifactProjection.PetItemStateChanged
                && artifactProjection.PetCreatureRefreshSlots.Contains(49)
                && artifactProjection.EquipmentRefreshSlots.Contains(
                    PetInventoryLayout.ArtifactRedEquipSlot),
                ref failures);
        }

        private static InventoryMoveRequest BuildRequest(
            short sourceSlot,
            int moveCount)
        {
            return new InventoryMoveRequest
            {
                SourceListType = InventoryListType.Main,
                SourceSlotIndex = sourceSlot,
                MoveCount = moveCount,
                DestinationListType = InventoryListType.Main,
                DestinationSlotIndex = DestinationSlot,
            };
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                "[" + (condition ? "PASS" : "FAIL") + "] " + label);
            if (!condition)
                failures++;
        }

        private sealed class Fixture : IDisposable
        {
            internal Fixture(int initialCount)
            {
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "inventory-move-transaction-"
                        + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(
                    DatabasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(Database);

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    Database);
                inventory.SetListParam16(
                    InventoryListType.Main,
                    ItemSlotBoundService.MainExpandStageFull);
                var source = ItemCore.Create(
                    ItemCore.KindConsumable,
                    ItemTemplateId);
                source.Count = initialCount;
                if (!inventory.SetItem(
                        InventoryListType.Main,
                        SourceSlot,
                        source))
                {
                    throw new InvalidOperationException(
                        "unable to seed inventory move source");
                }

                var seedLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                {
                    throw new InvalidOperationException(
                        "unable to persist inventory move fixture");
                }

                Lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }

            internal bool HasOnlineAndPersistedState(
                int sourceCount,
                int destinationCount)
            {
                return HasState(
                        Lease.Inventory,
                        sourceCount,
                        destinationCount)
                    && HasPersistedState(sourceCount, destinationCount);
            }

            internal bool HasNoDirtyState()
            {
                return Lease.Inventory.DirtyListTypes.Count == 0;
            }

            internal void CreateDestinationInsertFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_inventory_move_destination_insert
BEFORE INSERT ON character_inventory_items
WHEN NEW.character_id = {CharacterId}
 AND NEW.list_type = {(int)InventoryListType.Main}
 AND NEW.slot_index = {DestinationSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected inventory move insert failure');
END;");
            }

            internal void CreateSourceDeleteFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_inventory_move_source_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {SourceSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected inventory move delete failure');
END;");
            }

            internal void CreateSourceUpdateFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_inventory_move_source_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {SourceSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected inventory move update failure');
END;");
            }

            internal void DropFailureTriggers()
            {
                ExecuteNonQuery(@"
DROP TRIGGER IF EXISTS fail_inventory_move_destination_insert;
DROP TRIGGER IF EXISTS fail_inventory_move_source_delete;
DROP TRIGGER IF EXISTS fail_inventory_move_source_update;");
            }

            public void Dispose()
            {
                try
                {
                    DropFailureTriggers();
                }
                catch
                {
                }

                InventoryContext.Unregister(
                    Lease.SessionId,
                    Lease.CharacterId);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    try
                    {
                        var path = DatabasePath + suffix;
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch
                    {
                    }
                }
            }

            private bool HasPersistedState(
                int sourceCount,
                int destinationCount)
            {
                using (var connection = Database.OpenConnection())
                {
                    var inventory = InventoryService.LoadFromDb(
                        connection,
                        CharacterId,
                        AccountId,
                        Database);
                    return HasState(
                        inventory,
                        sourceCount,
                        destinationCount);
                }
            }

            private static bool HasState(
                InventoryService inventory,
                int sourceCount,
                int destinationCount)
            {
                return (inventory.GetItem(
                            InventoryListType.Main,
                            SourceSlot)?.Count ?? 0) == sourceCount
                    && (inventory.GetItem(
                            InventoryListType.Main,
                            DestinationSlot)?.Count ?? 0) == destinationCount;
            }

            private void ExecuteNonQuery(string sql)
            {
                using (var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = DatabasePath,
                        ForeignKeys = true,
                    }.ToString()))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = sql;
                        command.ExecuteNonQuery();
                    }
                }
            }

            private static void SeedCharacter(IGameDatabase database)
            {
                database.Write((connection, transaction) =>
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, 'inventory-move-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                        command.Parameters.AddWithValue("@aid", AccountId);
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.Parameters.AddWithValue(
                            "@name",
                            Encoding.UTF8.GetBytes(
                                "InventoryMoveTransaction"));
                        command.ExecuteNonQuery();
                    }
                });
            }
        }
    }
}
