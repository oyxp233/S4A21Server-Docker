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
    internal static class PetCreatureSatietyTransactionSelfTest
    {
        private const int AccountId = 984600;
        private const int CharacterId = 984601;
        private const int PetItemTemplateId = 0x0000F62F;
        private const int CreatureKey = 123;

        public static int Run()
        {
            var failures = 0;
            try
            {
                CheckTownRecoveryRollback(ref failures);
                CheckDeathRollback(ref failures);
                CheckRevivalRollback(ref failures);
                CheckDeathPrecheckAdvancesAnchor(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] pet creature satiety transaction selftest threw: "
                    + ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "PetCreatureSatietyTransactionSelfTest OK"
                    : "PetCreatureSatietyTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckTownRecoveryRollback(ref int failures)
        {
            using (var fixture = new Fixture(initialSatiety: 40))
            {
                var start = DateTime.UtcNow.AddSeconds(-360);
                var end = DateTime.UtcNow;
                fixture.CreateCreatureUpdateFailureTrigger();
                var failed = PetCreatureSatietyCommitService.TryCommitTownElapsed(
                    fixture.Lease,
                    start,
                    end,
                    out _);
                Check(
                    "pet town recovery failure rolls back satiety",
                    !failed
                    && fixture.HasOnlineAndPersistedSatiety(40)
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                var recovered = PetCreatureSatietyCommitService.TryCommitTownElapsed(
                    fixture.Lease,
                    start,
                    end,
                    out var update);
                Check(
                    "pet town recovery retries after persistence recovery",
                    recovered
                    && update.After == 41
                    && fixture.HasOnlineAndPersistedSatiety(41)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void CheckDeathRollback(ref int failures)
        {
            using (var fixture = new Fixture(initialSatiety: 1))
            {
                var start = DateTime.UtcNow.AddSeconds(-61);
                var end = DateTime.UtcNow;
                fixture.CreateCreatureUpdateFailureTrigger();
                var failed = PetCreatureSatietyCommitService.TryCommitDungeonDeath(
                    fixture.Lease,
                    start,
                    end,
                    out _);
                Check(
                    "pet death persistence failure keeps the creature alive",
                    !failed
                    && fixture.HasOnlineAndPersistedSatiety(1)
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                var recovered = PetCreatureSatietyCommitService.TryCommitDungeonDeath(
                    fixture.Lease,
                    start,
                    end,
                    out var update);
                Check(
                    "pet death retries and commits after recovery",
                    recovered
                    && update.After == 0
                    && fixture.HasOnlineAndPersistedSatiety(0)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void CheckRevivalRollback(ref int failures)
        {
            using (var fixture = new Fixture(initialSatiety: 0))
            {
                fixture.CreateCreatureUpdateFailureTrigger();
                var failed = PetCreatureSatietyCommitService.TryCommitRevival(
                    fixture.Lease,
                    out _);
                Check(
                    "pet revival persistence failure keeps the creature dead",
                    !failed
                    && fixture.HasOnlineAndPersistedSatiety(0)
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                var recovered = PetCreatureSatietyCommitService.TryCommitRevival(
                    fixture.Lease,
                    out var update);
                Check(
                    "pet revival retries and commits after recovery",
                    recovered
                    && update.Revived
                    && update.After == 1
                    && fixture.HasOnlineAndPersistedSatiety(1)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void CheckDeathPrecheckAdvancesAnchor(
            ref int failures)
        {
            using (var client = new TcpClient())
            {
                var session = new EnhancedClientSession(
                    client,
                    new GamePacketHeader());
                session.Player.CharacterId = CharacterId;
                using (var fixture = new Fixture(
                    initialSatiety: 40,
                    sessionId: session.SessionId))
                {
                    var originalAnchor = DateTime.UtcNow.AddSeconds(-61);
                    var checkNow = DateTime.UtcNow;
                    session.Player.PetCreatureSatietyDungeonStartUtc =
                        originalAnchor;
                    session.Player.PetCreatureSatietyDungeonId = 1001;

                    var died = PetCreatureRuntimeService.CheckDungeonDeathAsync(
                            session,
                            "selftest_satiety_anchor",
                            checkNow)
                        .GetAwaiter()
                        .GetResult();
                    var afterPrecheck = fixture.ReadOnlineSatiety();
                    Check(
                        "pet death precheck advances anchor after non-death commit",
                        !died
                        && session.Player.PetCreatureSatietyDungeonStartUtc
                            == checkNow
                        && afterPrecheck < 40
                        && fixture.ReadPersistedSatiety() == afterPrecheck,
                        ref failures);

                    var repeated = PetCreatureSatietyCommitService
                        .TryCommitDungeonElapsed(
                            fixture.Lease,
                            session.Player.PetCreatureSatietyDungeonStartUtc,
                            checkNow,
                            out var repeatedUpdate);
                    Check(
                        "pet death precheck does not charge the same interval twice",
                        repeated
                        && !repeatedUpdate.Changed
                        && fixture.HasOnlineAndPersistedSatiety(afterPrecheck)
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
                int initialSatiety,
                Guid sessionId = default)
            {
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "pet-creature-satiety-"
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
                        "unable to seed pet satiety equipment");
                }

                inventory.CreatureDetails.PutDirty(new CreatureDetail
                {
                    Uid = CreatureKey,
                    Stomach = (byte)initialSatiety,
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
                        "unable to persist pet satiety fixture");
                }

                Lease = InventoryContext.Register(
                    sessionId == Guid.Empty ? Guid.NewGuid() : sessionId,
                    CharacterId,
                    inventory);
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }

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

            internal bool HasOnlineAndPersistedSatiety(int expected)
            {
                return ReadOnlineSatiety() == expected
                    && ReadPersistedSatiety() == expected;
            }

            internal bool HasNoDirtyState()
            {
                return Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.CreatureDetails.DirtyDetailUids.Count == 0;
            }

            internal void CreateCreatureUpdateFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_pet_satiety_creature_update
BEFORE UPDATE OF field04 ON character_creatures
WHEN OLD.character_id = {CharacterId}
 AND OLD.creature_key = {CreatureKey}
BEGIN
    SELECT RAISE(ABORT, 'injected pet satiety update failure');
END;");
            }

            internal void DropFailureTriggers()
            {
                ExecuteNonQuery(@"
DROP TRIGGER IF EXISTS fail_pet_satiety_creature_update;");
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
VALUES(@aid, 'pet-creature-satiety', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                        command.Parameters.AddWithValue("@aid", AccountId);
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.Parameters.AddWithValue(
                            "@name",
                            Encoding.UTF8.GetBytes(
                                "PetCreatureSatiety"));
                        command.ExecuteNonQuery();
                    }
                });
            }
        }
    }
}
