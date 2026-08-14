using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class InventorySortLockTransactionSelfTest
    {
        private const int AccountId = 984800;
        private const int CharacterId = 984801;
        private const short FirstSlot = 65;
        private const short SecondSlot = 66;
        private const int LowerItemId = 1004;
        private const int HigherItemId = 2000;

        public static int Run()
        {
            var failures = 0;
            try
            {
                using (var fixture = new Fixture())
                {
                    fixture.CreateItemUpdateFailureTrigger();
                    var sortFailed = InventorySortCommitService.TryCommit(
                        fixture.Lease,
                        InventoryListType.Main,
                        ItemCore.KindConsumable,
                        out var failedSort,
                        out var sortPersistenceFailed);
                    Check(
                        "inventory sort UPDATE failure rejects commit",
                        !sortFailed
                        && failedSort?.Success == true
                        && sortPersistenceFailed,
                        ref failures);
                    Check(
                        "inventory sort UPDATE failure restores all slots",
                        fixture.HasOnlineAndPersistedState(
                            HigherItemId,
                            LowerItemId,
                            firstLocked: false)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    var sorted = InventorySortCommitService.TryCommit(
                        fixture.Lease,
                        InventoryListType.Main,
                        ItemCore.KindConsumable,
                        out var sortedResult,
                        out var sortRecoveryPersistenceFailed);
                    Check(
                        "inventory sort retries after persistence recovery",
                        sorted
                        && sortedResult?.Mutated == true
                        && sortedResult.AffectedSlotCount == 2
                        && !sortRecoveryPersistenceFailed
                        && fixture.HasOnlineAndPersistedState(
                            LowerItemId,
                            HigherItemId,
                            firstLocked: false)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.CreateItemUpdateFailureTrigger();
                    var toggleFailed = SortItemLockCommitService.TryCommitToggle(
                        fixture.Lease,
                        InventoryListType.Main,
                        FirstSlot,
                        out var failedEntry,
                        out var togglePersistenceFailed);
                    Check(
                        "sort lock UPDATE failure restores unlocked state",
                        !toggleFailed
                        && failedEntry?.State == 1
                        && togglePersistenceFailed
                        && fixture.HasOnlineAndPersistedState(
                            LowerItemId,
                            HigherItemId,
                            firstLocked: false)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    var locked = SortItemLockCommitService.TryCommitToggle(
                        fixture.Lease,
                        InventoryListType.Main,
                        FirstSlot,
                        out var lockEntry,
                        out var lockPersistenceFailed);
                    Check(
                        "sort lock retries and commits after recovery",
                        locked
                        && lockEntry?.State == 1
                        && !lockPersistenceFailed
                        && fixture.HasOnlineAndPersistedState(
                            LowerItemId,
                            HigherItemId,
                            firstLocked: true)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.CreateItemUpdateFailureTrigger();
                    var unlockFailed = SortItemLockCommitService.TryCommitUnlock(
                        fixture.Lease,
                        InventoryListType.Main,
                        FirstSlot,
                        out _);
                    Check(
                        "sort unlock UPDATE failure preserves locked state",
                        !unlockFailed
                        && fixture.HasOnlineAndPersistedState(
                            LowerItemId,
                            HigherItemId,
                            firstLocked: true)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    var unlocked = SortItemLockCommitService.TryCommitUnlock(
                        fixture.Lease,
                        InventoryListType.Main,
                        FirstSlot,
                        out _);
                    Check(
                        "sort unlock retries and commits after recovery",
                        unlocked
                        && fixture.HasOnlineAndPersistedState(
                            LowerItemId,
                            HigherItemId,
                            firstLocked: false)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    var missingUnlock = SortItemLockCommitService.TryCommitUnlock(
                        fixture.Lease,
                        InventoryListType.Main,
                        120,
                        out var missingChanged);
                    Check(
                        "sort unlock keeps missing-slot protocol idempotence",
                        missingUnlock
                        && !missingChanged
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] inventory sort lock transaction selftest threw: "
                    + ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "InventorySortLockTransactionSelfTest OK"
                    : "InventorySortLockTransactionSelfTest FAIL ("
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
                    "inventory-sort-lock-transaction-"
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
                SetItem(inventory, FirstSlot, HigherItemId);
                SetItem(inventory, SecondSlot, LowerItemId);

                var seedLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                {
                    throw new InvalidOperationException(
                        "unable to persist inventory sort fixture");
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
                int firstItemId,
                int secondItemId,
                bool firstLocked)
            {
                return HasState(
                        Lease.Inventory,
                        firstItemId,
                        secondItemId,
                        firstLocked)
                    && HasPersistedState(
                        firstItemId,
                        secondItemId,
                        firstLocked);
            }

            internal bool HasNoDirtyState()
            {
                return Lease.Inventory.DirtyListTypes.Count == 0;
            }

            internal void CreateItemUpdateFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_inventory_sort_lock_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
BEGIN
    SELECT RAISE(ABORT, 'injected inventory sort lock update failure');
END;");
            }

            internal void DropFailureTriggers()
            {
                ExecuteNonQuery(@"
DROP TRIGGER IF EXISTS fail_inventory_sort_lock_update;");
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
                int firstItemId,
                int secondItemId,
                bool firstLocked)
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
                        firstItemId,
                        secondItemId,
                        firstLocked);
                }
            }

            private static bool HasState(
                InventoryService inventory,
                int firstItemId,
                int secondItemId,
                bool firstLocked)
            {
                var first = inventory.GetItem(
                    InventoryListType.Main,
                    FirstSlot);
                var second = inventory.GetItem(
                    InventoryListType.Main,
                    SecondSlot);
                return first?.ItemId == firstItemId
                    && second?.ItemId == secondItemId
                    && (first.SortLockFlag == 1) == firstLocked;
            }

            private static void SetItem(
                InventoryService inventory,
                short slotIndex,
                int itemId)
            {
                var item = ItemCore.Create(
                    ItemCore.KindConsumable,
                    itemId);
                item.Count = 1;
                if (!inventory.SetItem(
                        InventoryListType.Main,
                        slotIndex,
                        item))
                {
                    throw new InvalidOperationException(
                        "unable to seed inventory sort item");
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
VALUES(@aid, 'inventory-sort-lock-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                        command.Parameters.AddWithValue("@aid", AccountId);
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.Parameters.AddWithValue(
                            "@name",
                            Encoding.UTF8.GetBytes(
                                "InventorySortLockTransaction"));
                        command.ExecuteNonQuery();
                    }
                });
            }
        }
    }
}
