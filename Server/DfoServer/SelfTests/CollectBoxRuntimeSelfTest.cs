using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class CollectBoxRuntimeSelfTest
    {
        private const int AccountId = 948000;
        private const int CharacterId = 948001;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== COLLECT_BOX_RUNTIME selftest ===");

            if (!TryFindSample(out var boxIndex, out var slotIndex, out var itemId))
            {
                Check("collectbox sample item found", false);
                PrintSummary();
                return 1;
            }

            Check("collectbox sample item found", itemId > 0);

            var tempDb = Path.Combine(Path.GetTempPath(), "collectbox_runtime_selftest.db");
            DeleteTempDatabase(tempDb);

            var previousDatabasePath = Environment.GetEnvironmentVariable("INVENTORY_DATABASE_PATH");
            InventoryLease lease = null;
            InventoryLease loadedLease = null;
            try
            {
                Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", tempDb);
                SeedIdentity(tempDb);

                var inventory = new InventoryService(CharacterId, AccountId);
                var grantOk = InventoryRewardGrantService.TryCreateAndInsert(
                    inventory,
                    itemId,
                    ItemCreateReason.AdminGrant,
                    2,
                    out var grant);
                Check("grant sample item succeeds", grantOk && grant != null && grant.Success);
                Check("sample starts with two in main", inventory.CountMainItem(itemId) == 2);
                var sourceSlotIndex = grant != null ? grant.SlotIndex : InventoryService.MainSlotStart;

                lease = InventoryContext.Register(Guid.NewGuid(), inventory);

                Check("reject wrong source slot leaves inventory unchanged",
                    VerifyWrongSourceSlotReject(inventory, boxIndex, slotIndex, sourceSlotIndex, itemId));
                Check("put collectbox item succeeds",
                    CollectBoxRuntimeService.TryPutItem(inventory, boxIndex, sourceSlotIndex, itemId, out var put)
                    && put.Success
                    && put.InventoryItem != null
                    && put.InventoryItem.RemainingStackCount == 1);
                Check("put decrements main count", inventory.CountMainItem(itemId) == 1);
                Check("put marks collectbox slot", inventory.CollectBox.GetItemId(boxIndex, slotIndex) == itemId);
                Check("save dirty after put succeeds", InventoryPersistenceService.SaveDirty(lease));

                InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
                lease = null;

                var loaded = LoadInventory(tempDb);
                Check("loaded inventory keeps one source item", loaded.CountMainItem(itemId) == 1);
                Check("loaded collectbox keeps slot item", loaded.CollectBox.GetItemId(boxIndex, slotIndex) == itemId);

                loadedLease = InventoryContext.Register(Guid.NewGuid(), loaded);

                var failingDatabase = new FailingOpenGameDatabase(
                    new GameDatabase(tempDb, ServerPaths.SchemaFilePath));
                InventoryContext.Unregister(loadedLease.SessionId, loadedLease.CharacterId);
                loadedLease = null;

                InventoryService failingInventory;
                using (var connection = new SqliteConnection(failingDatabase.ConnectionString))
                {
                    connection.Open();
                    failingInventory = InventoryService.LoadFromDb(
                        connection,
                        CharacterId,
                        AccountId,
                        failingDatabase);
                }

                loadedLease = InventoryContext.Register(Guid.NewGuid(), failingInventory);
                Check("take mutation is created before injected commit failure",
                    CollectBoxRuntimeService.TryTakeItem(
                        failingInventory,
                        itemId,
                        out var failedTake)
                    && failedTake.Success
                    && failingInventory.CountMainItem(itemId) == 2
                    && failingInventory.CollectBox.GetItemId(boxIndex, slotIndex) == 0);
                Check("failed collectbox commit reloads persisted state",
                    !OnlineInventoryMutationCommitCoordinator.TryCommit(
                        loadedLease,
                        "collectbox-runtime-selftest-failure")
                    && loadedLease.Inventory.CountMainItem(itemId) == 1
                    && loadedLease.Inventory.CollectBox.GetItemId(boxIndex, slotIndex) == itemId
                    && !loadedLease.Inventory.TitleBook.HasDirtySlots
                    && loadedLease.Inventory.Achievements.DirtyQuestIds.Count == 0);

                InventoryContext.Unregister(loadedLease.SessionId, loadedLease.CharacterId);
                loadedLease = null;

                loaded = LoadInventory(tempDb);
                loadedLease = InventoryContext.Register(Guid.NewGuid(), loaded);
                Check("take collectbox item succeeds",
                    CollectBoxRuntimeService.TryTakeItem(loaded, itemId, out var take)
                    && take.Success
                    && take.InventoryItem != null
                    && take.InventoryItem.SlotIndex >= InventoryService.MainSlotStart);
                Check("take returns main count to two", loaded.CountMainItem(itemId) == 2);
                Check("take clears collectbox slot", loaded.CollectBox.GetItemId(boxIndex, slotIndex) == 0);
                Check("save dirty after take succeeds", InventoryPersistenceService.SaveDirty(loadedLease));

                var savedSlots = new CollectBoxProgressRepository(tempDb, ServerPaths.SchemaFilePath)
                    .LoadSlots(CharacterId, boxIndex);
                Check("collectbox row removed after take", savedSlots.Count == 0);
            }
            finally
            {
                if (loadedLease != null)
                    InventoryContext.Unregister(loadedLease.SessionId, loadedLease.CharacterId);
                if (lease != null)
                    InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
                Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", previousDatabasePath);
            }

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static bool VerifyWrongSourceSlotReject(
            InventoryService inventory,
            int boxIndex,
            int collectBoxSlotIndex,
            short sourceSlotIndex,
            int itemId)
        {
            for (var index = InventoryService.MainSlotStart; index <= InventoryService.MainSlotEnd; index++)
            {
                if (index == sourceSlotIndex || inventory.GetItem(InventoryListType.Main, (short)index) != null)
                    continue;

                var rejected = !CollectBoxRuntimeService.TryPutItem(
                    inventory,
                    boxIndex,
                    index,
                    itemId,
                    out _);
                return rejected
                    && inventory.CountMainItem(itemId) == 2
                    && inventory.CollectBox.GetItemId(boxIndex, collectBoxSlotIndex) == 0;
            }

            return true;
        }

        private static bool TryFindSample(out int boxIndex, out int slotIndex, out int itemId)
        {
            boxIndex = 0;
            slotIndex = 0;
            itemId = 0;

            foreach (var index in CollectBoxDataService.GetAllIndexes())
            {
                var entry = CollectBoxDataService.GetByIndex(index);
                if (entry == null)
                    continue;

                for (var slot = 0; slot < entry.Slots.Count; slot++)
                {
                    var candidateId = entry.Slots[slot].ItemId;
                    if (candidateId <= 0)
                        continue;

                    ItemMetadata metadata;
                    try
                    {
                        metadata = ItemMetadataResolver.Resolve(candidateId);
                    }
                    catch
                    {
                        continue;
                    }

                    if (metadata == null || !metadata.IsStackable)
                        continue;

                    boxIndex = index;
                    slotIndex = slot;
                    itemId = candidateId;
                    return true;
                }
            }

            return false;
        }

        private static InventoryService LoadInventory(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                return InventoryService.LoadFromDb(connection, CharacterId, AccountId);
            }
        }

        private static void SeedIdentity(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'collectbox-runtime-selftest', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'collectbox-runtime-selftest');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            try
            {
                if (File.Exists(databasePath))
                    File.Delete(databasePath);

                var wal = databasePath + "-wal";
                if (File.Exists(wal))
                    File.Delete(wal);

                var shm = databasePath + "-shm";
                if (File.Exists(shm))
                    File.Delete(shm);
            }
            catch
            {
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

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }

        private sealed class FailingOpenGameDatabase : IGameDatabase
        {
            private readonly IGameDatabase _inner;

            internal FailingOpenGameDatabase(IGameDatabase inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public string DatabasePath => _inner.DatabasePath;

            public string SchemaFilePath => _inner.SchemaFilePath;

            public string ConnectionString => _inner.ConnectionString;

            public SqliteConnection OpenConnection()
            {
                throw new InvalidOperationException(
                    "injected collectbox commit failure");
            }

            public T Read<T>(Func<SqliteConnection, T> action) =>
                _inner.Read(action);

            public T Write<T>(
                Func<SqliteConnection, SqliteTransaction, T> action,
                bool immediate = true) =>
                _inner.Write(action, immediate);

            public void Write(
                Action<SqliteConnection, SqliteTransaction> action,
                bool immediate = true) =>
                _inner.Write(action, immediate);
        }
    }
}
