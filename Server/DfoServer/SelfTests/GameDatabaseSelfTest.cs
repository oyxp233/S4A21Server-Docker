using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.TitleBook;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.SelfTests
{
    internal static class GameDatabaseSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo-game-database-" + Guid.NewGuid().ToString("N") + ".db");
            var defaultDatabasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo-game-database-default-" + Guid.NewGuid().ToString("N") + ".db");
            var previousDefaultDatabasePath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");

            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);

                Check("game database creates the baseline once",
                    File.Exists(databasePath)
                    && database.Read(connection =>
                        ReadInt64(connection, "PRAGMA user_version;") == 1),
                    ref failures);
                Check("opened connections use foreign keys and busy timeout",
                    database.Read(connection =>
                        ReadInt64(connection, "PRAGMA foreign_keys;") == 1
                        && ReadInt64(connection, "PRAGMA busy_timeout;") == 5000),
                    ref failures);
                Check("opened connections reuse WAL mode",
                    database.Read(connection => string.Equals(
                        ReadString(connection, "PRAGMA journal_mode;"),
                        "wal",
                        StringComparison.OrdinalIgnoreCase)),
                    ref failures);

                database.Write((connection, transaction) =>
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
INSERT INTO accounts(m_id, password_hash)
VALUES('game-database-commit', '');";
                        command.ExecuteNonQuery();
                    }
                });
                Check("write transaction commits",
                    database.Read(connection =>
                        ReadInt64(connection, @"
SELECT COUNT(*) FROM accounts
WHERE m_id='game-database-commit';") == 1),
                    ref failures);

                var rolledBack = false;
                try
                {
                    database.Write((connection, transaction) =>
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
INSERT INTO accounts(m_id, password_hash)
VALUES('game-database-rollback', '');";
                            command.ExecuteNonQuery();
                        }

                        throw new InvalidOperationException("injected rollback");
                    });
                }
                catch (InvalidOperationException ex)
                {
                    rolledBack = ex.Message == "injected rollback";
                }
                Check("write transaction rolls back on exception",
                    rolledBack
                    && database.Read(connection =>
                        ReadInt64(connection, @"
SELECT COUNT(*) FROM accounts
WHERE m_id='game-database-rollback';") == 0),
                    ref failures);

                var countingDatabase = new CountingGameDatabase(database);
                var accountRepository = new SqliteAccountRepository(countingDatabase);
                var characterRepository = new SqliteCharacterRepository(countingDatabase);
                Check("repository construction performs no database IO",
                    countingDatabase.OpenCount == 0,
                    ref failures);

                var accountId = accountRepository.Create(
                    "game-database-repository",
                    string.Empty);
                characterRepository.Create(new CharacterRecord
                {
                    CharacterId = 991001,
                    AccountId = accountId,
                    Name = Encoding.UTF8.GetBytes("GameDatabaseCharacter"),
                    Level = 1,
                    Direction = 5,
                    AreaState = 3,
                    Appearance = Array.Empty<CharacterAppearanceEntry>(),
                });
                var account = accountRepository.GetById(accountId);
                var character = characterRepository.GetById(991001);
                Check("repositories share the injected database",
                    countingDatabase.OpenCount == 4
                    && account != null
                    && character != null,
                    ref failures);
                CheckOnlineInventoryMutationCommit(
                    database,
                    accountId,
                    ref failures);

                var beforeDataSourceConstruction = countingDatabase.OpenCount;
                var sharedDataSource = new SqliteSelectCharacterDataSource(
                    countingDatabase,
                    characterRepository);
                Check("select-character dependency graph performs no construction IO",
                    countingDatabase.OpenCount == beforeDataSourceConstruction,
                    ref failures);
                Check("game command registry rejects duplicate command ids",
                    RejectsDuplicateGameCommand(),
                    ref failures);

                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    defaultDatabasePath);
                using var runtimeBuilder = new ServerRuntimeBuilder(countingDatabase);
                var coreDependencies = runtimeBuilder
                    .GetOrCreateGameProtocolCoreDependencies();
                var reusedCoreDependencies = runtimeBuilder
                    .GetOrCreateGameProtocolCoreDependencies();
                Check("runtime builder reuses one core dependency graph",
                    ReferenceEquals(coreDependencies, reusedCoreDependencies)
                    && ReferenceEquals(
                        coreDependencies.Database,
                        countingDatabase),
                    ref failures);
                var inventoryDependencies = runtimeBuilder
                    .GetOrCreateGameProtocolInventoryDependencies(
                        coreDependencies);
                var reusedInventoryDependencies = runtimeBuilder
                    .GetOrCreateGameProtocolInventoryDependencies();
                Check("runtime builder reuses one inventory dependency graph",
                    ReferenceEquals(
                        inventoryDependencies,
                        reusedInventoryDependencies)
                    && ReferenceEquals(
                        inventoryDependencies.MailboxService,
                        reusedInventoryDependencies.MailboxService)
                    && ReferenceEquals(
                        inventoryDependencies.OverflowRewardSink,
                        reusedInventoryDependencies.OverflowRewardSink),
                    ref failures);
                var sessions = new SessionDirectory();
                var worldDependencies = runtimeBuilder
                    .GetOrCreateGameProtocolWorldDependencies(
                        sessions,
                        coreDependencies);
                var reusedWorldDependencies = runtimeBuilder
                    .GetOrCreateGameProtocolWorldDependencies(sessions);
                Check("runtime builder reuses one session-bound world dependency graph",
                    ReferenceEquals(worldDependencies, reusedWorldDependencies)
                    && ReferenceEquals(worldDependencies.Sessions, sessions),
                    ref failures);
                var rejectsAnotherDirectory = false;
                try
                {
                    runtimeBuilder.GetOrCreateGameProtocolWorldDependencies(
                        new SessionDirectory());
                }
                catch (InvalidOperationException)
                {
                    rejectsAnotherDirectory = true;
                }
                Check("runtime builder rejects a second session directory",
                    rejectsAnotherDirectory,
                    ref failures);
                var characterInventoryHandlers = runtimeBuilder
                    .GetOrCreateGameProtocolCharacterInventoryHandlers(
                        coreDependencies,
                        inventoryDependencies,
                        worldDependencies);
                var reusedCharacterInventoryHandlers = runtimeBuilder
                    .GetOrCreateGameProtocolCharacterInventoryHandlers();
                Check("runtime builder reuses one character-inventory handler module",
                    ReferenceEquals(
                        characterInventoryHandlers,
                        reusedCharacterInventoryHandlers),
                    ref failures);
                Check("formal inventory retains the injected database",
                    HasInjectedInventoryDatabase(
                        characterInventoryHandlers.CharacterSelect,
                        countingDatabase,
                        accountId),
                    ref failures);
                var expertJobHandlers = runtimeBuilder
                    .GetOrCreateGameProtocolExpertJobHandlers(
                        coreDependencies,
                        inventoryDependencies,
                        worldDependencies);
                var reusedExpertJobHandlers = runtimeBuilder
                    .GetOrCreateGameProtocolExpertJobHandlers();
                Check("runtime builder reuses one expert-job handler module",
                    ReferenceEquals(expertJobHandlers, reusedExpertJobHandlers),
                    ref failures);
                var townDungeonHandlers = runtimeBuilder
                    .GetOrCreateGameProtocolTownDungeonHandlers(
                        coreDependencies,
                        inventoryDependencies,
                        worldDependencies);
                var reusedTownDungeonHandlers = runtimeBuilder
                    .GetOrCreateGameProtocolTownDungeonHandlers();
                Check("runtime builder reuses one town-dungeon handler module",
                    ReferenceEquals(
                        townDungeonHandlers,
                        reusedTownDungeonHandlers),
                    ref failures);
                var socialHandlers = runtimeBuilder
                    .GetOrCreateGameProtocolSocialHandlers(
                        coreDependencies,
                        worldDependencies,
                        townDungeonHandlers);
                var reusedSocialHandlers = runtimeBuilder
                    .GetOrCreateGameProtocolSocialHandlers();
                Check("runtime builder reuses one social-PvP handler module",
                    ReferenceEquals(socialHandlers, reusedSocialHandlers),
                    ref failures);
                var featureHandlers = runtimeBuilder
                    .GetOrCreateGameProtocolFeatureHandlers(
                        coreDependencies,
                        inventoryDependencies,
                        worldDependencies,
                        townDungeonHandlers);
                var reusedFeatureHandlers = runtimeBuilder
                    .GetOrCreateGameProtocolFeatureHandlers();
                Check("runtime builder reuses one feature handler module",
                    ReferenceEquals(featureHandlers, reusedFeatureHandlers),
                    ref failures);
                var characterSessionLifecycle = runtimeBuilder
                    .GetOrCreateCharacterSessionLifecycleCoordinator(
                        coreDependencies,
                        inventoryDependencies,
                        worldDependencies,
                        characterInventoryHandlers,
                        expertJobHandlers,
                        townDungeonHandlers,
                        socialHandlers,
                        featureHandlers);
                var reusedCharacterSessionLifecycle = runtimeBuilder
                    .GetOrCreateCharacterSessionLifecycleCoordinator();
                Check("runtime builder reuses one character session lifecycle",
                    ReferenceEquals(
                        characterSessionLifecycle,
                        reusedCharacterSessionLifecycle),
                    ref failures);
                using (var protocol = runtimeBuilder.BuildGameProtocolHandler(sessions))
                {
                    var commandRegistry =
                        GetField<GameCommandRegistry>(protocol, "_cmdDispatch");
                    Check("runtime builder retains the injected database",
                        ReferenceEquals(runtimeBuilder.Database, countingDatabase),
                        ref failures);
                    Check("formal protocol composition reuses the injected database",
                        !File.Exists(defaultDatabasePath),
                        ref failures);
                    Check("formal protocol command registry is populated",
                        commandRegistry?.Count > 100,
                        ref failures);
                    Check("formal protocol registers the complete unique PVP command surface",
                        HasCompletePvpCommandSurface(commandRegistry),
                        ref failures);
                    Check("formal protocol registers the complete unique Dungeon command surface",
                        HasCompleteDungeonCommandSurface(commandRegistry),
                        ref failures);
                    Check("formal mailbox overflow composition reuses the injected database",
                        IsFormalMailboxOverflowComposition(
                            protocol,
                            characterInventoryHandlers,
                            inventoryDependencies)
                        && !File.Exists(defaultDatabasePath),
                        ref failures);
                    Check("formal protocol reuses the feature handler module",
                        IsFormalFeatureHandlerComposition(
                            protocol,
                            featureHandlers)
                        && !File.Exists(defaultDatabasePath),
                        ref failures);
                    Check("formal protocol reuses the character session lifecycle",
                        ReferenceEquals(
                            GetField<object>(
                                protocol,
                                "_characterSessionLifecycle"),
                            characterSessionLifecycle)
                        && !File.Exists(defaultDatabasePath),
                        ref failures);
                    Check("formal quest session composition reuses the injected database",
                        IsFormalQuestSessionComposition(
                            protocol,
                            coreDependencies,
                            countingDatabase)
                        && !File.Exists(defaultDatabasePath),
                        ref failures);
                    var rejectsSecondProtocol = false;
                    try
                    {
                        runtimeBuilder.BuildGameProtocolHandler(sessions);
                    }
                    catch (InvalidOperationException)
                    {
                        rejectsSecondProtocol = true;
                    }
                    Check("runtime builder rejects a second protocol runtime",
                        rejectsSecondProtocol,
                        ref failures);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] game database selftest threw: " + ex);
                failures++;
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDefaultDatabasePath);
                DeleteIfExists(databasePath);
                DeleteIfExists(databasePath + "-wal");
                DeleteIfExists(databasePath + "-shm");
                DeleteIfExists(defaultDatabasePath);
                DeleteIfExists(defaultDatabasePath + "-wal");
                DeleteIfExists(defaultDatabasePath + "-shm");
            }

            Console.WriteLine(
                failures == 0
                    ? "[PASS] game database self-test"
                    : $"[FAIL] game database self-test failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static long ReadInt64(SqliteConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static string ReadString(SqliteConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToString(command.ExecuteScalar());
            }
        }

        private static bool IsFormalMailboxOverflowComposition(
            GameProtocolHandler protocol,
            GameProtocolCharacterInventoryHandlers characterInventoryHandlers,
            GameProtocolInventoryDependencies inventoryDependencies)
        {
            var expected = inventoryDependencies?.OverflowRewardSink;
            return expected != null
                && ReferenceEquals(
                    GetField<object>(
                        characterInventoryHandlers.Inventory,
                        "_overflowRewardSink"),
                    expected)
                && ReferenceEquals(
                    GetField<object>(
                        GetField<object>(protocol, "_lotteryItemHandler"),
                        "_overflowRewardSink"),
                    expected)
                && ReferenceEquals(
                    GetField<object>(
                        GetField<object>(protocol, "_craneMiniGameHandler"),
                        "_overflowRewardSink"),
                    expected)
                && ReferenceEquals(
                    GetField<object>(
                        GetField<object>(protocol, "_ceraShopHandler"),
                        "_overflowRewardSink"),
                    expected)
                && ReferenceEquals(
                    GetField<object>(
                        GetField<object>(protocol, "_mailboxHandler"),
                        "_mailboxService"),
                    inventoryDependencies.MailboxService);
        }

        private static void CheckOnlineInventoryMutationCommit(
            IGameDatabase database,
            int accountId,
            ref int failures)
        {
            const int characterId = 991001;
            const int achievementId = 991101;
            const int committedTitleId = 991201;
            const int uncommittedTitleId = 991202;
            InventoryLease lease = null;

            try
            {
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                }

                lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                inventory.TitleBook.SetItem(
                    0,
                    0,
                    CreateSelfTestTitle(committedTitleId));
                var achievement = inventory.Achievements.GetOrCreateEntry(
                    achievementId,
                    3);
                achievement.P1 = 2;
                inventory.Achievements.MarkDirty(achievementId);

                var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "game-database-selftest-commit");
                var persisted = database.Read(connection =>
                {
                    var title = CharacterTitleBookRepository.LoadModel(
                        connection,
                        characterId)
                        .GetItem(0, 0);
                    return title?.ItemId == committedTitleId
                        && ReadAchievementP1(
                            connection,
                            characterId,
                            achievementId) == 2;
                });
                Check(
                    "online title-book and achievement mutations commit atomically",
                    committed
                    && persisted
                    && !inventory.TitleBook.HasDirtySlots
                    && inventory.Achievements.DirtyQuestIds.Count == 0,
                    ref failures);

                InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
                lease = null;

                var failingDatabase = new FailingOpenGameDatabase(database);
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        failingDatabase);
                }

                lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                inventory.TitleBook.SetItem(
                    0,
                    0,
                    CreateSelfTestTitle(uncommittedTitleId));
                achievement = inventory.Achievements.GetOrCreateEntry(
                    achievementId,
                    3);
                achievement.P1 = 0;
                inventory.Achievements.MarkDirty(achievementId);

                var rejected = !OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "game-database-selftest-rollback");
                var reloaded = lease.Inventory;
                var reloadedAchievement = FindAchievement(
                    reloaded,
                    achievementId);
                Check(
                    "failed online mutation commit reloads the persisted inventory",
                    rejected
                    && reloaded.TitleBook.GetItem(0, 0)?.ItemId
                        == committedTitleId
                    && reloadedAchievement?.P1 == 2
                    && !reloaded.TitleBook.HasDirtySlots
                    && reloaded.Achievements.DirtyQuestIds.Count == 0,
                    ref failures);
            }
            finally
            {
                if (lease != null)
                {
                    InventoryContext.Unregister(
                        lease.SessionId,
                        lease.CharacterId);
                }
            }
        }

        private static ItemCore CreateSelfTestTitle(int itemId)
        {
            return new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = itemId,
                Durability = 1,
            };
        }

        private static AchievementCompleteEntrySnapshot FindAchievement(
            InventoryService inventory,
            int achievementId)
        {
            var entries = inventory?.Achievements.BuildSnapshot().Entries;
            if (entries == null)
                return null;

            foreach (var entry in entries)
            {
                if (entry.AchievementId == achievementId)
                    return entry;
            }

            return null;
        }

        private static int ReadAchievementP1(
            SqliteConnection connection,
            int characterId,
            int achievementId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT p1
FROM character_achievements
WHERE character_id=@cid AND achievement_id=@achievementId;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue(
                    "@achievementId",
                    achievementId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? -1
                    : Convert.ToInt32(value);
            }
        }

        private static bool RejectsDuplicateGameCommand()
        {
            var registry = new GameCommandRegistry();
            registry.RegisterGroup(
                "selftest-first",
                group => group[0x7FFF] = NoopCommand);
            try
            {
                registry.RegisterGroup(
                    "selftest-second",
                    group => group[0x7FFF] = NoopCommand);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("0x7FFF", StringComparison.Ordinal)
                    && ex.Message.Contains(
                        "selftest-first",
                        StringComparison.Ordinal)
                    && ex.Message.Contains(
                        "selftest-second",
                        StringComparison.Ordinal);
            }
        }

        private static Task NoopCommand(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => Task.CompletedTask;

        private static bool HasCompletePvpCommandSurface(
            GameCommandRegistry registry)
        {
            if (registry == null)
                return false;

            var commands = new[]
            {
                PvpRoomHandler.MakeRoomCommandType,
                PvpRoomHandler.EnterRoomCommandType,
                PvpRoomHandler.SetSeatStateCommandType,
                PvpRoomHandler.SetReadyStateCommandType,
                PvpRoomHandler.SetTeamModeCommandType,
                PvpRoomHandler.DiePvpCharacterCommandType,
                PvpRoomHandler.PvpTimeOutCommandType,
                PvpRoomHandler.EndPvpResultCommandType,
                PvpRoomHandler.PvpRankResponseCommandType,
                PvpRoomHandler.CompleteLoadPvpCommandType,
                PvpRoomHandler.ConnectP2pPvpCommandType,
                PvpRoomHandler.PvpRequestFightCommandType
            };
            var uniqueCommands = new HashSet<ushort>();
            foreach (var command in commands)
            {
                if (!uniqueCommands.Add(command)
                    || !registry.TryGetValue(command, out var handler)
                    || handler == null)
                {
                    return false;
                }
            }

            return uniqueCommands.Count == commands.Length;
        }

        private static bool HasCompleteDungeonCommandSurface(
            GameCommandRegistry registry)
        {
            if (registry == null)
                return false;

            var commands = new ushort[]
            {
                0x000F,
                0x0010,
                0x0027,
                0x0028,
                0x0029,
                0x002B,
                0x002D,
                0x002E,
                0x002F,
                0x0045,
                0x0047,
                0x0048,
                0x0075,
                0x007B,
                0x008F,
                0x00BF,
                0x0128,
                0x0129,
                0x013C,
                0x01E4,
                0x0211,
                0x0253,
                0x026B,
                0x026D,
                0x0270,
                (ushort)CmdPacketType.TOURNAMENT_REWARD_SELECT_STATE,
                (ushort)CmdPacketType.TOURNAMENT_REWARD_SELECT,
                (ushort)CmdPacketType.BLOOD_ROUND_UI_PREPARE_FINISH_,
                (ushort)CmdPacketType.DIE_BLOOD_MONSTER,
                (ushort)CmdPacketType.SELECT_ULTIMATE_DIFFICULTY,
                0x0312,
                0x03B6,
                0x03AB,
                0x009F,
                0x02D7,
                0x02D8
            };
            var uniqueCommands = new HashSet<ushort>();
            foreach (var command in commands)
            {
                if (!uniqueCommands.Add(command)
                    || !registry.TryGetValue(command, out var handler)
                    || handler == null)
                {
                    return false;
                }
            }

            return uniqueCommands.Count == commands.Length;
        }

        private static bool HasInjectedInventoryDatabase(
            object characterSelectHandler,
            IGameDatabase database,
            int accountId)
        {
            var method = characterSelectHandler?.GetType().GetMethod(
                "TryLoadInventoryForLease",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
            var inventory = method?.Invoke(
                characterSelectHandler,
                new object[] { 991001, accountId }) as InventoryService;
            return inventory != null
                && ReferenceEquals(inventory.Database, database);
        }

        private static bool IsFormalQuestSessionComposition(
            GameProtocolHandler protocol,
            GameProtocolCoreDependencies coreDependencies,
            IGameDatabase database)
        {
            var lifecycle = GetField<object>(
                protocol,
                "_characterSessionLifecycle");
            return ReferenceEquals(
                    GetField<object>(lifecycle, "_database"),
                    database)
                && ReferenceEquals(
                    GetField<object>(lifecycle, "_characterRepository"),
                    coreDependencies.CharacterRepository)
                && ReferenceEquals(
                    GetField<object>(lifecycle, "_selectCharacterDataSource"),
                    coreDependencies.SelectCharacterDataSource);
        }

        private static bool IsFormalFeatureHandlerComposition(
            GameProtocolHandler protocol,
            GameProtocolFeatureHandlers featureHandlers)
        {
            return ReferenceEquals(
                    GetField<object>(protocol, "_lotteryItemHandler"),
                    featureHandlers?.LotteryItem)
                && ReferenceEquals(
                    GetField<object>(protocol, "_petCreatureHandler"),
                    featureHandlers?.PetCreature)
                && ReferenceEquals(
                    GetField<object>(protocol, "_mailboxHandler"),
                    featureHandlers?.Mailbox)
                && ReferenceEquals(
                    GetField<object>(protocol, "_mercenaryHandler"),
                    featureHandlers?.Mercenary)
                && ReferenceEquals(
                    GetField<object>(protocol, "_growthCapsuleHandler"),
                    featureHandlers?.GrowthCapsule)
                && ReferenceEquals(
                    GetField<object>(protocol, "_craneMiniGameHandler"),
                    featureHandlers?.CraneMiniGame);
        }

        private static T GetField<T>(object owner, string fieldName)
            where T : class
        {
            if (owner == null || string.IsNullOrWhiteSpace(fieldName))
                return null;

            var field = owner.GetType().GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
            return field?.GetValue(owner) as T;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine(condition ? "[PASS] " + name : "[FAIL] " + name);
            if (!condition)
                failures++;
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private sealed class CountingGameDatabase : IGameDatabase
        {
            private readonly IGameDatabase _inner;

            internal CountingGameDatabase(IGameDatabase inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            internal int OpenCount { get; private set; }

            public string DatabasePath => _inner.DatabasePath;

            public string SchemaFilePath => _inner.SchemaFilePath;

            public string ConnectionString => _inner.ConnectionString;

            public SqliteConnection OpenConnection()
            {
                OpenCount++;
                return _inner.OpenConnection();
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
                    "injected online inventory commit failure");
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
