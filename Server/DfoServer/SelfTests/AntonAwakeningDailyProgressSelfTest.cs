using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class AntonAwakeningDailyProgressSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== ANTON_AWAKENING_DAILY_PROGRESS selftest ===");
            var failures = 0;
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_progress_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(
                    tempDbPath,
                    ServerPaths.SchemaFilePath);
                const int accountA = 61000;
                const int accountB = 61001;
                const int characterA = 61010;
                const int characterB = 61011;
                const int stateCharacter = 61012;
                const int incompleteCharacter = 61013;
                const int migrationCharacter = 61016;
                const int staleLegacyCharacter = 61017;
                const int unanchoredLegacyCharacter = 61018;
                const int customMigrationCharacter = 61019;
                SeedAccount(database, accountA, "anton-progress-a");
                SeedAccount(database, accountB, "anton-progress-b");
                SeedCharacter(database, characterA, accountA, "anton-progress-a1");
                SeedCharacter(database, stateCharacter, accountA, "anton-progress-a2");
                SeedCharacter(database, incompleteCharacter, accountA, "anton-progress-a3");
                SeedCharacter(database, migrationCharacter, accountA, "anton-progress-a5");
                SeedCharacter(database, staleLegacyCharacter, accountA, "anton-progress-a6");
                SeedCharacter(database, unanchoredLegacyCharacter, accountA, "anton-progress-a7");
                SeedCharacter(database, customMigrationCharacter, accountA, "anton-progress-a8");
                SeedCharacter(database, characterB, accountB, "anton-progress-b1");

                var beforeUtc = new DateTime(
                    2026, 9, 11, 21, 59, 59, DateTimeKind.Utc);
                var boundaryUtc = new DateTime(
                    2026, 9, 11, 22, 0, 0, DateTimeKind.Utc);
                var dailyReset = new DailyResetService(database);
                var repository = new AntonAwakeningDailyProgressRepository(
                    database,
                    dailyReset);
                var catalog = BuildDailyProgressCatalog(
                    99,
                    243,
                    244,
                    245,
                    246,
                    247);
                var definition = catalog.Definitions.Single();
                var syntheticRepository = new AntonAwakeningDailyProgressRepository(
                    database,
                    dailyReset,
                    catalog);
                Check(
                    "daily marker key is namespaced by sequential group",
                    AntonAwakeningDailyProgressRepository.BuildMarkerKey(
                        definition.GroupKey)
                        == "sequential_progress_v1:99",
                    ref failures);

                repository.EnsureCurrentDayAndLoad(
                    characterA,
                    definition,
                    beforeUtc);
                foreach (var dungeonId in new[]
                    { 225, 231, 243, 244, 245, 246, 247 })
                {
                    SeedPermission(database, characterA, dungeonId, 3);
                }
                SeedPermission(database, characterB, 243, 3);

                var beforeRows = repository.EnsureCurrentDayAndLoad(
                    characterA,
                    definition,
                    beforeUtc);
                Check(
                    "same-day load preserves all five awakening rows",
                    beforeRows.Count == 5,
                    ref failures);

                var afterRows = repository.EnsureCurrentDayAndLoad(
                    characterA,
                    definition,
                    boundaryUtc);
                Check(
                    "06:00 rollover removes only the current character's 243-247 rows",
                    afterRows.Count == 0
                    && PermissionExists(database, characterA, 225)
                    && PermissionExists(database, characterA, 231)
                    && PermissionExists(database, characterB, 243),
                    ref failures);

                SeedCounter(
                    database,
                    characterB,
                    AntonAwakeningDailyProgressRepository.BuildMarkerKey(
                        definition.GroupKey),
                    DailyResetService.PeriodWeek,
                    0);
                var markerFailureRolledBack = false;
                try
                {
                    repository.EnsureCurrentDayAndLoad(
                        characterB,
                        definition,
                        boundaryUtc);
                }
                catch (InvalidOperationException)
                {
                    markerFailureRolledBack = true;
                }
                Check(
                    "marker failure rolls back permission deletion",
                    markerFailureRolledBack
                    && PermissionExists(database, characterB, 243),
                    ref failures);
                Check(
                    "rollover installs one current-day marker",
                    ReadMarker(
                        database,
                        characterA,
                        definition.GroupKey) == 1
                    && ReadDayId(database, characterA)
                        == DailyResetService.TodayId(boundaryUtc),
                    ref failures);

                var currentAntonDefinition = SequentialDungeonDefinitionCatalog
                    .Current.Definitions
                    .SingleOrDefault(value =>
                        value.IsAntonDungeonSequence
                        && value.ShowIndividualProcess
                        && value.EntranceExceptDungeonIds.Count > 0
                        && value.RewardableDungeonIds.Count > 0
                        && value.ClearRewardGroups.Count > 0);
                Check(
                    "current catalog has one semantic Anton awakening definition",
                    currentAntonDefinition != null
                    && SequentialDungeonDefinitionCatalog.Current
                        .IsUniqueAntonAwakeningDefinition(currentAntonDefinition),
                    ref failures);
                if (currentAntonDefinition != null)
                {
                    SeedDailyResetAnchor(
                        database,
                        migrationCharacter,
                        beforeUtc);
                    SeedPermission(database, migrationCharacter, 243, 3);
                    SeedCounter(
                        database,
                        migrationCharacter,
                        "anton_awakening_progress_initialized",
                        DailyResetService.PeriodDay,
                        1);
                    var migratedRows = repository.EnsureCurrentDayAndLoad(
                        migrationCharacter,
                        currentAntonDefinition,
                        beforeUtc);
                    Check(
                        "same-day legacy marker migration preserves progress",
                        migratedRows.Any(row => row.DungeonId == 243)
                        && ReadMarker(database, migrationCharacter, currentAntonDefinition.GroupKey) == 1,
                        ref failures);

                    SeedPermission(database, unanchoredLegacyCharacter, 243, 3);
                    SeedCounter(
                        database,
                        unanchoredLegacyCharacter,
                        "anton_awakening_progress_initialized",
                        DailyResetService.PeriodDay,
                        1);
                    var unanchoredRejected = false;
                    try
                    {
                        repository.EnsureCurrentDayAndLoad(
                            unanchoredLegacyCharacter,
                            currentAntonDefinition,
                            beforeUtc);
                    }
                    catch (InvalidOperationException)
                    {
                        unanchoredRejected = true;
                    }
                    Check(
                        "legacy marker without reset anchor fails closed",
                        unanchoredRejected
                        && PermissionExists(
                            database,
                            unanchoredLegacyCharacter,
                            243)
                        && ReadMarker(
                            database,
                            unanchoredLegacyCharacter,
                            currentAntonDefinition.GroupKey) == 0
                        && ReadLegacyMarker(
                            database,
                            unanchoredLegacyCharacter) == 1,
                        ref failures);

                    SeedDailyResetAnchor(
                        database,
                        staleLegacyCharacter,
                        beforeUtc);
                    SeedPermission(database, staleLegacyCharacter, 243, 3);
                    SeedCounter(
                        database,
                        staleLegacyCharacter,
                        "anton_awakening_progress_initialized",
                        DailyResetService.PeriodDay,
                        1);
                    var staleRows = repository.EnsureCurrentDayAndLoad(
                        staleLegacyCharacter,
                        currentAntonDefinition,
                        boundaryUtc);
                    Check(
                        "cross-day legacy marker is not migrated",
                        staleRows.Count == 0
                        && ReadMarker(database, staleLegacyCharacter, currentAntonDefinition.GroupKey) == 1
                        && ReadLegacyMarker(database, staleLegacyCharacter) == 0,
                        ref failures);

                    var customCatalog = BuildSemanticAntonCatalog(199);
                    var customDefinition = customCatalog.Definitions.Single();
                    var customRepository = new AntonAwakeningDailyProgressRepository(
                        database,
                        dailyReset,
                        customCatalog);
                    SeedDailyResetAnchor(
                        database,
                        customMigrationCharacter,
                        beforeUtc);
                    SeedPermission(database, customMigrationCharacter, 243, 3);
                    SeedCounter(
                        database,
                        customMigrationCharacter,
                        "anton_awakening_progress_initialized",
                        DailyResetService.PeriodDay,
                        1);
                    var customRows = customRepository.EnsureCurrentDayAndLoad(
                        customMigrationCharacter,
                        customDefinition,
                        beforeUtc);
                    Check(
                        "custom semantic catalog controls legacy migration",
                        customRows.Any(row => row.DungeonId == 243)
                        && ReadMarker(
                            database,
                            customMigrationCharacter,
                            customDefinition.GroupKey) == 1,
                        ref failures);
                }

                var anchoredNow = boundaryUtc.AddSeconds(1);
                var service = new AntonAwakeningDailyProgressService(
                    syntheticRepository,
                    catalog,
                    () => anchoredNow);
                var expected = new[]
                {
                    (DungeonId: 243, Progress: (byte)1, Mask: 0x01),
                    (DungeonId: 244, Progress: (byte)2, Mask: 0x03),
                    (DungeonId: 245, Progress: (byte)3, Mask: 0x07),
                    (DungeonId: 246, Progress: (byte)4, Mask: 0x0F),
                };
                foreach (var item in expected)
                {
                    var applied = service.TryApplyClear(
                        characterA,
                        item.DungeonId,
                        out var result);
                    Check(
                        $"clear {item.DungeonId} projects progress and route mask",
                        applied
                        && result.State.Sequence.ConfigKey == 99
                        && result.State.ProgressIndex == item.Progress
                        && result.State.RouteMask == item.Mask,
                        ref failures);
                }
                Check(
                    "clear 247 keeps all prerequisites and advances progress to five",
                    service.TryApplyClear(characterA, 247, out var finalResult)
                    && finalResult.State.ProgressIndex == 5
                    && finalResult.State.RouteMask == 0x0F,
                    ref failures);

                var reconstructed = new AntonAwakeningDailyProgressService(
                    new AntonAwakeningDailyProgressRepository(
                        database,
                        new DailyResetService(database),
                        catalog),
                    catalog,
                    () => anchoredNow.AddMinutes(1));
                Check(
                    "same-day progress survives service reconstruction",
                    reconstructed.TryRestore(characterA, 99, out var restored)
                    && restored.ProgressIndex == 5
                    && restored.RouteMask == 0x0F,
                    ref failures);

                syntheticRepository.EnsureCurrentDayAndLoad(
                    stateCharacter,
                    definition,
                    anchoredNow);
                var completedState = ResolveCompletedState(definition, 245);
                Check(
                    "current PVF completed state for awakening difficulty is three",
                    completedState == 3,
                    ref failures);
                RecordState(
                    syntheticRepository,
                    definition,
                    stateCharacter,
                    245,
                    1,
                    anchoredNow);
                Check(
                    "state one does not satisfy a route bit",
                    service.TryRestore(stateCharacter, 99, out var stateOne)
                    && stateOne.RouteMask == 0,
                    ref failures);
                RecordState(
                    syntheticRepository,
                    definition,
                    stateCharacter,
                    245,
                    2,
                    anchoredNow);
                Check(
                    "state two does not satisfy a route bit",
                    service.TryRestore(stateCharacter, 99, out var stateTwo)
                    && stateTwo.RouteMask == 0,
                    ref failures);
                RecordState(
                    syntheticRepository,
                    definition,
                    stateCharacter,
                    245,
                    completedState,
                    anchoredNow);
                Check(
                    "completed state sets only dungeon 245's route bit",
                    service.TryRestore(stateCharacter, 99, out var stateThree)
                    && stateThree.RouteMask == 0x04,
                    ref failures);

                syntheticRepository.EnsureCurrentDayAndLoad(
                    incompleteCharacter,
                    definition,
                    anchoredNow);
                foreach (var dungeonId in new[] { 243, 244, 246 })
                {
                    RecordState(
                        syntheticRepository,
                        definition,
                        incompleteCharacter,
                        dungeonId,
                        ResolveCompletedState(definition, dungeonId),
                        anchoredNow);
                }
                var leaderDecision = service.EvaluateAdmission(characterA, 247);
                var followerDecision = service.EvaluateAdmission(
                    incompleteCharacter,
                    247);
                Check(
                    "247 admission is character-scoped and lists the exact missing prerequisite",
                    leaderDecision.Allowed
                    && !followerDecision.Allowed
                    && followerDecision.MissingDungeonIds.SequenceEqual(
                        new[] { 245 }),
                    ref failures);
                var firstDecision = service.EvaluateAdmission(
                    incompleteCharacter,
                    243);
                var secondDecision = service.EvaluateAdmission(
                    incompleteCharacter,
                    244);
                var thirdDecision = service.EvaluateAdmission(
                    incompleteCharacter,
                    245);
                var fourthDecision = service.EvaluateAdmission(
                    incompleteCharacter,
                    246);
                Check(
                    "each sequential target requires only its ordered completed prefix",
                    firstDecision.Status == AntonAwakeningAdmissionStatus.Allowed
                    && secondDecision.Status
                        == AntonAwakeningAdmissionStatus.Allowed
                    && thirdDecision.Status
                        == AntonAwakeningAdmissionStatus.Allowed
                    && fourthDecision.Status
                        == AntonAwakeningAdmissionStatus.MissingPrerequisites
                    && fourthDecision.MissingDungeonIds.SequenceEqual(
                        new[] { 245 }),
                    ref failures);
                Check(
                    "dungeon outside the sequential definition is not gated",
                    service.EvaluateAdmission(0, 9999).Status
                        == AntonAwakeningAdmissionStatus.NotApplicable,
                    ref failures);

                var ambiguousCatalog = SequentialDungeonDefinitionCatalog.Parse(
                    @"
[sequential dungeon]
201
[dungeon index check]
1201 1202
[/dungeon index check]
[show individual process]
[/show individual process]
[entrance except dungeon]
1202
[/entrance except dungeon]
[/sequential dungeon]
[sequential dungeon]
202
[dungeon index check]
1201 1202
[/dungeon index check]
[show individual process]
[/show individual process]
[entrance except dungeon]
1202
[/entrance except dungeon]
[/sequential dungeon]",
                    _ => (byte)2);
                var ambiguousService = new AntonAwakeningDailyProgressService(
                    new AntonAwakeningDailyProgressRepository(
                        database,
                        dailyReset,
                        ambiguousCatalog),
                    ambiguousCatalog,
                    () => anchoredNow);
                var ambiguousDecision = ambiguousService.EvaluateAdmission(
                    characterA,
                    1202);
                Check(
                    "ambiguous sequential definition rejects admission",
                    !ambiguousDecision.Allowed
                    && ambiguousDecision.Status
                        == AntonAwakeningAdmissionStatus.InvalidState,
                    ref failures);

                var invalidRejected = false;
                try
                {
                    RecordState(
                        syntheticRepository,
                        definition,
                        characterA,
                        242,
                        3,
                        anchoredNow);
                }
                catch (ArgumentException)
                {
                    invalidRejected = true;
                }
                Check(
                    "out-of-scope updates fail before mutating permission rows",
                    invalidRejected
                    && !PermissionExists(database, characterA, 242),
                    ref failures);


                const int rollbackCharacter = 61014;
                SeedCharacter(
                    database,
                    rollbackCharacter,
                    accountA,
                    "anton-progress-a4");
                syntheticRepository.EnsureCurrentDayAndLoad(
                    rollbackCharacter,
                    definition,
                    anchoredNow);
                CreateFailingPermissionTrigger(database);
                var mutationRolledBack = false;
                try
                {
                    syntheticRepository.RecordClearAndLoad(
                        rollbackCharacter,
                        definition,
                        new[]
                        {
                            new DungeonPermissionEntrySnapshot
                            {
                                DungeonId = 243,
                                ClearState = ResolveCompletedState(
                                    definition,
                                    243),
                            },
                            new DungeonPermissionEntrySnapshot
                            {
                                DungeonId = 245,
                                ClearState = ResolveCompletedState(
                                    definition,
                                    245),
                            },
                        },
                        anchoredNow,
                        out _);
                }
                catch (SqliteException)
                {
                    mutationRolledBack = true;
                }
                finally
                {
                    DropFailingPermissionTrigger(database);
                }
                Check(
                    "multi-row clear mutation rolls back atomically on SQL failure",
                    mutationRolledBack
                    && !PermissionExists(database, rollbackCharacter, 243)
                    && !PermissionExists(database, rollbackCharacter, 245),
                    ref failures);

                const int dynamicCharacter = 61015;
                SeedCharacter(
                    database,
                    dynamicCharacter,
                    accountA,
                    "sequential-progress-dynamic");
                var dynamicCatalog = BuildDailyProgressCatalog(
                    101,
                    1001,
                    1003,
                    1008);
                var dynamicDefinition = dynamicCatalog.Definitions.Single();
                var dynamicRepository = new AntonAwakeningDailyProgressRepository(
                    database,
                    dailyReset,
                    dynamicCatalog);
                dynamicRepository.EnsureCurrentDayAndLoad(
                    dynamicCharacter,
                    dynamicDefinition,
                    beforeUtc);
                foreach (var dungeonId in new[] { 1001, 1002, 1003, 1008 })
                    SeedPermission(database, dynamicCharacter, dungeonId, 2);
                var dynamicRows = dynamicRepository.EnsureCurrentDayAndLoad(
                    dynamicCharacter,
                    dynamicDefinition,
                    boundaryUtc);
                Check(
                    "repository uses the Definition's dynamic dungeon set",
                    dynamicRows.Count == 0
                    && !PermissionExists(database, dynamicCharacter, 1001)
                    && PermissionExists(database, dynamicCharacter, 1002)
                    && !PermissionExists(database, dynamicCharacter, 1003)
                    && !PermissionExists(database, dynamicCharacter, 1008),
                    ref failures);
                var dynamicSnapshot = dynamicRepository.RecordClearAndLoad(
                    dynamicCharacter,
                    dynamicDefinition,
                    new[]
                    {
                        new DungeonPermissionEntrySnapshot
                        {
                            DungeonId = 1003,
                            ClearState = 2,
                        },
                    },
                    boundaryUtc,
                    out var dynamicChanges);
                Check(
                    "repository loads and updates only dynamic Definition IDs",
                    dynamicChanges.Count == 1
                    && dynamicChanges[0].DungeonId == 1003
                    && dynamicSnapshot.Count == 1
                    && dynamicSnapshot[0].DungeonId == 1003
                    && PermissionExists(database, dynamicCharacter, 1002),
                    ref failures);
                var dynamicOutOfScopeRejected = false;
                try
                {
                    dynamicRepository.RecordClearAndLoad(
                        dynamicCharacter,
                        dynamicDefinition,
                        new[]
                        {
                            new DungeonPermissionEntrySnapshot
                            {
                                DungeonId = 1002,
                                ClearState = 2,
                            },
                        },
                        boundaryUtc,
                        out _);
                }
                catch (ArgumentException)
                {
                    dynamicOutOfScopeRejected = true;
                }
                Check(
                    "repository validation rejects IDs outside the Definition",
                    dynamicOutOfScopeRejected,
                    ref failures);

                Check(
                    "key 41 and key 28 retain their PVF-derived identities",
                    AntonNormalConquest.TryGetSequenceByKey(41, out var awakening)
                    && awakening.DungeonIds.SequenceEqual(
                        new[] { 243, 244, 245, 246, 247 })
                    && AntonNormalConquest.TryGetSequenceByKey(28, out var normal)
                    && normal.DungeonIds.Take(5).SequenceEqual(
                        new[] { 225, 226, 228, 229, 231 })
                    && AntonNormalConquest.TryResolveClearPlan(
                        225,
                        out var normalPlan)
                    && normalPlan.Sequence.ConfigKey == 28,
                    ref failures);

                // 暴走序列由 PVF 语义唯一确定(不硬编码 41): 只有它同时带
                // [show individual process] / [entrance except dungeon] /
                // [rewardable dungeon index] / [clear reward item]。
                var sequenceCatalog = SequentialDungeonDefinitionCatalog.Current;
                Check(
                    "catalog resolves exactly one Anton awakening definition",
                    sequenceCatalog.TryResolveAntonAwakeningDefinition(
                        out var resolvedAwakening)
                    && resolvedAwakening.GroupKey == 41
                    && sequenceCatalog.IsUniqueAntonAwakeningDefinition(
                        resolvedAwakening),
                    ref failures);
                Check(
                    "Anton normal entrance is an Anton sequence but not the "
                    + "awakening one",
                    sequenceCatalog.TryGetByGroupKey(28, out var normalEntrance)
                    && normalEntrance.IsAntonDungeonSequence
                    && !sequenceCatalog.IsUniqueAntonAwakeningDefinition(
                        normalEntrance),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[ANTON_AWAKENING_DAILY_PROGRESS] EXCEPTION: {ex}");
                failures++;
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }

            Console.WriteLine(
                failures == 0
                    ? "ANTON_AWAKENING_DAILY_PROGRESS selftest passed."
                    : $"ANTON_AWAKENING_DAILY_PROGRESS selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte ResolveCompletedState(
            SequentialDungeonDefinition definition,
            int dungeonId)
        {
            if (definition == null
                || !AntonNormalConquest.TryResolveCompletedState(
                    dungeonId,
                    definition.Difficulty,
                    out var state))
            {
                throw new InvalidOperationException(
                    $"Unable to resolve completed state for dungeon {dungeonId}.");
            }
            return state;
        }

        private static void RecordState(
            AntonAwakeningDailyProgressRepository repository,
            SequentialDungeonDefinition definition,
            int characterId,
            int dungeonId,
            byte clearState,
            DateTime utcNow)
        {
            repository.RecordClearAndLoad(
                characterId,
                definition,
                new[]
                {
                    new DungeonPermissionEntrySnapshot
                    {
                        DungeonId = checked((ushort)dungeonId),
                        ClearState = clearState,
                    },
                },
                utcNow,
                out _);
        }

        private static SequentialDungeonDefinitionCatalog
            BuildDailyProgressCatalog(int groupKey, params int[] dungeonIds)
        {
            var joinedDungeonIds = string.Join(" ", dungeonIds);
            var finalDungeonId = dungeonIds[dungeonIds.Length - 1];
            var config = $@"
[sequential dungeon]
{groupKey}
[dungeon index check]
{joinedDungeonIds}
[/dungeon index check]
[show individual process]
[/show individual process]
[entrance except dungeon]
{finalDungeonId}
[/entrance except dungeon]
[always visible dungeon]
{finalDungeonId}
[/always visible dungeon]
[/sequential dungeon]";
            return SequentialDungeonDefinitionCatalog.Parse(
                config,
                _ => (byte)2);
        }

        private static SequentialDungeonDefinitionCatalog
            BuildSemanticAntonCatalog(int groupKey)
        {
            var config = $@"
[sequential dungeon]
{groupKey}
[dungeon index check]
243 244 245 246 247
[/dungeon index check]
[show individual process]
[/show individual process]
[entrance except dungeon]
247
[/entrance except dungeon]
[rewardable dungeon index]
247
[/rewardable dungeon index]
[clear reward item]
1 10157831 0
[/clear reward item]
[/sequential dungeon]";
            return SequentialDungeonDefinitionCatalog.Parse(
                config,
                _ => (byte)2,
                _ => true);
        }

        private static void SeedAccount(
            GameDatabase database,
            int accountId,
            string mid)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@mid", mid);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void SeedCharacter(
            GameDatabase database,
            int characterId,
            int accountId,
            string name)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@name", name);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void SeedPermission(
            GameDatabase database,
            int characterId,
            int dungeonId,
            byte clearState)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO character_dungeon_permissions
    (character_id, sort_order, dungeon_id, clear_state)
VALUES
    (@cid,
     (SELECT COALESCE(MAX(sort_order), 0) + 1
      FROM character_dungeon_permissions
      WHERE character_id = @cid),
     @did,
     @state);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@did", dungeonId);
                    command.Parameters.AddWithValue("@state", clearState);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static bool PermissionExists(
            GameDatabase database,
            int characterId,
            int dungeonId)
        {
            return database.Read(connection =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM character_dungeon_permissions
WHERE character_id = @cid AND dungeon_id = @did;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@did", dungeonId);
                    return Convert.ToInt32(command.ExecuteScalar()) > 0;
                }
            });
        }

        private static void SeedCounter(
            GameDatabase database,
            int characterId,
            string key,
            string period,
            long value)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO character_daily_counters
    (character_id, counter_key, period, value)
VALUES (@cid, @key, @period, @value);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@key", key);
                    command.Parameters.AddWithValue("@period", period);
                    command.Parameters.AddWithValue("@value", value);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void CreateFailingPermissionTrigger(
            GameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
CREATE TRIGGER selftest_fail_anton_permission
BEFORE INSERT ON character_dungeon_permissions
WHEN NEW.dungeon_id = 245
BEGIN
    SELECT RAISE(ABORT, 'selftest permission failure');
END;";
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void DropFailingPermissionTrigger(GameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        "DROP TRIGGER IF EXISTS selftest_fail_anton_permission;";
                    command.ExecuteNonQuery();
                }
            });
        }

        private static long ReadMarker(
            GameDatabase database,
            int characterId,
            int groupKey)
        {
            return database.Read(connection =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT value
FROM character_daily_counters
WHERE character_id = @cid
  AND counter_key = @key
  AND period = 'day';";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue(
                        "@key",
                        AntonAwakeningDailyProgressRepository.BuildMarkerKey(
                            groupKey));
                    return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
                }
            });
        }

        private static void SeedDailyResetAnchor(
            GameDatabase database,
            int characterId,
            DateTime utcNow)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO character_daily_reset (character_id, day_id, week_id)
VALUES (@cid, @day, 0);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue(
                        "@day",
                        DailyResetService.TodayId(utcNow));
                    command.ExecuteNonQuery();
                }
            });
        }

        private static long ReadLegacyMarker(
            GameDatabase database,
            int characterId)
        {
            return database.Read(connection =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT value
FROM character_daily_counters
WHERE character_id = @cid
  AND counter_key = 'anton_awakening_progress_initialized'
  AND period = 'day';";
                    command.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
                }
            });
        }

        private static int ReadDayId(GameDatabase database, int characterId)
        {
            return database.Read(connection =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT day_id
FROM character_daily_reset
WHERE character_id = @cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(command.ExecuteScalar() ?? 0);
                }
            });
        }

        private static void TryDelete(string path)
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

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
