using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class PetCreatureExperienceTransactionSelfTest
    {
        private const int AccountId = 984500;
        private const int CharacterId = 984501;
        private const int PetItemTemplateId = 0x0000F62F;
        private const int CreatureKey = 123;
        private const int ConsumedFatigue = 5;

        public static int Run()
        {
            var failures = 0;
            try
            {
                using (var fixture = new Fixture())
                {
                    var room = new RoomState();
                    Check(
                        "pet experience room reservation is acquired once",
                        room.TryBeginPetExperienceGrant()
                        && !room.TryBeginPetExperienceGrant(),
                        ref failures);

                    fixture.CreateCreatureUpdateFailureTrigger();
                    var failed = PetCreatureExperienceCommitService.TryCommit(
                        fixture.Lease,
                        ConsumedFatigue,
                        out var failedUpdate);
                    Check(
                        "pet experience CreatureDetail UPDATE failure rejects commit",
                        !failed && !failedUpdate.Changed,
                        ref failures);
                    room.CancelPetExperienceGrant();
                    Check(
                        "pet experience failure rolls back detail and releases room",
                        !room.PetExperienceGranted
                        && room.TryBeginPetExperienceGrant()
                        && fixture.ReadOnlineExperience() == 0
                        && fixture.ReadPersistedExperience() == 0
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    var recovered = PetCreatureExperienceCommitService.TryCommit(
                        fixture.Lease,
                        ConsumedFatigue,
                        out var recoveredUpdate);
                    Check(
                        "pet experience retries after persistence recovery",
                        recovered
                        && recoveredUpdate.Changed
                        && recoveredUpdate.BeforeExperience == 0
                        && recoveredUpdate.AfterExperience == ConsumedFatigue,
                        ref failures);

                    room.CompletePetExperienceGrant();
                    Check(
                        "pet experience completion blocks duplicate room grant",
                        room.PetExperienceGranted
                        && !room.TryBeginPetExperienceGrant(),
                        ref failures);
                    Check(
                        "pet experience recovery grants exactly once",
                        fixture.ReadOnlineExperience() == ConsumedFatigue
                        && fixture.ReadPersistedExperience() == ConsumedFatigue
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] pet creature experience transaction selftest threw: "
                    + ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "PetCreatureExperienceTransactionSelfTest OK"
                    : "PetCreatureExperienceTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
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
            internal Fixture()
            {
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "pet-creature-experience-"
                        + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(
                    DatabasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(Database);

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    Database);
                var equippedPet = ItemCore.Create(
                    ItemCore.KindCreature,
                    PetItemTemplateId);
                equippedPet.Value = CreatureKey;
                if (!inventory.SetItem(
                        InventoryListType.Equipment,
                        PetInventoryLayout.CreatureEquipSlot,
                        equippedPet))
                {
                    throw new InvalidOperationException(
                        "unable to seed pet experience equipment");
                }

                inventory.CreatureDetails.PutDirty(new CreatureDetail
                {
                    Uid = CreatureKey,
                    Stomach = 40,
                    Exp = 0,
                    Level = 1,
                });
                var seedLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                {
                    throw new InvalidOperationException(
                        "unable to persist pet experience fixture");
                }

                Lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }

            internal int ReadOnlineExperience()
            {
                return Lease.Inventory.CreatureDetails
                    .GetDetail(CreatureKey)?.Exp ?? -1;
            }

            internal int ReadPersistedExperience()
            {
                using (var connection = Database.OpenConnection())
                {
                    var inventory = InventoryService.LoadFromDb(
                        connection,
                        CharacterId,
                        AccountId,
                        Database);
                    return inventory.CreatureDetails
                        .GetDetail(CreatureKey)?.Exp ?? -1;
                }
            }

            internal bool HasNoDirtyState()
            {
                return Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.CreatureDetails.DirtyDetailUids.Count == 0;
            }

            internal void CreateCreatureUpdateFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_pet_experience_creature_update
BEFORE UPDATE OF progress_value ON character_creatures
WHEN OLD.character_id = {CharacterId}
 AND OLD.creature_key = {CreatureKey}
BEGIN
    SELECT RAISE(ABORT, 'injected pet experience update failure');
END;");
            }

            internal void DropFailureTriggers()
            {
                ExecuteNonQuery(@"
DROP TRIGGER IF EXISTS fail_pet_experience_creature_update;");
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
VALUES(@aid, 'pet-creature-experience', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                        command.Parameters.AddWithValue("@aid", AccountId);
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.Parameters.AddWithValue(
                            "@name",
                            Encoding.UTF8.GetBytes(
                                "PetCreatureExperience"));
                        command.ExecuteNonQuery();
                    }
                });
            }
        }
    }
}
