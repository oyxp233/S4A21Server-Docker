using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Pets;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class PetConsumableTransactionSelfTest
    {
        private const int AccountId = 984300;
        private const int CharacterId = 984301;
        private const int PetFoodItemTemplateId = 24;
        private const int EquippedPetItemTemplateId = 100330649;
        private const int CreatureKey = 1;
        private const short PetFoodSlot = 189;
        private const int InitialSatiety = 40;
        private const int FedSatiety = 70;

        public static int Run()
        {
            var failures = 0;
            try
            {
                CheckItemUpdateRollback(ref failures);
                CheckItemDeleteRollback(ref failures);
                CheckCreatureUpdateRollbackAndRecovery(ref failures);
                CheckDungeonElapsedCommitAndAnchor(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] pet consumable transaction selftest threw: " + ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "PetConsumableTransactionSelfTest OK"
                    : "PetConsumableTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckItemUpdateRollback(ref int failures)
        {
            using (var fixture = new Fixture(initialFoodCount: 2))
            {
                fixture.CreateItemUpdateFailureTrigger();
                var committed = PetConsumableCommitService.TryCommit(
                    fixture.Lease,
                    InventoryListType.Pet,
                    PetFoodSlot,
                    PetFoodItemTemplateId,
                    out var mutation);

                Check(
                    "pet food item UPDATE failure rejects the transaction",
                    !committed && mutation == null,
                    ref failures);
                Check(
                    "pet food item UPDATE failure rolls back food and satiety",
                    fixture.HasOnlineState(2, InitialSatiety)
                    && fixture.HasPersistedState(2, InitialSatiety)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void CheckItemDeleteRollback(ref int failures)
        {
            using (var fixture = new Fixture(initialFoodCount: 1))
            {
                fixture.CreateItemDeleteFailureTrigger();
                var committed = PetConsumableCommitService.TryCommit(
                    fixture.Lease,
                    InventoryListType.Pet,
                    PetFoodSlot,
                    PetFoodItemTemplateId,
                    out var mutation);

                Check(
                    "pet food item DELETE failure rejects the transaction",
                    !committed && mutation == null,
                    ref failures);
                Check(
                    "pet food item DELETE failure rolls back food and satiety",
                    fixture.HasOnlineState(1, InitialSatiety)
                    && fixture.HasPersistedState(1, InitialSatiety)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void CheckCreatureUpdateRollbackAndRecovery(
            ref int failures)
        {
            using (var fixture = new Fixture(initialFoodCount: 1))
            {
                fixture.CreateCreatureUpdateFailureTrigger();
                var failed = PetConsumableCommitService.TryCommit(
                    fixture.Lease,
                    InventoryListType.Pet,
                    PetFoodSlot,
                    PetFoodItemTemplateId,
                    out var failedMutation);

                Check(
                    "creature detail UPDATE failure rejects the transaction",
                    !failed && failedMutation == null,
                    ref failures);
                Check(
                    "creature detail UPDATE failure rolls back food and satiety",
                    fixture.HasOnlineState(1, InitialSatiety)
                    && fixture.HasPersistedState(1, InitialSatiety)
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                var recovered = PetConsumableCommitService.TryCommit(
                    fixture.Lease,
                    InventoryListType.Pet,
                    PetFoodSlot,
                    PetFoodItemTemplateId,
                    out var recoveredMutation);

                Check(
                    "pet food transaction retries after fault recovery",
                    recovered
                    && recoveredMutation != null
                    && recoveredMutation.RemainingStackCount == 0
                    && recoveredMutation.PetSatietyBefore == InitialSatiety
                    && recoveredMutation.PetSatietyAfter == FedSatiety,
                    ref failures);
                Check(
                    "fault recovery consumes and feeds exactly once",
                    fixture.HasOnlineState(0, FedSatiety)
                    && fixture.HasPersistedState(0, FedSatiety)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void CheckDungeonElapsedCommitAndAnchor(
            ref int failures)
        {
            using (var client = new TcpClient())
            {
                var session = new EnhancedClientSession(
                    client,
                    new GamePacketHeader());
                session.Player.CharacterId = CharacterId;

                using (var fixture = new Fixture(
                    initialFoodCount: 1,
                    sessionId: session.SessionId))
                {
                    var originalAnchor = DateTime.UtcNow.AddSeconds(-61);
                    session.Player.PetCreatureSatietyDungeonStartUtc =
                        originalAnchor;
                    session.Player.PetCreatureSatietyDungeonId = 1001;
                    fixture.CreateCreatureUpdateFailureTrigger();

                    var failed = PetCreatureRuntimeService
                        .TryCommitDungeonElapsedBeforeMutation(
                            session,
                            fixture.Lease,
                            "selftest_pet_consumable_elapsed",
                            continueTiming: true);
                    Check(
                        "dungeon elapsed failure preserves the timing anchor",
                        !failed
                        && session.Player.PetCreatureSatietyDungeonStartUtc
                            == originalAnchor
                        && fixture.ReadOnlineSatiety() == InitialSatiety
                        && fixture.ReadPersistedSatiety() == InitialSatiety,
                        ref failures);

                    fixture.DropFailureTriggers();
                    var recovered = PetCreatureRuntimeService
                        .TryCommitDungeonElapsedBeforeMutation(
                            session,
                            fixture.Lease,
                            "selftest_pet_consumable_elapsed_retry",
                            continueTiming: true);
                    var onlineSatiety = fixture.ReadOnlineSatiety();
                    Check(
                        "dungeon elapsed retries after persistence recovery",
                        recovered
                        && session.Player.PetCreatureSatietyDungeonStartUtc
                            > originalAnchor
                        && onlineSatiety < InitialSatiety
                        && fixture.ReadPersistedSatiety() == onlineSatiety
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }
            }
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
            internal Fixture(
                int initialFoodCount,
                Guid sessionId = default)
            {
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "pet-consumable-transaction-"
                        + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(
                    DatabasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(Database);

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    Database);
                var food = ItemCore.Create(
                    ItemCore.KindCreatureConsumable,
                    PetFoodItemTemplateId);
                food.Count = initialFoodCount;
                if (!inventory.SetItem(
                        InventoryListType.Pet,
                        PetFoodSlot,
                        food))
                {
                    throw new InvalidOperationException(
                        "unable to seed pet food fixture");
                }

                var equippedPet = ItemCore.Create(
                    ItemCore.KindCreature,
                    EquippedPetItemTemplateId);
                equippedPet.Value = CreatureKey;
                if (!inventory.SetItem(
                        InventoryListType.Equipment,
                        PetInventoryLayout.CreatureEquipSlot,
                        equippedPet))
                {
                    throw new InvalidOperationException(
                        "unable to seed equipped pet fixture");
                }

                inventory.CreatureDetails.PutDirty(new CreatureDetail
                {
                    Uid = CreatureKey,
                    Stomach = InitialSatiety,
                    FieldAfterValue32 = 1,
                });

                var seedLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                {
                    throw new InvalidOperationException(
                        "unable to persist pet consumable fixture");
                }

                Lease = InventoryContext.Register(
                    sessionId == Guid.Empty ? Guid.NewGuid() : sessionId,
                    CharacterId,
                    inventory);
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }

            internal bool HasOnlineState(int foodCount, int satiety)
            {
                return (Lease.Inventory.GetItem(
                            InventoryListType.Pet,
                            PetFoodSlot)?.Count ?? 0) == foodCount
                    && Lease.Inventory.CreatureDetails
                        .GetDetail(CreatureKey)?.Stomach == satiety;
            }

            internal bool HasPersistedState(int foodCount, int satiety)
            {
                using (var connection = Database.OpenConnection())
                {
                    var inventory = InventoryService.LoadFromDb(
                        connection,
                        CharacterId,
                        AccountId,
                        Database);
                    return (inventory.GetItem(
                                InventoryListType.Pet,
                                PetFoodSlot)?.Count ?? 0) == foodCount
                        && inventory.CreatureDetails
                            .GetDetail(CreatureKey)?.Stomach == satiety;
                }
            }

            internal bool HasNoDirtyState()
            {
                return Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.CreatureDetails.DirtyDetailUids.Count == 0;
            }

            internal int ReadOnlineSatiety()
            {
                return Lease.Inventory.CreatureDetails
                    .GetDetail(CreatureKey)?.Stomach ?? -1;
            }

            internal int ReadPersistedSatiety()
            {
                using (var connection = Database.OpenConnection())
                {
                    var inventory = InventoryService.LoadFromDb(
                        connection,
                        CharacterId,
                        AccountId,
                        Database);
                    return inventory.CreatureDetails
                        .GetDetail(CreatureKey)?.Stomach ?? -1;
                }
            }

            internal void CreateItemUpdateFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_pet_consumable_item_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Pet}
 AND OLD.slot_index = {PetFoodSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected pet consumable item update failure');
END;");
            }

            internal void CreateItemDeleteFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_pet_consumable_item_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Pet}
 AND OLD.slot_index = {PetFoodSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected pet consumable item delete failure');
END;");
            }

            internal void CreateCreatureUpdateFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_pet_consumable_creature_update
BEFORE UPDATE OF field04 ON character_creatures
WHEN OLD.character_id = {CharacterId}
 AND OLD.creature_key = {CreatureKey}
BEGIN
    SELECT RAISE(ABORT, 'injected pet consumable creature update failure');
END;");
            }

            internal void DropFailureTriggers()
            {
                ExecuteNonQuery(@"
DROP TRIGGER IF EXISTS fail_pet_consumable_item_update;
DROP TRIGGER IF EXISTS fail_pet_consumable_item_delete;
DROP TRIGGER IF EXISTS fail_pet_consumable_creature_update;");
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
VALUES(@aid, 'pet-consumable-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                        command.Parameters.AddWithValue("@aid", AccountId);
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.Parameters.AddWithValue(
                            "@name",
                            Encoding.UTF8.GetBytes(
                                "PetConsumableTransaction"));
                        command.ExecuteNonQuery();
                    }
                });
            }
        }
    }
}
