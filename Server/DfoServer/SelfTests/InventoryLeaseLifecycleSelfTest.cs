using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class InventoryLeaseLifecycleSelfTest
    {
        private const int CharacterA = 982001;
        private const int CharacterB = 982002;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "inventory-lease-lifecycle-" +
                Guid.NewGuid().ToString("N") + ".db");
            var previousDatabasePath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");
            InventoryLease lease = null;
            InventoryLease replacement = null;

            try
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    databasePath);
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var accounts = new SqliteAccountRepository(database);
                var characters = new SqliteCharacterRepository(database);
                var accountId = accounts.Create(
                    "inventory-lease-lifecycle-" + Guid.NewGuid().ToString("N"),
                    string.Empty);
                characters.Create(CreateCharacter(CharacterA, accountId, "LeaseA"));
                characters.Create(CreateCharacter(CharacterB, accountId, "LeaseB"));

                var failingDatabase = new FailingOpenGameDatabase(database);
                var failingInventoryA = CreateFailingInventory(
                    failingDatabase,
                    CharacterA,
                    accountId);
                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterA,
                    failingInventoryA);
                failingInventoryA.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    77);

                var failedUnregister = !InventoryContext.Unregister(
                    lease.SessionId,
                    CharacterA);
                Check(
                    "disconnect save failure retains the owned lease",
                    failedUnregister
                    && InventoryContext.TryGetLease(
                        CharacterA,
                        out var retainedAfterDisconnect)
                    && ReferenceEquals(retainedAfterDisconnect, lease)
                    && InventoryContext.TryGetOwnedLease(
                        lease.SessionId,
                        CharacterA,
                        out _),
                    ref failures);

                var normalInventoryA = LoadInventory(
                    database,
                    CharacterA,
                    accountId);
                Check(
                    "retained lease can be replaced after persistence recovery",
                    InventoryContext.TryReplaceCurrentLease(
                        lease,
                        normalInventoryA,
                        out replacement)
                    && ReferenceEquals(
                        replacement.Inventory,
                        normalInventoryA),
                    ref failures);
                Check(
                    "recovered lease unregisters cleanly",
                    replacement != null
                    && InventoryContext.Unregister(
                        replacement.SessionId,
                        CharacterA)
                    && !InventoryContext.TryGetLease(
                        CharacterA,
                        out _),
                    ref failures);
                lease = null;
                replacement = null;

                var failingInventoryForReplacement = CreateFailingInventory(
                    failingDatabase,
                    CharacterA,
                    accountId);
                var oldSession = Guid.NewGuid();
                lease = InventoryContext.Register(
                    oldSession,
                    CharacterA,
                    failingInventoryForReplacement);
                failingInventoryForReplacement.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    88);
                var replacementInventoryA = LoadInventory(
                    database,
                    CharacterA,
                    accountId);
                var sameCharacterRejected = false;
                try
                {
                    InventoryContext.Register(
                        Guid.NewGuid(),
                        CharacterA,
                        replacementInventoryA);
                }
                catch (InvalidOperationException)
                {
                    sameCharacterRejected = true;
                }

                Check(
                    "same-character takeover is rejected when old lease save fails",
                    sameCharacterRejected
                    && InventoryContext.TryGetLease(
                        CharacterA,
                        out var retainedAfterTakeover)
                    && ReferenceEquals(retainedAfterTakeover, lease)
                    && retainedAfterTakeover.IsOwnedBy(oldSession),
                    ref failures);

                replacement = null;
                Check(
                    "same-character retained lease remains recoverable",
                    InventoryContext.TryReplaceCurrentLease(
                        lease,
                        replacementInventoryA,
                        out replacement)
                    && replacement != null
                    && InventoryContext.Unregister(
                        replacement.SessionId,
                        CharacterA),
                    ref failures);
                lease = null;
                replacement = null;

                var failingInventoryForSwitch = CreateFailingInventory(
                    failingDatabase,
                    CharacterA,
                    accountId);
                var switchSession = Guid.NewGuid();
                lease = InventoryContext.Register(
                    switchSession,
                    CharacterA,
                    failingInventoryForSwitch);
                failingInventoryForSwitch.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    99);
                var inventoryB = LoadInventory(database, CharacterB, accountId);
                var crossCharacterRejected = false;
                try
                {
                    InventoryContext.Register(
                        switchSession,
                        CharacterB,
                        inventoryB);
                }
                catch (InvalidOperationException)
                {
                    crossCharacterRejected = true;
                }

                Check(
                    "cross-character switch is rejected when old lease save fails",
                    crossCharacterRejected
                    && InventoryContext.TryGetOwnedLease(
                        switchSession,
                        CharacterA,
                        out var retainedAfterSwitch)
                    && ReferenceEquals(retainedAfterSwitch, lease)
                    && !InventoryContext.TryGetLease(
                        CharacterB,
                        out _),
                    ref failures);

                replacement = null;
                var recoveredA = LoadInventory(database, CharacterA, accountId);
                Check(
                    "cross-character retained lease can be recovered without partial takeover",
                    InventoryContext.TryReplaceCurrentLease(
                        lease,
                        recoveredA,
                        out replacement)
                    && replacement != null
                    && InventoryContext.Unregister(
                        replacement.SessionId,
                        CharacterA),
                    ref failures);
                lease = null;
                replacement = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] inventory lease lifecycle selftest threw: " + ex);
                failures++;
            }
            finally
            {
                if (replacement != null)
                {
                    InventoryContext.Unregister(
                        replacement.SessionId,
                        replacement.CharacterId);
                }
                if (lease != null)
                {
                    InventoryContext.Unregister(
                        lease.SessionId,
                        lease.CharacterId);
                }

                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);
                DeleteDatabaseFiles(databasePath);
            }

            Console.WriteLine(
                failures == 0
                    ? "InventoryLeaseLifecycleSelfTest OK"
                    : "InventoryLeaseLifecycleSelfTest FAIL (" +
                    failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static InventoryService LoadInventory(
            IGameDatabase database,
            int characterId,
            int accountId)
        {
            using (var connection = database.OpenConnection())
            {
                return InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
            }
        }

        private static InventoryService CreateFailingInventory(
            IGameDatabase database,
            int characterId,
            int accountId)
        {
            return new InventoryService(characterId, accountId, database);
        }

        private static CharacterRecord CreateCharacter(
            int characterId,
            int accountId,
            string name)
        {
            return new CharacterRecord
            {
                CharacterId = characterId,
                AccountId = accountId,
                Name = Encoding.UTF8.GetBytes(name),
                Job = 0,
                GrowType = 0,
                Level = 1,
                TownId = 1,
                AreaId = 0,
                Direction = 5,
                AreaState = 3,
                Appearance = Array.Empty<CharacterAppearanceEntry>(),
            };
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var path = databasePath + suffix;
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
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
                    "injected inventory lease persistence failure");
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
