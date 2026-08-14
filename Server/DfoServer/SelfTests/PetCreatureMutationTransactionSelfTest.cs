using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class PetCreatureMutationTransactionSelfTest
    {
        private const int AccountId = 984400;
        private const int CharacterId = 984401;
        private const short EggSlot = 48;
        private const short RenameCardSlot = 190;
        private const int EggItemTemplateId = 0x0000F62E;
        private const int HatchedPetItemTemplateId = 0x0000F62F;
        private const int RenameCardItemTemplateId = 25;
        private const int CreatureKey = 123;
        private static readonly byte[] OldName = Encoding.UTF8.GetBytes("Before");
        private static readonly byte[] NewName = Encoding.UTF8.GetBytes("After");

        public static int Run()
        {
            var failures = 0;
            try
            {
                CheckHatchItemRollback(ref failures);
                CheckHatchDetailRollbackAndRecovery(ref failures);
                CheckRenameItemRollback(ref failures);
                CheckRenameDetailRollbackAndRecovery(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] pet creature mutation transaction selftest threw: "
                    + ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "PetCreatureMutationTransactionSelfTest OK"
                    : "PetCreatureMutationTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckHatchItemRollback(ref int failures)
        {
            using (var fixture = Fixture.CreateHatch())
            {
                fixture.CreateHatchItemUpdateFailureTrigger();
                var committed = PetCreatureMutationCommitService.TryCommitHatch(
                    fixture.Lease,
                    InventoryListType.Pet,
                    EggSlot,
                    EggItemTemplateId,
                    out var result);

                Check(
                    "pet hatch item UPDATE failure rejects the transaction",
                    !committed && result == null,
                    ref failures);
                Check(
                    "pet hatch item UPDATE failure restores egg and detail",
                    fixture.HasOnlineHatchState(hatched: false)
                    && fixture.HasPersistedHatchState(hatched: false)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void CheckHatchDetailRollbackAndRecovery(
            ref int failures)
        {
            using (var fixture = Fixture.CreateHatch())
            {
                fixture.CreateCreatureInsertFailureTrigger();
                var failed = PetCreatureMutationCommitService.TryCommitHatch(
                    fixture.Lease,
                    InventoryListType.Pet,
                    EggSlot,
                    EggItemTemplateId,
                    out var failedResult);

                Check(
                    "pet hatch CreatureDetail INSERT failure rejects the transaction",
                    !failed && failedResult == null,
                    ref failures);
                Check(
                    "pet hatch CreatureDetail INSERT failure restores egg and detail",
                    fixture.HasOnlineHatchState(hatched: false)
                    && fixture.HasPersistedHatchState(hatched: false)
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                var recovered = PetCreatureMutationCommitService.TryCommitHatch(
                    fixture.Lease,
                    InventoryListType.Pet,
                    EggSlot,
                    EggItemTemplateId,
                    out var recoveredResult);
                Check(
                    "pet hatch retries after persistence recovery",
                    recovered
                    && recoveredResult != null
                    && recoveredResult.HatchedItemTemplateId
                        == HatchedPetItemTemplateId
                    && recoveredResult.PetSerialOrHandle == CreatureKey,
                    ref failures);
                Check(
                    "pet hatch recovery transforms egg and creates detail once",
                    fixture.HasOnlineHatchState(hatched: true)
                    && fixture.HasPersistedHatchState(hatched: true)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void CheckRenameItemRollback(ref int failures)
        {
            using (var fixture = Fixture.CreateRename())
            {
                fixture.CreateRenameItemDeleteFailureTrigger();
                var committed = PetCreatureMutationCommitService.TryCommitRename(
                    fixture.Lease,
                    BuildRenameRequest(),
                    out var result);

                Check(
                    "pet rename item DELETE failure rejects the transaction",
                    !committed && result == null,
                    ref failures);
                Check(
                    "pet rename item DELETE failure restores card and name",
                    fixture.HasOnlineRenameState(1, OldName)
                    && fixture.HasPersistedRenameState(1, OldName)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void CheckRenameDetailRollbackAndRecovery(
            ref int failures)
        {
            using (var fixture = Fixture.CreateRename())
            {
                fixture.CreateCreatureUpdateFailureTrigger();
                var failed = PetCreatureMutationCommitService.TryCommitRename(
                    fixture.Lease,
                    BuildRenameRequest(),
                    out var failedResult);

                Check(
                    "pet rename CreatureDetail UPDATE failure rejects the transaction",
                    !failed && failedResult == null,
                    ref failures);
                Check(
                    "pet rename CreatureDetail UPDATE failure restores card and name",
                    fixture.HasOnlineRenameState(1, OldName)
                    && fixture.HasPersistedRenameState(1, OldName)
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                var recovered = PetCreatureMutationCommitService.TryCommitRename(
                    fixture.Lease,
                    BuildRenameRequest(),
                    out var recoveredResult);
                Check(
                    "pet rename retries after persistence recovery",
                    recovered
                    && recoveredResult != null
                    && recoveredResult.SourceRemainingCount == 0
                    && recoveredResult.NameBytes.SequenceEqual(NewName),
                    ref failures);
                Check(
                    "pet rename recovery consumes card and changes name once",
                    fixture.HasOnlineRenameState(0, NewName)
                    && fixture.HasPersistedRenameState(0, NewName)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static PetCreatureRenameRequest BuildRenameRequest()
        {
            return new PetCreatureRenameRequest
            {
                SourceListType = InventoryListType.Pet,
                SourceSlotIndex = RenameCardSlot,
                NameBytes = NewName,
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
            private Fixture(bool hatch)
            {
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "pet-creature-mutation-"
                        + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(
                    DatabasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(Database);

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    Database);
                if (hatch)
                    SeedHatch(inventory);
                else
                    SeedRename(inventory);

                var seedLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                {
                    throw new InvalidOperationException(
                        "unable to persist pet creature mutation fixture");
                }

                Lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }

            internal static Fixture CreateHatch()
                => new Fixture(hatch: true);

            internal static Fixture CreateRename()
                => new Fixture(hatch: false);

            internal bool HasOnlineHatchState(bool hatched)
            {
                return HasHatchState(Lease.Inventory, hatched);
            }

            internal bool HasPersistedHatchState(bool hatched)
            {
                using (var connection = Database.OpenConnection())
                {
                    return HasHatchState(
                        InventoryService.LoadFromDb(
                            connection,
                            CharacterId,
                            AccountId,
                            Database),
                        hatched);
                }
            }

            internal bool HasOnlineRenameState(
                int cardCount,
                byte[] expectedName)
            {
                return HasRenameState(
                    Lease.Inventory,
                    cardCount,
                    expectedName);
            }

            internal bool HasPersistedRenameState(
                int cardCount,
                byte[] expectedName)
            {
                using (var connection = Database.OpenConnection())
                {
                    return HasRenameState(
                        InventoryService.LoadFromDb(
                            connection,
                            CharacterId,
                            AccountId,
                            Database),
                        cardCount,
                        expectedName);
                }
            }

            internal bool HasNoDirtyState()
            {
                return Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.CreatureDetails.DirtyDetailUids.Count == 0;
            }

            internal void CreateHatchItemUpdateFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_pet_hatch_item_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Pet}
 AND OLD.slot_index = {EggSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected pet hatch item update failure');
END;");
            }

            internal void CreateCreatureInsertFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_pet_creature_insert
BEFORE INSERT ON character_creatures
WHEN NEW.character_id = {CharacterId}
 AND NEW.creature_key = {CreatureKey}
BEGIN
    SELECT RAISE(ABORT, 'injected pet creature insert failure');
END;");
            }

            internal void CreateRenameItemDeleteFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_pet_rename_item_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Pet}
 AND OLD.slot_index = {RenameCardSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected pet rename item delete failure');
END;");
            }

            internal void CreateCreatureUpdateFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_pet_creature_update
BEFORE UPDATE OF creature_text ON character_creatures
WHEN OLD.character_id = {CharacterId}
 AND OLD.creature_key = {CreatureKey}
BEGIN
    SELECT RAISE(ABORT, 'injected pet creature update failure');
END;");
            }

            internal void DropFailureTriggers()
            {
                ExecuteNonQuery(@"
DROP TRIGGER IF EXISTS fail_pet_hatch_item_update;
DROP TRIGGER IF EXISTS fail_pet_creature_insert;
DROP TRIGGER IF EXISTS fail_pet_rename_item_delete;
DROP TRIGGER IF EXISTS fail_pet_creature_update;");
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

            private static void SeedHatch(InventoryService inventory)
            {
                var egg = ItemCore.Create(
                    ItemCore.KindCreature,
                    EggItemTemplateId);
                egg.Value = CreatureKey;
                if (!inventory.SetItem(
                        InventoryListType.Pet,
                        EggSlot,
                        egg))
                {
                    throw new InvalidOperationException(
                        "unable to seed pet hatch fixture");
                }
            }

            private static void SeedRename(InventoryService inventory)
            {
                var equippedPet = ItemCore.Create(
                    ItemCore.KindCreature,
                    HatchedPetItemTemplateId);
                equippedPet.Value = CreatureKey;
                if (!inventory.SetItem(
                        InventoryListType.Equipment,
                        PetInventoryLayout.CreatureEquipSlot,
                        equippedPet))
                {
                    throw new InvalidOperationException(
                        "unable to seed equipped pet fixture");
                }

                var renameCard = ItemCore.Create(
                    ItemCore.KindCreatureConsumable,
                    RenameCardItemTemplateId);
                renameCard.Count = 1;
                if (!inventory.SetItem(
                        InventoryListType.Pet,
                        RenameCardSlot,
                        renameCard))
                {
                    throw new InvalidOperationException(
                        "unable to seed pet rename card fixture");
                }

                inventory.CreatureDetails.PutDirty(new CreatureDetail
                {
                    Uid = CreatureKey,
                    Stomach = 40,
                    FieldAfterValue32 = 1,
                    NameBytes = OldName,
                });
            }

            private static bool HasHatchState(
                InventoryService inventory,
                bool hatched)
            {
                var item = inventory.GetItem(
                    InventoryListType.Pet,
                    EggSlot);
                if (item == null
                    || item.ItemId != (hatched
                        ? HatchedPetItemTemplateId
                        : EggItemTemplateId))
                {
                    return false;
                }

                var hasDetail = inventory.CreatureDetails
                    .TryGetDetail(CreatureKey, out var detail)
                    && detail != null;
                return hasDetail == hatched;
            }

            private static bool HasRenameState(
                InventoryService inventory,
                int cardCount,
                byte[] expectedName)
            {
                var currentCount = inventory.GetItem(
                    InventoryListType.Pet,
                    RenameCardSlot)?.Count ?? 0;
                var detail = inventory.CreatureDetails.GetDetail(CreatureKey);
                return currentCount == cardCount
                    && detail != null
                    && detail.NameBytes.SequenceEqual(expectedName);
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
VALUES(@aid, 'pet-creature-mutation', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                        command.Parameters.AddWithValue("@aid", AccountId);
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.Parameters.AddWithValue(
                            "@name",
                            Encoding.UTF8.GetBytes(
                                "PetCreatureMutation"));
                        command.ExecuteNonQuery();
                    }
                });
            }
        }
    }
}
