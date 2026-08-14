using DfoServer.Game.Dungeon;
using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class SpecialDungeonPart3SelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== SPECIAL_DUNGEON_PART3 selftest ===");
            var failures = 0;

            TestProtocolBodies(ref failures);
            TestSpecialPassiveObjectItemDefinition(ref failures);
            TestPassiveObjectDropDefinitionAndPlanner(ref failures);
            TestPassiveObjectDropActionAndProbabilityBoundaries(ref failures);
            TestPassiveObjectDropRoomLedgerAndProjection(ref failures);
            TestAntonPermissionBatch(ref failures);
            TestAntonNormalPvfSequence(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void TestSpecialPassiveObjectItemDefinition(
            ref int failures)
        {
            var parser = typeof(DungeonFile).GetMethod(
                "ParseSpecialPassiveObjectItem",
                BindingFlags.NonPublic | BindingFlags.Static);
            var passed = parser != null;

            var dungeon = new DungeonFile();
            try
            {
                parser?.Invoke(
                    null,
                    new object[]
                    {
                        "0 -1 0 1 60 2 6515 10001 6516 0",
                        dungeon,
                    });
            }
            catch (TargetInvocationException ex)
            {
                Console.WriteLine(
                    $"[INFO] special passive object parser threw: " +
                    $"{ex.InnerException ?? ex}");
                passed = false;
            }

            Check(
                "special passive object item keeps group index, level and empty groups",
                passed
                && dungeon.SpecialPassiveObjectItemDefinitionPresent
                && !dungeon.SpecialPassiveObjectItemDefinitionMalformed
                && dungeon.SpecialPassiveObjectItemGroups.Count == 2
                && dungeon.SpecialPassiveObjectItemGroups[0].GroupIndex == 0
                && dungeon.SpecialPassiveObjectItemGroups[0].LevelOverride == -1
                && dungeon.SpecialPassiveObjectItemGroups[0].Items.Count == 0
                && dungeon.SpecialPassiveObjectItemGroups[1].GroupIndex == 1
                && dungeon.SpecialPassiveObjectItemGroups[1].LevelOverride == 60
                && dungeon.SpecialPassiveObjectItemGroups[1].Items.Count == 2
                && dungeon.SpecialPassiveObjectItemGroups[1].Items[0].ItemId == 6515
                && dungeon.SpecialPassiveObjectItemGroups[1].Items[0].Weight == 10001,
                ref failures);

            var empty = new DungeonFile();
            parser?.Invoke(null, new object[] { string.Empty, empty });
            Check(
                "empty special passive object item definition remains an explicit empty definition",
                empty.SpecialPassiveObjectItemDefinitionPresent
                && !empty.SpecialPassiveObjectItemDefinitionMalformed
                && empty.SpecialPassiveObjectItemGroups.Count == 0,
                ref failures);

            var malformedRows = new[]
            {
                "1 -1 0",
                "0 -2 0",
                "0 -1 -1",
                "0 -1 1 6515",
                "0 -1 0 2 -1 0",
                "0 -1 0 0 -1 0",
                "0 -1 0 trailing",
            };
            var malformedPassed = parser != null;
            for (var index = 0; index < malformedRows.Length; index++)
            {
                var malformed = new DungeonFile();
                try
                {
                    parser?.Invoke(
                        null,
                        new object[] { malformedRows[index], malformed });
                }
                catch
                {
                    malformedPassed = false;
                }
                malformedPassed &=
                    malformed.SpecialPassiveObjectItemDefinitionPresent
                    && malformed.SpecialPassiveObjectItemDefinitionMalformed
                    && malformed.SpecialPassiveObjectItemGroups.Count == 0;
            }
            Check(
                "malformed special passive object item rows disable the whole definition",
                malformedPassed,
                ref failures);

            var duplicate = new DungeonFile();
            parser?.Invoke(null, new object[] { "0 -1 0", duplicate });
            parser?.Invoke(null, new object[] { "1 -1 0", duplicate });
            Check(
                "duplicate special passive object item definitions fail closed",
                duplicate.SpecialPassiveObjectItemDefinitionMalformed
                && duplicate.SpecialPassiveObjectItemGroups.Count == 0,
                ref failures);
        }

        private static void TestPassiveObjectDropDefinitionAndPlanner(
            ref int failures)
        {
            var definition = PassiveObjectRandomDropDefinitionCatalog.Parse(
                BuildPassiveObjectDropEtc());
            Check(
                "object drop ETC parses as one typed immutable definition",
                definition.IsValid
                && definition.GetBaseRate(60, 0) == 10000
                && definition.GetBaseRate(60, 2) == 10000
                && definition.GetDifficultyRate(2, 0) == 1.0
                && definition.GetActorTypeRate(2, 0) == 1.0
                && definition.TryGetGradeRange(60, out var gradeRange)
                && gradeRange.Down == 1
                && gradeRange.Up == 1,
                ref failures);
            Check(
                "original PVF object drop ETC loads as a valid definition",
                PassiveObjectRandomDropDefinitionCatalog.Current.IsValid,
                ref failures);

            var malformedDefinition = PassiveObjectRandomDropDefinitionCatalog.Parse(
                BuildPassiveObjectDropEtc().Replace(
                    "[drop prob count]\r\n1",
                    "[drop prob count]\r\n2"));
            Check(
                "malformed object drop ETC disables the complete definition",
                !malformedDefinition.IsValid,
                ref failures);

            var groups = new[]
            {
                new SpecialPassiveObjectItemGroup(
                    0,
                    -1,
                    new[] { new SpecialPassiveObjectItem(6515, 10001) }),
                new SpecialPassiveObjectItemGroup(
                    1,
                    -1,
                    new[] { new SpecialPassiveObjectItem(9999, 10001) }),
            };
            var objects = new List<SpecialPassiveObjectInfo>
            {
                new SpecialPassiveObjectInfo
                {
                    ObjectCode = 9001,
                    Spawns = new List<SpecialPassiveObjectSpawnInfo>
                    {
                        new SpecialPassiveObjectSpawnInfo
                        {
                            Kind = "[monster]",
                            Code = 123,
                        },
                    },
                },
                new SpecialPassiveObjectInfo
                {
                    ObjectCode = 9002,
                    Spawns = new List<SpecialPassiveObjectSpawnInfo>
                    {
                        new SpecialPassiveObjectSpawnInfo
                        {
                            Kind = "[item]",
                            Code = 0,
                            Level = 2,
                            Param0 = 1,
                        },
                    },
                },
            };
            var equipmentPool = new Dictionary<long, List<(int Id, int Weight)>>
            {
                [(long)60 * 10] = new List<(int Id, int Weight)>
                {
                    (7777, 100),
                },
            };
            var planner = new PassiveObjectDropPlanningService(
                definition,
                equipmentPool,
                (level, lcg) => 77);
            var first = planner.Plan(
                groups,
                objects,
                dungeonBasisLevel: 60,
                difficulty: 0,
                new DnfLcg(123456));
            var replay = planner.Plan(
                groups,
                objects,
                dungeonBasisLevel: 60,
                difficulty: 0,
                new DnfLcg(123456));
            var expected =
                "1:Item:6515:1,1:Item:6515:1," +
                "1:Gold:0:77,1:Item:7777:1";
            Check(
                "room actor planner filters unreferenced groups and preserves parent actor index",
                FormatPassiveObjectIntents(first) == expected
                && first.SpecificDropCount == 2
                && first.RandomDropCount == 2
                && first.InvalidActionCount == 0
                && first.UnsupportedRandomCategoryCount == 0
                && !first.WasTruncated
                && first.Intents.All(intent => intent.ItemId != 9999),
                ref failures);
            Check(
                "room actor planner replays deterministically from the room seed",
                FormatPassiveObjectIntents(replay) == expected,
                ref failures);
        }

        private static void TestPassiveObjectDropActionAndProbabilityBoundaries(
            ref int failures)
        {
            var groups = new[]
            {
                new SpecialPassiveObjectItemGroup(
                    0,
                    -1,
                    new[] { new SpecialPassiveObjectItem(6515, 10001) }),
            };
            var randomOnlyObjects = new[]
            {
                new SpecialPassiveObjectInfo
                {
                    Spawns = new List<SpecialPassiveObjectSpawnInfo>
                    {
                        new SpecialPassiveObjectSpawnInfo
                        {
                            Kind = "[item]",
                            Code = 0,
                            Level = -1,
                            Param0 = 1,
                        },
                    },
                },
            };
            var specificOnlyObjects = new[]
            {
                new SpecialPassiveObjectInfo
                {
                    Spawns = new List<SpecialPassiveObjectSpawnInfo>
                    {
                        new SpecialPassiveObjectSpawnInfo
                        {
                            Kind = "[item]",
                            Code = 0,
                            Level = 1,
                            Param0 = -1,
                        },
                    },
                },
            };
            var equipmentPool = new Dictionary<long, List<(int Id, int Weight)>>
            {
                [(long)60 * 10] = new List<(int Id, int Weight)>
                {
                    (7777, 100),
                },
            };
            var planner = new PassiveObjectDropPlanningService(
                PassiveObjectRandomDropDefinitionCatalog.Parse(
                    BuildPassiveObjectDropEtc()),
                equipmentPool,
                (level, lcg) => 77);

            var randomOnly = planner.Plan(
                groups,
                randomOnlyObjects,
                dungeonBasisLevel: 60,
                difficulty: 0,
                new DnfLcg(51852));
            Check(
                "negative specific sentinel preserves the random object drop path",
                FormatPassiveObjectIntents(randomOnly) ==
                    "0:Gold:0:77,0:Item:7777:1"
                && randomOnly.SpecificDropCount == 0
                && randomOnly.RandomDropCount == 2
                && randomOnly.InvalidActionCount == 0,
                ref failures);

            var specificOnly = planner.Plan(
                groups,
                specificOnlyObjects,
                dungeonBasisLevel: 60,
                difficulty: 0,
                new DnfLcg(51852));
            Check(
                "negative random sentinel preserves the specific object drop path",
                FormatPassiveObjectIntents(specificOnly) == "0:Item:6515:1"
                && specificOnly.SpecificDropCount == 1
                && specificOnly.RandomDropCount == 0
                && specificOnly.InvalidActionCount == 0,
                ref failures);

            var goldBoundaryDefinition =
                PassiveObjectRandomDropDefinitionCatalog.Parse(
                    BuildPassiveObjectDropEtc(
                        "1 200 5000 0 0 0 0"));
            var goldBoundary = new PassiveObjectDropPlanningService(
                goldBoundaryDefinition,
                new Dictionary<long, List<(int Id, int Weight)>>(),
                (level, lcg) => 77).Plan(
                    groups,
                    randomOnlyObjects,
                    dungeonBasisLevel: 60,
                    difficulty: 0,
                    new DnfLcg(51852));
            Check(
                "object gold probability uses a strict roll less than rate boundary",
                goldBoundary.Intents.Count == 0
                && goldBoundary.RandomDropCount == 0,
                ref failures);

            var itemBoundaryDefinition =
                PassiveObjectRandomDropDefinitionCatalog.Parse(
                    BuildPassiveObjectDropEtc(
                        "1 200 0 0 5000 0 0"));
            var itemBoundary = new PassiveObjectDropPlanningService(
                itemBoundaryDefinition,
                equipmentPool,
                (level, lcg) => 77).Plan(
                    groups,
                    randomOnlyObjects,
                    dungeonBasisLevel: 60,
                    difficulty: 0,
                    new DnfLcg(51852));
            Check(
                "object item probability includes an equal roll and rate boundary",
                FormatPassiveObjectIntents(itemBoundary) ==
                    "0:Item:7777:1",
                ref failures);

            var rangePool = new Dictionary<long, List<(int Id, int Weight)>>
            {
                [(long)59 * 10] = new List<(int Id, int Weight)>
                {
                    (5959, 100),
                },
                [(long)61 * 10] = new List<(int Id, int Weight)>
                {
                    (6161, 100),
                },
            };
            var rangeBoundary = new PassiveObjectDropPlanningService(
                PassiveObjectRandomDropDefinitionCatalog.Parse(
                    BuildPassiveObjectDropEtc(
                        "1 200 0 0 10000 0 0")),
                rangePool,
                (level, lcg) => 77).Plan(
                    groups,
                    randomOnlyObjects,
                    dungeonBasisLevel: 60,
                    difficulty: 0,
                    new DnfLcg(51852));
            Check(
                "object equipment range includes level-down and excludes level+up",
                FormatPassiveObjectIntents(rangeBoundary) ==
                    "0:Item:5959:1",
                ref failures);
        }

        private static void TestPassiveObjectDropRoomLedgerAndProjection(
            ref int failures)
        {
            var instance = new DungeonInstance(1002, 0);
            var key = new RoomKey(1, 2, -1);
            var maze = new GameWorld.Dungeon.MazeSumInfo
            {
                Index = 5000,
                X = key.X,
                Y = key.Y,
                Monsters = new List<GameWorld.Dungeon.MonsterSumInfo>(),
                SpecialPassiveObjects = new List<SpecialPassiveObjectInfo>(),
            };
            var room = instance.GetOrCreateRoom(
                key,
                roomId => new DungeonInstanceRoom(
                    roomId,
                    key,
                    maze,
                    seed: 42),
                out _);
            var expectedPlan = new PassiveObjectDropPlan(
                new[]
                {
                    new PassiveObjectDropIntent(
                        3,
                        PassiveObjectDropIntentKind.Gold,
                        itemId: 0,
                        amount: 10),
                    new PassiveObjectDropIntent(
                        4,
                        PassiveObjectDropIntentKind.Gold,
                        itemId: 0,
                        amount: 20),
                },
                specificDropCount: 0,
                randomDropCount: 2,
                invalidActionCount: 0,
                unsupportedRandomCategoryCount: 0,
                wasTruncated: false);
            var factoryCalls = 0;
            var plans = new PassiveObjectDropPlan[16];
            Parallel.For(
                0,
                plans.Length,
                index =>
                {
                    plans[index] = room.GetOrCreatePassiveObjectDropPlan(
                        () =>
                        {
                            Interlocked.Increment(ref factoryCalls);
                            return expectedPlan;
                        });
                });
            Check(
                "shared room creates one passive object drop plan under concurrency",
                factoryCalls == 1
                && plans.All(plan => ReferenceEquals(plan, expectedPlan)),
                ref failures);

            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                runGeneration: 1,
                DungeonRunState.Active);
            run.SetCurrentRoom(room);
            run.RoomStates[key] = new RoomState
            {
                InstanceRoom = room,
                Maze = maze,
                KilledSeqIds = new HashSet<ushort>(),
                Seed = room.Seed,
                Lcg = new DnfLcg(room.Seed),
            };
            run.SceneSlotCounter = ushort.MaxValue;
            run.Drops[1] = DropInfo.CreateGold(1, 1);

            var firstProjection =
                PassiveObjectDropProjectionService.ProjectAndRegister(
                    run,
                    room,
                    expectedPlan);
            var replayProjection =
                PassiveObjectDropProjectionService.ProjectAndRegister(
                    run,
                    room,
                    expectedPlan);
            Check(
                "passive object projection wraps and skips occupied scene slots atomically",
                !firstProjection.StaleRoom
                && !firstProjection.SceneSlotsExhausted
                && firstProjection.Entries.Count == 2
                && firstProjection.Entries[0].GlobalSeq == 2
                && firstProjection.Entries[1].GlobalSeq == 3
                && run.SceneSlotCounter == 3
                && run.Drops.Count == 3,
                ref failures);
            Check(
                "passive object projection replay returns the same participant room ledger",
                replayProjection.Entries.Count == 2
                && replayProjection.Entries[0].GlobalSeq == 2
                && replayProjection.Entries[1].GlobalSeq == 3
                && run.Drops.Count == 3,
                ref failures);

            var startMap = DungeonNotificationBuilder.BuildStartMap(
                maze,
                firstMonsterSequence: 1,
                randomSeed: 42,
                extraEntries: firstProjection.Entries);
            Check(
                "START_MAP projects parent actor and reserved scene slot",
                startMap.Length >= 56
                && startMap[17] == 2
                && startMap[18] == 3
                && BitConverter.ToUInt16(startMap, 19) == 2
                && startMap[37] == 4
                && BitConverter.ToUInt16(startMap, 38) == 3,
                ref failures);
        }

        private static string BuildPassiveObjectDropEtc(
            string dropProbabilityRow = "1 200 10000 0 10000 0 0")
        {
            var rarity = string.Join(
                " ",
                Enumerable.Repeat("1000000", 4 * 7));
            var difficulty = string.Join(" ", Enumerable.Repeat("1", 5 * 5));
            var actorType = string.Join(" ", Enumerable.Repeat("1", 5 * 4));
            return string.Join(
                "\r\n",
                "[drop prob count]",
                "1",
                "[drop prob]",
                dropProbabilityRow,
                "[basis of rarity dicision]",
                rarity,
                "[dungeon difficulty drop bonusrate]",
                difficulty,
                "[monster type drop bonusrate]",
                actorType,
                "[item drop ref table]",
                "60 1 1",
                "[item drop rarity control]",
                "0");
        }

        private static string FormatPassiveObjectIntents(
            PassiveObjectDropPlan plan) =>
            string.Join(
                ",",
                plan.Intents.Select(intent =>
                    $"{intent.ObjectIndex}:{intent.Kind}:" +
                    $"{intent.ItemId}:{intent.Amount}"));

        private static void TestProtocolBodies(ref int failures)
        {
            var linked = DungeonNotificationBuilder.BuildLinkedDungeonInfo(
                226,
                2);
            Check(
                "0x0282 uses confirmed int32 dungeon + int32 difficulty",
                BytesEqual(
                    linked,
                    0xE2, 0x00, 0x00, 0x00,
                    0x02, 0x00, 0x00, 0x00),
                ref failures);

            var progress =
                DungeonNotificationBuilder.BuildSequentialDungeonInfo(
                    28,
                    1,
                    0);
            Check(
                "0x025B uses confirmed int32 + byte + int32 body",
                BytesEqual(
                    progress,
                    0x1C, 0x00, 0x00, 0x00,
                    0x01,
                    0x00, 0x00, 0x00, 0x00),
                ref failures);

            var permissionBody = DungeonPermissionBodyBuilder.BuildEntries(
                BuildPermissions((225, 3), (226, 2)));
            Check(
                "0x0005 runtime snapshot reuses init permission layout",
                BytesEqual(
                    permissionBody,
                    0x02, 0x00,
                    0xE1, 0x00, 0x03,
                    0xE2, 0x00, 0x02),
                ref failures);
        }

        private static void TestAntonPermissionBatch(ref int failures)
        {
            const int accountId = 978031;
            const int characterId = 978131;
            const int rollbackCharacterId = 978132;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"anton-permission-batch-{Guid.NewGuid():N}.db");

            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'anton-permission-selftest', '');
INSERT INTO characters (character_id, account_id, name, level)
VALUES (@cid, @aid, 'AntonPermissionMain', 86),
       (@rollbackCid, @aid, 'AntonPermissionRollback', 86);";
                        command.Parameters.AddWithValue("@aid", accountId);
                        command.Parameters.AddWithValue("@cid", characterId);
                        command.Parameters.AddWithValue(
                            "@rollbackCid",
                            rollbackCharacterId);
                        command.ExecuteNonQuery();
                    }
                }

                var repository = new SqliteCharacterStateRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var snapshot = repository.ApplyDungeonPermissionBatch(
                    characterId,
                    BuildPermissions((225, 3), (226, 2), (228, 1)),
                    out var changes);
                Check(
                    "Anton permission changes commit as one batch",
                    Format(changes) == "225:3,226:2,228:1"
                        && Format(snapshot) == "225:3,226:2,228:1",
                    ref failures);

                snapshot = repository.ApplyDungeonPermissionBatch(
                    characterId,
                    BuildPermissions((225, 2), (226, 1), (228, 1)),
                    out changes);
                Check(
                    "Anton permission state is monotonic and replay is a no-op",
                    changes.Count == 0
                        && Format(snapshot) == "225:3,226:2,228:1",
                    ref failures);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = $@"
CREATE TRIGGER fail_anton_permission_batch
BEFORE INSERT ON character_dungeon_permissions
WHEN NEW.character_id = {rollbackCharacterId}
 AND NEW.dungeon_id = 228
BEGIN
    SELECT RAISE(ABORT, 'injected Anton permission failure');
END;";
                        command.ExecuteNonQuery();
                    }
                }

                var failed = false;
                try
                {
                    repository.ApplyDungeonPermissionBatch(
                        rollbackCharacterId,
                        BuildPermissions((225, 3), (228, 1)),
                        out _);
                }
                catch (SqliteException)
                {
                    failed = true;
                }

                Check(
                    "Anton permission batch rolls back every prior write on failure",
                    failed
                        && repository.LoadDungeonPermissions(
                            rollbackCharacterId).Count == 0,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[FAIL] Anton permission batch checks: {ex}");
                failures++;
            }
            finally
            {
                DeleteDatabaseFiles(databasePath);
            }
        }

        private static void TestAntonNormalPvfSequence(ref int failures)
        {
            try
            {
                _ = GameWorld.GameWorldConfig.PvfArchivePath;
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine(
                    "[SKIP] PVF-backed Anton Normal checks: " +
                    "Script.pvf not found");
                return;
            }

            try
            {
                Check(
                    "Anton Normal main sequence comes from paired WDM entries",
                    AntonNormalConquest.TryGetSequence(
                        225,
                        out var sequence)
                        && sequence.ConfigKey == 28
                        && sequence.Difficulty == 2
                        && string.Join(",", sequence.DungeonIds)
                            == "225,226,228,229,231",
                    ref failures);
                if (sequence == null)
                    return;

                Check(
                    "unpaired Anton auxiliary entry is excluded",
                    !AntonNormalConquest.TryGetSequence(227, out _),
                    ref failures);

                var expectedNext = new[] { 226, 228, 229, 231, 0 };
                var expectedPreview = new[] { 228, 229, 231, 0, 0 };
                var plansValid = true;
                for (var index = 0;
                    index < sequence.DungeonIds.Count;
                    index++)
                {
                    plansValid &= AntonNormalConquest.TryResolveClearPlan(
                        sequence.DungeonIds[index],
                        out var plan)
                        && plan.CurrentIndex == index
                        && plan.NextDungeonId == expectedNext[index]
                        && plan.PreviewDungeonId == expectedPreview[index];
                }
                Check(
                    "each clear advances one entry and previews only one lock",
                    plansValid,
                    ref failures);

                Check(
                    "linked challenge follows the WDM sequence and stops at final",
                    AntonNormalConquest.TryResolveLinkedNext(225, out var next)
                        && next == 226
                        && AntonNormalConquest.TryResolveLinkedNext(
                            229,
                            out next)
                        && next == 231
                        && !AntonNormalConquest.TryResolveLinkedNext(
                            231,
                            out _),
                    ref failures);

                Check(
                    "permission states derive from designated difficulty",
                    AntonNormalConquest.TryResolveUnlockedState(
                        226,
                        sequence.Difficulty,
                        out var unlockedState)
                        && unlockedState == 2
                        && AntonNormalConquest.TryResolveCompletedState(
                            226,
                            sequence.Difficulty,
                            out var completedState)
                        && completedState == 3,
                    ref failures);

                Check(
                    "merely opening the first entry does not restore conquest",
                    !AntonNormalConquest.TryResolveSyncState(
                        BuildPermissions((225, 2)),
                        out _),
                    ref failures);

                Check(
                    "first clear restores progress one and locked preview",
                    AntonNormalConquest.TryResolveSyncState(
                        BuildPermissions((225, 3), (226, 2)),
                        out var firstSync)
                        && firstSync.ProgressIndex == 1
                        && Format(firstSync.PermissionEntries)
                            == "225:3,226:2,228:1",
                    ref failures);

                var fullyOpened = BuildPermissions(
                    (225, 3),
                    (226, 3),
                    (228, 3),
                    (229, 3),
                    (231, 2));
                Check(
                    "persisted later progress wins when an earlier stage is replayed",
                    AntonNormalConquest.TryResolveSyncState(
                        fullyOpened,
                        out var openedSync)
                        && openedSync.ProgressIndex == 4
                        && Format(openedSync.PermissionEntries)
                            == "225:3,226:3,228:3,229:3,231:2",
                    ref failures);

                Check(
                    "final clear restores one-past-end progress five",
                    AntonNormalConquest.TryResolveSyncState(
                        BuildPermissions(
                            (225, 3),
                            (226, 3),
                            (228, 3),
                            (229, 3),
                            (231, 3)),
                        out var finalSync)
                        && finalSync.ProgressIndex
                            == sequence.DungeonIds.Count
                        && BytesEqual(
                            DungeonNotificationBuilder
                                .BuildSequentialDungeonInfo(
                                    finalSync.Sequence.ConfigKey,
                                    finalSync.ProgressIndex,
                                    0),
                            0x1C, 0x00, 0x00, 0x00,
                            0x05,
                            0x00, 0x00, 0x00, 0x00),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] Anton Normal PVF checks: {ex}");
                failures++;
            }
        }

        private static List<DungeonPermissionEntrySnapshot> BuildPermissions(
            params (int DungeonId, byte ClearState)[] entries)
        {
            return entries.Select(entry =>
                new DungeonPermissionEntrySnapshot
                {
                    DungeonId = (ushort)entry.DungeonId,
                    ClearState = entry.ClearState,
                }).ToList();
        }

        private static string Format(
            IEnumerable<DungeonPermissionEntrySnapshot> entries)
            => string.Join(",", entries.Select(
                entry => $"{entry.DungeonId}:{entry.ClearState}"));

        private static bool BytesEqual(byte[] actual, params byte[] expected)
            => actual != null && actual.SequenceEqual(expected);

        private static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var path in new[]
            {
                databasePath,
                databasePath + "-wal",
                databasePath + "-shm",
            })
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
        }

        private static void Check(
            string name,
            bool passed,
            ref int failures)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed)
                failures++;
        }
    }
}
