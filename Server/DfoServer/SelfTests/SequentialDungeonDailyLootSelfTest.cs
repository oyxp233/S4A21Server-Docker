using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    public static class SequentialDungeonDailyLootSelfTest
    {
        private const int CharacterId = 57901;
        private const int MonsterA = 56675;
        private const int MonsterB = 56678;
        private const int RecoveryMonster = 123456789;

        public static int Run()
        {
            Console.WriteLine("=== SEQUENTIAL_DUNGEON_DAILY_LOOT selftest ===");
            var failures = 0;

            VerifyGeneratedResultClaims(ref failures);
            VerifyEmptyAndIndependentKeys(ref failures);
            VerifyDurableAndDailyRollover(ref failures);
            VerifyGenerationKeepsStartingGameDay(ref failures);
            VerifyStaleAnchorCannotRewindAdvancedPeriod(ref failures);
            VerifyStaleClaimRollsBackAfterConcurrentRollover(ref failures);
            VerifyAmbiguousDefinitionFailsClosed(ref failures);
            VerifyClaimFailureRollsBack(ref failures);
            VerifyGenerationExceptionPropagates(ref failures);
            VerifyRegisteredDropRollbackIsExact(ref failures);
            VerifyClaimedOutcomeJournalCheckpoints(ref failures);
            VerifyClaimedOutcomeReplaysThroughKillRecovery(ref failures);
            VerifyEmptyOutcomeReplaysThroughKillRecovery(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "SEQUENTIAL_DUNGEON_DAILY_LOOT selftest passed."
                    : $"SEQUENTIAL_DUNGEON_DAILY_LOOT selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyGeneratedResultClaims(ref int failures)
        {
            failures += WithDatabase(
                "generated",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    var guard = new SequentialDungeonDailyLootGuard(dailyReset);
                    var rolledBack = new List<DropInfo>();

                    var itemOnly = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () => ItemResult(10, 90001),
                        drops => rolledBack.AddRange(drops));
                    var duplicateGenerated = false;
                    var duplicate = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () =>
                        {
                            duplicateGenerated = true;
                            return ItemResult(11, 90002);
                        },
                        drops => rolledBack.AddRange(drops));
                    var goldOnly = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterB,
                        () => new MonsterDropResult
                        {
                            GoldAmount = 1234,
                            Drops = new List<DropInfo>(),
                        },
                        drops => rolledBack.AddRange(drops));

                    Check(
                        "item-only generation claims the configured monster once",
                        itemOnly.Drops?.Count == 1
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    definition.GroupKey,
                                    MonsterA)) == 1,
                        ref localFailures);
                    Check(
                        "the same monster is suppressed before another generation",
                        duplicate.Drops?.Count == 0
                            && duplicate.GoldAmount == 0
                            && !duplicateGenerated,
                        ref localFailures);
                    Check(
                        "a pure-gold result claims an independent configured monster",
                        goldOnly.GoldAmount == 1234
                            && goldOnly.Drops?.Count == 0
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    definition.GroupKey,
                                    MonsterB)) == 1,
                        ref localFailures);
                    Check(
                        "successful claims do not invoke rollback",
                        rolledBack.Count == 0,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyEmptyAndIndependentKeys(ref int failures)
        {
            failures += WithDatabase(
                "independent",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var firstDefinition = ParseDefinition(groupKey: 99);
                    var otherDefinition = ParseDefinition(groupKey: 100);
                    var guard = new SequentialDungeonDailyLootGuard(dailyReset);

                    var empty = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        firstDefinition,
                        MonsterA,
                        EmptyResult,
                        _ => throw new InvalidOperationException(
                            "empty results must not roll back"));
                    var emptyDidNotClaim = dailyReset.GetCounter(
                        CharacterId,
                        SequentialDungeonDailyLootGuard.BuildCounterKey(
                            firstDefinition.GroupKey,
                            MonsterA)) == 0;
                    var multiItem = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        firstDefinition,
                        MonsterA,
                        () => new MonsterDropResult
                        {
                            Drops = new List<DropInfo>
                            {
                                DropInfo.CreateItem(20, 90011, 1),
                                DropInfo.CreateItem(21, 90012, 2),
                            },
                        },
                        _ => { });
                    var mixed = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        firstDefinition,
                        MonsterB,
                        () => new MonsterDropResult
                        {
                            GoldAmount = 500,
                            Drops = new List<DropInfo>
                            {
                                DropInfo.CreateItem(22, 90013, 1),
                            },
                        },
                        _ => { });
                    var otherGroup = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        otherDefinition,
                        MonsterA,
                        () => ItemResult(23, 90014),
                        _ => { });
                    var nonConfigured = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        firstDefinition,
                        monsterId: 99999,
                        generate: () => ItemResult(24, 90015),
                        rollback: _ => { });

                    Check(
                        "an empty roll remains empty and does not claim",
                        empty.Drops?.Count == 0
                            && emptyDidNotClaim,
                        ref localFailures);
                    Check(
                        "multiple items in one death consume only one claim",
                        multiItem.Drops?.Count == 2
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    firstDefinition.GroupKey,
                                    MonsterA)) == 1,
                        ref localFailures);
                    Check(
                        "mixed item and gold generation consumes only one claim",
                        mixed.Drops?.Count == 1
                            && mixed.GoldAmount == 500
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    firstDefinition.GroupKey,
                                    MonsterB)) == 1,
                        ref localFailures);
                    Check(
                        "the same monster in another sequential group is independent",
                        otherGroup.Drops?.Count == 1
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    otherDefinition.GroupKey,
                                    MonsterA)) == 1,
                        ref localFailures);
                    Check(
                        "a non-configured monster generates without creating a counter",
                        nonConfigured.Drops?.Count == 1
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    firstDefinition.GroupKey,
                                    99999)) == 0,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyDurableAndDailyRollover(ref int failures)
        {
            failures += WithDatabase(
                "durable",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    var firstGuard = new SequentialDungeonDailyLootGuard(
                        dailyReset);
                    firstGuard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () => ItemResult(30, 90101),
                        _ => { });

                    var recreatedGenerated = false;
                    var recreatedGuard = new SequentialDungeonDailyLootGuard(
                        new DailyResetService(database));
                    var afterReentry = recreatedGuard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () =>
                        {
                            recreatedGenerated = true;
                            return ItemResult(31, 90102);
                        },
                        _ => { });
                    Check(
                        "a generated drop stays claimed after guard reconstruction",
                        afterReentry.Drops?.Count == 0
                            && !recreatedGenerated,
                        ref localFailures);

                    ForcePreviousGameDay(database);
                    var afterRollover = recreatedGuard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () => ItemResult(32, 90103),
                        _ => { });
                    Check(
                        "the Beijing 06:00 game-day rollover restores eligibility",
                        afterRollover.Drops?.Count == 1
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    definition.GroupKey,
                                    MonsterA)) == 1,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyGenerationKeepsStartingGameDay(
            ref int failures)
        {
            failures += WithDatabase(
                "anchored-boundary",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    var beforeBoundary = new DateTime(
                        2026,
                        9,
                        14,
                        21,
                        59,
                        59,
                        DateTimeKind.Utc);
                    var afterBoundary = beforeBoundary.AddSeconds(2);
                    var utcNow = beforeBoundary;
                    var guard = new SequentialDungeonDailyLootGuard(
                        dailyReset,
                        () => utcNow);

                    var generated = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () =>
                        {
                            utcNow = afterBoundary;
                            return ItemResult(33, 90104);
                        },
                        _ => { });
                    var startingDayCount = dailyReset.GetCounter(
                        CharacterId,
                        SequentialDungeonDailyLootGuard.BuildCounterKey(
                            definition.GroupKey,
                            MonsterA),
                        DailyResetService.PeriodDay,
                        beforeBoundary);
                    var newDayCount = dailyReset.GetCounter(
                        CharacterId,
                        SequentialDungeonDailyLootGuard.BuildCounterKey(
                            definition.GroupKey,
                            MonsterA),
                        DailyResetService.PeriodDay,
                        afterBoundary);
                    var afterBoundaryGuard =
                        new SequentialDungeonDailyLootGuard(
                            dailyReset,
                            () => afterBoundary);
                    var nextDay = afterBoundaryGuard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () => ItemResult(34, 90105),
                        _ => { });

                    Check(
                        "one generation is claimed against its starting game day",
                        generated.Drops?.Count == 1
                            && startingDayCount == 1
                            && newDayCount == 0
                            && nextDay.Drops?.Count == 1,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyAmbiguousDefinitionFailsClosed(
            ref int failures)
        {
            failures += WithDatabase(
                "ambiguous",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var catalog = SequentialDungeonDefinitionCatalog.Parse(
                        $@"
[sequential dungeon]
99
[dungeon index check]
320
[/dungeon index check]
[monster index check]
{MonsterA}
[/monster index check]
[show individual process]
[/show individual process]
[/sequential dungeon]
[sequential dungeon]
100
[dungeon index check]
320
[/dungeon index check]
[monster index check]
{MonsterA}
[/monster index check]
[show individual process]
[/show individual process]
[/sequential dungeon]",
                        _ => (byte)2);
                    var ambiguousInstance = new DungeonInstance(
                        dungeonId: 320,
                        difficulty: 0,
                        sequentialCatalog: catalog);
                    var guard = new SequentialDungeonDailyLootGuard(
                        dailyReset);
                    var ambiguousGenerated = false;
                    var ambiguous = guard.GenerateAndMark(
                        CharacterId,
                        ambiguousInstance.SequentialDefinitionResolution,
                        ambiguousInstance.SequentialDefinition,
                        MonsterA,
                        () =>
                        {
                            ambiguousGenerated = true;
                            return ItemResult(35, 90106);
                        },
                        _ => { });

                    var ordinaryInstance = new DungeonInstance(
                        dungeonId: 321,
                        difficulty: 0,
                        sequentialCatalog: catalog);
                    var ordinary = guard.GenerateAndMark(
                        CharacterId,
                        ordinaryInstance.SequentialDefinitionResolution,
                        ordinaryInstance.SequentialDefinition,
                        MonsterA,
                        () => ItemResult(36, 90107),
                        _ => { });

                    Check(
                        "an ambiguous frozen definition fails closed",
                        ambiguousInstance.SequentialDefinitionResolution
                            == SequentialDungeonCapabilityResolution.Ambiguous
                            && ambiguousInstance.SequentialDefinition == null
                            && ambiguous.Drops?.Count == 0
                            && ambiguous.GoldAmount == 0
                            && !ambiguousGenerated,
                        ref localFailures);
                    Check(
                        "an absent definition preserves ordinary dungeon drops",
                        ordinaryInstance.SequentialDefinitionResolution
                            == SequentialDungeonCapabilityResolution.Absent
                            && ordinaryInstance.SequentialDefinition == null
                            && ordinary.Drops?.Count == 1,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyStaleAnchorCannotRewindAdvancedPeriod(
            ref int failures)
        {
            failures += WithDatabase(
                "stale-read",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    var beforeMondayBoundary = new DateTime(
                        2026,
                        9,
                        13,
                        21,
                        59,
                        59,
                        DateTimeKind.Utc);
                    var afterMondayBoundary = beforeMondayBoundary.AddSeconds(2);
                    const string newDayKey = "selftest_new_day_claim";
                    const string newWeekKey = "selftest_new_week_claim";

                    dailyReset.TryIncrementCounter(
                        CharacterId,
                        newDayKey,
                        cap: 1,
                        period: DailyResetService.PeriodDay,
                        utcNow: afterMondayBoundary);
                    dailyReset.TryIncrementCounter(
                        CharacterId,
                        newWeekKey,
                        cap: 1,
                        period: DailyResetService.PeriodWeek,
                        utcNow: afterMondayBoundary);

                    var generated = false;
                    var guard = new SequentialDungeonDailyLootGuard(
                        dailyReset,
                        () => beforeMondayBoundary);
                    var stale = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () =>
                        {
                            generated = true;
                            return ItemResult(37, 90108);
                        },
                        _ => { });

                    var standaloneReadRejected = false;
                    try
                    {
                        dailyReset.GetCounter(
                            CharacterId,
                            SequentialDungeonDailyLootGuard.BuildCounterKey(
                                definition.GroupKey,
                                MonsterA),
                            DailyResetService.PeriodDay,
                            beforeMondayBoundary);
                    }
                    catch (InvalidOperationException)
                    {
                        standaloneReadRejected = true;
                    }

                    var transactionReadRejected = false;
                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            dailyReset.GetCounter(
                                connection,
                                transaction,
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    definition.GroupKey,
                                    MonsterA),
                                DailyResetService.PeriodDay,
                                beforeMondayBoundary);
                        }
                        catch (InvalidOperationException)
                        {
                            transactionReadRejected = true;
                        }
                        transaction.Rollback();
                    }

                    ReadResetState(
                        database,
                        out var dayId,
                        out var weekId);
                    Check(
                        "a stale anchored read fails closed without rewinding a newer day or week",
                        stale.Drops?.Count == 0
                            && stale.GoldAmount == 0
                            && !generated
                            && standaloneReadRejected
                            && transactionReadRejected
                            && dayId == DailyResetService.TodayId(
                                afterMondayBoundary)
                            && weekId == DailyResetService.WeekId(
                                afterMondayBoundary)
                            && ReadCounterDirect(database, newDayKey) == 1
                            && ReadCounterDirect(database, newWeekKey) == 1,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyStaleClaimRollsBackAfterConcurrentRollover(
            ref int failures)
        {
            failures += WithDatabase(
                "stale-claim",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    var beforeMondayBoundary = new DateTime(
                        2026,
                        9,
                        13,
                        21,
                        59,
                        59,
                        DateTimeKind.Utc);
                    var afterMondayBoundary = beforeMondayBoundary.AddSeconds(2);
                    var generationStarted = new ManualResetEventSlim(false);
                    var allowGenerationToFinish = new ManualResetEventSlim(false);
                    var rolledBack = new List<DropInfo>();
                    var oldGuard = new SequentialDungeonDailyLootGuard(
                        dailyReset,
                        () => beforeMondayBoundary);

                    var oldAttempt = Task.Run(() => oldGuard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () =>
                        {
                            generationStarted.Set();
                            if (!allowGenerationToFinish.Wait(
                                    TimeSpan.FromSeconds(5)))
                            {
                                throw new TimeoutException(
                                    "stale-claim generation was not released");
                            }
                            return ItemResult(38, 90109);
                        },
                        drops => rolledBack.AddRange(drops)));

                    if (!generationStarted.Wait(TimeSpan.FromSeconds(5)))
                    {
                        allowGenerationToFinish.Set();
                        throw new TimeoutException(
                            "stale-claim generation did not start");
                    }

                    dailyReset.TryIncrementCounter(
                        CharacterId,
                        SequentialDungeonDailyLootGuard.BuildCounterKey(
                            definition.GroupKey,
                            MonsterB),
                        cap: 1,
                        period: DailyResetService.PeriodDay,
                        utcNow: afterMondayBoundary);
                    dailyReset.TryIncrementCounter(
                        CharacterId,
                        "selftest_new_week_during_generation",
                        cap: 1,
                        period: DailyResetService.PeriodWeek,
                        utcNow: afterMondayBoundary);
                    allowGenerationToFinish.Set();
                    var staleResult = oldAttempt.GetAwaiter().GetResult();

                    var newDayGuard = new SequentialDungeonDailyLootGuard(
                        dailyReset,
                        () => afterMondayBoundary);
                    var newDayMonsterA = newDayGuard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () => ItemResult(39, 90110),
                        _ => { });

                    ReadResetState(
                        database,
                        out var dayId,
                        out var weekId);
                    Check(
                        "a stale claim rolls back while preserving newer counters and eligibility",
                        staleResult.Drops?.Count == 0
                            && staleResult.GoldAmount == 0
                            && rolledBack.Count == 1
                            && rolledBack[0].SceneSlot == 38
                            && newDayMonsterA.Drops?.Count == 1
                            && ReadCounterDirect(
                                database,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    definition.GroupKey,
                                    MonsterB)) == 1
                            && ReadCounterDirect(
                                database,
                                "selftest_new_week_during_generation") == 1
                            && dayId == DailyResetService.TodayId(
                                afterMondayBoundary)
                            && weekId == DailyResetService.WeekId(
                                afterMondayBoundary),
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyClaimFailureRollsBack(ref int failures)
        {
            failures += WithDatabase(
                "claim-race",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    var guard = new SequentialDungeonDailyLootGuard(dailyReset);
                    var rolledBack = new List<DropInfo>();
                    var raced = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () =>
                        {
                            dailyReset.TryIncrementCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    definition.GroupKey,
                                    MonsterA),
                                cap: 1,
                                period: DailyResetService.PeriodDay);
                            return ItemResult(40, 90201);
                        },
                        drops => rolledBack.AddRange(drops));
                    Check(
                        "a concurrently consumed cap rolls back the generated batch",
                        raced.Drops?.Count == 0
                            && raced.GoldAmount == 0
                            && rolledBack.Count == 1
                            && rolledBack[0].SceneSlot == 40,
                        ref localFailures);
                    return localFailures;
                });

            failures += WithDatabase(
                "claim-exception",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    InjectCounterFailure(database);
                    var guard = new SequentialDungeonDailyLootGuard(dailyReset);
                    var rolledBack = new List<DropInfo>();
                    var failed = guard.GenerateAndMark(
                        CharacterId,
                        SequentialDungeonCapabilityResolution.Resolved,
                        definition,
                        MonsterA,
                        () => ItemResult(41, 90202),
                        drops => rolledBack.AddRange(drops));
                    Check(
                        "a counter persistence exception rolls back and fails closed",
                        failed.Drops?.Count == 0
                            && failed.GoldAmount == 0
                            && rolledBack.Count == 1
                            && rolledBack[0].SceneSlot == 41,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyGenerationExceptionPropagates(
            ref int failures)
        {
            failures += WithDatabase(
                "generate-exception",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    var guard = new SequentialDungeonDailyLootGuard(dailyReset);
                    var generated = false;
                    var propagated = false;

                    try
                    {
                        guard.GenerateAndMark(
                            CharacterId,
                            SequentialDungeonCapabilityResolution.Resolved,
                            definition,
                            MonsterA,
                            () =>
                            {
                                generated = true;
                                throw new InvalidOperationException(
                                    "injected generation failure");
                            },
                            _ => throw new InvalidOperationException(
                                "generation failures must not roll back"));
                    }
                    catch (InvalidOperationException ex)
                    {
                        propagated = string.Equals(
                            ex.Message,
                            "injected generation failure",
                            StringComparison.Ordinal);
                    }

                    Check(
                        "a generation exception propagates without claiming",
                        generated
                            && propagated
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    definition.GroupKey,
                                    MonsterA)) == 0,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyRegisteredDropRollbackIsExact(
            ref int failures)
        {
            var run = new DungeonRun();
            var service = new DropService();
            var exact = DropInfo.CreateItem(50, 90301, 2);
            exact.DropGroupId = 700;
            var changedCount = DropInfo.CreateItem(51, 90302, 1);
            changedCount.DropGroupId = 701;
            var changedGroup = DropInfo.CreateItem(52, 90303, 1);
            changedGroup.DropGroupId = 702;
            var changedTemplate = DropInfo.CreateItem(53, 90304, 1);
            changedTemplate.DropGroupId = 703;

            run.Drops[50] = exact;
            var currentChangedCount = changedCount;
            currentChangedCount.StackCount = 2;
            run.Drops[51] = currentChangedCount;
            var currentChangedGroup = changedGroup;
            currentChangedGroup.DropGroupId++;
            run.Drops[52] = currentChangedGroup;
            var currentChangedTemplate = changedTemplate;
            currentChangedTemplate.TemplateId++;
            run.Drops[53] = currentChangedTemplate;
            run.SceneSlotCounter = 53;

            service.RollbackRegistered(
                run,
                new[]
                {
                    exact,
                    changedCount,
                    changedGroup,
                    changedTemplate,
                });
            Check(
                "rollback removes only an exact slot/group/template/count match",
                !run.Drops.ContainsKey(50)
                    && run.Drops.ContainsKey(51)
                    && run.Drops.ContainsKey(52)
                    && run.Drops.ContainsKey(53),
                ref failures);
            Check(
                "rollback never rewinds the scene slot counter",
                run.SceneSlotCounter == 53,
                ref failures);
        }

        private static void VerifyClaimedOutcomeReplaysThroughKillRecovery(
            ref int failures)
        {
            failures += WithDatabase(
                "kill-recovery",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var observed = RunKillRecoveryScenario(
                        database,
                        FindGoldDropSeed());
                    Check(
                        "a claimed sequential outcome is replayed through the real kill recovery chain without rerolling or double gold",
                        observed.SendFailed
                            && observed.FirstDrops.Length > 0
                            && observed.FirstGold > 0
                            && observed.RecoveredDie != null
                            && PacketMatchesDrops(
                                observed.RecoveredDie,
                                observed.FirstDrops)
                            && observed.FinalSceneSlot == observed.FirstSceneSlot
                            && observed.FinalGold == observed.FirstGold
                            && observed.FinalRandomSeed == observed.FirstRandomSeed
                            && observed.DailyCounter == 1,
                        ref localFailures);

                    return localFailures;
                });
        }

        private static void VerifyEmptyOutcomeReplaysThroughKillRecovery(
            ref int failures)
        {
            failures += WithDatabase(
                "kill-recovery-empty",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var observed = RunKillRecoveryScenario(
                        database,
                        FindEmptyDropSeed());
                    Check(
                        "an empty sequential outcome is replayed through the real kill recovery chain without rerolling",
                        observed.SendFailed
                            && observed.FirstDrops.Length == 0
                            && observed.FirstGold == 0
                            && observed.FirstSceneSlot == 0
                            && observed.RecoveredDie != null
                            && PacketMatchesDrops(
                                observed.RecoveredDie,
                                Array.Empty<DropInfo>())
                            && observed.FinalSceneSlot == 0
                            && observed.FinalGold == 0
                            && observed.FinalRandomSeed == observed.FirstRandomSeed
                            && observed.DailyCounter == 0,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyClaimedOutcomeJournalCheckpoints(
            ref int failures)
        {
            var instance = new DungeonInstance(243, 2);
            var run = new DungeonRun(
                instance,
                runId: 7902,
                runGeneration: 1,
                DungeonRunState.Active);
            var journal = instance.ParticipantEffects;
            var sourceEventId = Guid.NewGuid();
            var identity = new DungeonMonsterDropIdentity(70, MonsterA);
            var roomIdentity = new DungeonRoomIdentity(instance.Identity, 1);
            var source = new DungeonEventEnvelope(
                sourceEventId,
                run.CaptureIdentity(),
                roomIdentity.RoomInstanceId,
                CharacterId,
                CharacterId,
                identity.ActorSequenceId,
                identity.MonsterCode,
                "sequential-loot-selftest",
                1);
            var participant = new DungeonParticipantRosterEntry(
                CharacterId,
                4201,
                run,
                run.CaptureIdentity(),
                roomIdentity,
                attachmentGeneration: 1);
            journal.TryFreeze(
                source,
                DungeonParticipantEffectAudience.Room,
                new[] { participant },
                out _);
            var began = journal.TryBegin(
                sourceEventId,
                DungeonParticipantEffectAudience.Room,
                participant,
                DungeonParticipantEffectKinds.MonsterKill,
                out var reservation,
                out _);
            var emptySourceEventId = Guid.NewGuid();
            var emptyIdentity = new DungeonMonsterDropIdentity(69, MonsterA);
            var emptySource = new DungeonEventEnvelope(
                emptySourceEventId,
                run.CaptureIdentity(),
                roomIdentity.RoomInstanceId,
                CharacterId,
                CharacterId,
                emptyIdentity.ActorSequenceId,
                emptyIdentity.MonsterCode,
                "sequential-loot-empty-selftest",
                1);
            journal.TryFreeze(
                emptySource,
                DungeonParticipantEffectAudience.Room,
                new[] { participant },
                out _);
            var emptyBegan = journal.TryBegin(
                emptySourceEventId,
                DungeonParticipantEffectAudience.Room,
                participant,
                DungeonParticipantEffectKinds.MonsterKill,
                out var emptyReservation,
                out _);
            var emptyFrozen = journal.TryFreezeMonsterDropOutcome(
                emptyReservation,
                emptyIdentity,
                EmptyResult(),
                out _);
            var emptyResolution = journal.ResolveMonsterDropOutcome(
                emptyReservation,
                emptyIdentity,
                out var emptyReplay);

            var claimed = new MonsterDropResult
            {
                GoldAmount = 321,
                Drops = new List<DropInfo>
                {
                    DropInfo.CreateItem(71, 90401, 1),
                },
            };
            var frozen = journal.TryFreezeMonsterDropOutcome(
                reservation,
                identity,
                claimed,
                out _);
            var mutatedClaimedDrop = claimed.Drops[0];
            mutatedClaimedDrop.TemplateId++;
            claimed.Drops[0] = mutatedClaimedDrop;
            var failed = journal.TryFail(reservation);
            var retried = journal.TryBegin(
                sourceEventId,
                DungeonParticipantEffectAudience.Room,
                participant,
                DungeonParticipantEffectKinds.MonsterKill,
                out var retryReservation,
                out _);
            var replayed = journal.ResolveMonsterDropOutcome(
                retryReservation,
                identity,
                out var replayedResult);
            var mismatched = journal.ResolveMonsterDropOutcome(
                retryReservation,
                new DungeonMonsterDropIdentity(70, MonsterB),
                out _);

            var overflowFailed = false;
            var gold = int.MaxValue;
            try
            {
                journal.TryApplyMonsterDropGold(
                    retryReservation,
                    identity,
                    amount => gold = checked(gold + amount));
            }
            catch (OverflowException)
            {
                overflowFailed = true;
            }

            gold = 0;
            var applyCalls = 0;
            var retryApplied = journal.TryApplyMonsterDropGold(
                retryReservation,
                identity,
                amount =>
                {
                    applyCalls++;
                    gold = checked(gold + amount);
                });
            var duplicateAccepted = journal.TryApplyMonsterDropGold(
                retryReservation,
                identity,
                _ => applyCalls++);
            var committed = journal.TryCommit(retryReservation);

            Check(
                "empty outcomes are frozen as resolved without inventing a drop",
                began
                    && emptyBegan
                    && emptyFrozen
                    && emptyResolution
                        == DungeonMonsterDropOutcomeResolution.Resolved
                    && emptyReplay.Drops?.Count == 0
                    && emptyReplay.GoldAmount == 0,
                ref failures);
            Check(
                "a source event cannot reuse a claimed outcome for another monster identity",
                frozen
                    && failed
                    && retried
                    && replayed
                        == DungeonMonsterDropOutcomeResolution.Resolved
                    && replayedResult.Drops?.Count == 1
                    && replayedResult.Drops[0].TemplateId == 90401
                    && !ReferenceEquals(
                        replayedResult.Drops,
                        claimed.Drops)
                    && (replayedResult.Drops[0].Core == null
                        || !ReferenceEquals(
                            replayedResult.Drops[0].Core,
                            claimed.Drops[0].Core))
                    && mismatched
                        == DungeonMonsterDropOutcomeResolution.IdentityMismatch,
                ref failures);
            Check(
                "gold is marked only after checked accumulation succeeds and is never applied twice",
                overflowFailed
                    && retryApplied
                    && duplicateAccepted
                    && committed
                    && applyCalls == 1
                    && gold == 321,
                ref failures);
        }

        private static KillRecoveryObservation RunKillRecoveryScenario(
            IGameDatabase database,
            uint seed)
        {
            var sessions = new SessionDirectory();
            using (var runtime = new ServerRuntimeBuilder(database))
            {
                var core = runtime.GetOrCreateGameProtocolCoreDependencies();
                var inventory = runtime
                    .GetOrCreateGameProtocolInventoryDependencies(core);
                var world = runtime.GetOrCreateGameProtocolWorldDependencies(
                    sessions,
                    core);
                var handler = runtime
                    .GetOrCreateGameProtocolTownDungeonHandlers(
                        core,
                        inventory,
                        world)
                    .Dungeon;

                var definitionCatalog =
                    SequentialDungeonDefinitionCatalog.Parse(
                        $@"
[sequential dungeon]
99
[dungeon index check]
243
[/dungeon index check]
[monster index check]
{RecoveryMonster}
[/monster index check]
[show individual process]
[/show individual process]
[/sequential dungeon]",
                        _ => (byte)2);
                var instance = new DungeonInstance(
                    dungeonId: 243,
                    difficulty: 2,
                    rewardPolicy: DungeonRewardPolicy.Standard,
                    dropDefinition: new DungeonDropDefinition(
                        dungeonId: 243,
                        sharedDungeonId: -1,
                        impossibleClassification: -1,
                        sourcePath: "selftest/kill-recovery",
                        kind: DungeonDropDefinitionKind.ImpossibleSolo,
                        policy: DungeonDropPolicy.Impossible),
                    experienceDefinition:
                        DungeonExperienceDefinitionCatalog.Resolve(243),
                    sequentialCatalog: definitionCatalog);
                var run = new DungeonRun(
                    instance,
                    runId: 7901,
                    runGeneration: 1,
                    DungeonRunState.Active)
                {
                    Phase = DungeonRunPhase.InProgress,
                };
                AttachRoom(run, seed, RecoveryMonster);

                using (var disconnectedClient = new TcpClient())
                {
                    var disconnected = new EnhancedClientSession(
                        disconnectedClient,
                        new GamePacketHeader());
                    ConfigureSession(disconnected, run);
                    sessions.Register(CharacterId, disconnected);

                    var sendFailed = false;
                    try
                    {
                        handler.Handle_ENUM_CMDPACKET_DIE_MONSTER(
                                disconnected,
                                new GamePacketHeader(),
                                BitConverter.GetBytes((ushort)100))
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (InvalidOperationException)
                    {
                        sendFailed = true;
                    }

                    var firstDrops = run.Drops.Values
                        .OrderBy(drop => drop.SceneSlot)
                        .ToArray();
                    var firstGold = run.TotalGold;
                    var firstSceneSlot = run.SceneSlotCounter;
                    var firstRandomSeed = run.ParticipantDropLcg.Seed;

                    using (var capture = new LoopbackPacketCapture())
                    {
                        ConfigureSession(capture.Session, run);
                        sessions.Register(CharacterId, capture.Session);
                        handler.RecoverDungeonParticipantEffectsAsync(
                                capture.Session)
                            .GetAwaiter()
                            .GetResult();
                        var packets = capture.ReadPackets(minimumCount: 1);
                        var recoveredDie = packets.LastOrDefault(
                            packet => IsPacket(
                                packet,
                                command: 0x00,
                                type: (ushort)NotiPacketTypeA21.DIE_MONSTER));

                        return new KillRecoveryObservation
                        {
                            SendFailed = sendFailed,
                            FirstDrops = firstDrops,
                            FirstGold = firstGold,
                            FirstSceneSlot = firstSceneSlot,
                            FirstRandomSeed = firstRandomSeed,
                            RecoveredDie = recoveredDie,
                            FinalGold = run.TotalGold,
                            FinalSceneSlot = run.SceneSlotCounter,
                            FinalRandomSeed = run.ParticipantDropLcg.Seed,
                            DailyCounter = ReadCounterDirect(
                                database,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    99,
                                    RecoveryMonster)),
                        };
                    }
                }
            }
        }

        private sealed class KillRecoveryObservation
        {
            internal bool SendFailed;
            internal DropInfo[] FirstDrops;
            internal int FirstGold;
            internal ushort FirstSceneSlot;
            internal uint FirstRandomSeed;
            internal byte[] RecoveredDie;
            internal int FinalGold;
            internal ushort FinalSceneSlot;
            internal uint FinalRandomSeed;
            internal long DailyCounter;
        }

        private static void ConfigureSession(
            EnhancedClientSession session,
            DungeonRun run)
        {
            session.Account = new AccountRecord
            {
                AccountId = 57900,
                MId = "sequential-loot-recovery",
                PasswordHash = string.Empty,
            };
            session.Player.CharacterId = CharacterId;
            session.Player.UserId = 4201;
            session.Player.Level = 85;
            session.Player.Job = 0;
            session.Player.GrowType = 0;
            session.Player.CurrentRun = run;
        }

        private static void AttachRoom(
            DungeonRun run,
            uint seed,
            int primaryMonsterCode = MonsterA)
        {
            var roomKey = new RoomKey(0, 0, 0);
            var monsters = new List<DfoServer.GameWorld.Dungeon.MonsterSumInfo>
            {
                new DfoServer.GameWorld.Dungeon.MonsterSumInfo
                {
                    Code = primaryMonsterCode,
                    Level = 85,
                    Type = 0,
                    IsBlocking = true,
                },
                new DfoServer.GameWorld.Dungeon.MonsterSumInfo
                {
                    Code = MonsterB,
                    Level = 85,
                    Type = 0,
                    IsBlocking = true,
                },
            };
            var maze = new DfoServer.GameWorld.Dungeon.MazeSumInfo
            {
                Index = 1,
                X = 0,
                Y = 0,
                Monsters = monsters,
            };
            var room = run.Instance.GetOrCreateRoom(
                roomKey,
                roomId => new DungeonInstanceRoom(
                    roomId,
                    roomKey,
                    maze,
                    seed,
                    firstActorSequenceId: 100),
                out _);
            var participantDropLcg = new DnfLcg(seed);
            var roomState = new RoomState
            {
                InstanceRoom = room,
                Maze = maze,
                FirstSeqId = 100,
                MonsterCount = 2,
                KilledSeqIds = new HashSet<ushort>(),
                Seed = seed,
                Lcg = new DnfLcg(seed),
                ParticipantDropLcg = participantDropLcg,
            };
            roomState.TryActivate();
            run.RoomKey = roomKey;
            run.RoomStartSequence = 100;
            run.RoomMonsters = monsters;
            run.RoomLcg = roomState.Lcg;
            run.ParticipantDropLcg = participantDropLcg;
            run.RoomStates[roomKey] = roomState;
            run.SetCurrentRoom(room);
        }

        private static uint FindGoldDropSeed()
        {
            for (uint seed = 1; seed < 100000; seed++)
            {
                var slotCounter = (ushort)0;
                var result = new DropGenerator(new DnfLcg(seed))
                    .GenerateMonsterDrops(
                        monsterLevel: 85,
                        monsterType: 0,
                        monsterCode: RecoveryMonster,
                        difficulty: 2,
                        dungeonLevel: 85,
                        partyMemberCount: 1,
                        chronicleDropJobGroup: -1,
                        dropPolicy: DungeonDropPolicy.Impossible,
                        slotCounter: ref slotCounter);
                if (result.goldAmount > 0)
                    return seed;
            }

            throw new InvalidOperationException(
                "failed to find a deterministic gold-producing drop seed");
        }

        private static uint FindEmptyDropSeed()
        {
            for (uint seed = 1; seed < 100000; seed++)
            {
                var slotCounter = (ushort)0;
                var result = new DropGenerator(new DnfLcg(seed))
                    .GenerateMonsterDrops(
                        monsterLevel: 85,
                        monsterType: 0,
                        monsterCode: RecoveryMonster,
                        difficulty: 2,
                        dungeonLevel: 85,
                        partyMemberCount: 1,
                        chronicleDropJobGroup: -1,
                        dropPolicy: DungeonDropPolicy.Impossible,
                        slotCounter: ref slotCounter);
                if (result.goldAmount == 0 && result.drops.Count == 0)
                    return seed;
            }

            throw new InvalidOperationException(
                "failed to find a deterministic empty drop seed");
        }

        private static bool PacketMatchesDrops(
            byte[] packet,
            IReadOnlyList<DropInfo> expected)
        {
            if (packet == null
                || expected == null
                || packet.Length < 18
                || packet[17] != expected.Count)
            {
                return false;
            }

            for (var index = 0; index < expected.Count; index++)
            {
                var offset = 18 + index * 48;
                if (packet.Length < offset + 48
                    || BitConverter.ToUInt16(packet, offset)
                        != expected[index].SceneSlot
                    || BitConverter.ToUInt32(packet, offset + 2)
                        != expected[index].TemplateId)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsPacket(
            byte[] packet,
            byte command,
            ushort type) =>
            packet != null
            && packet.Length >= 15
            && packet[0] == command
            && BitConverter.ToUInt16(packet, 1) == type;

        private static SequentialDungeonDefinition ParseDefinition(
            int groupKey)
        {
            var catalog = SequentialDungeonDefinitionCatalog.Parse(
                $@"
[sequential dungeon]
{groupKey}
[dungeon index check]
243 244
[/dungeon index check]
[monster index check]
{MonsterA} {MonsterB}
[/monster index check]
[show individual process]
[/show individual process]
[/sequential dungeon]",
                _ => (byte)2);
            if (!catalog.TryGetByGroupKey(groupKey, out var definition))
            {
                throw new InvalidOperationException(
                    $"failed to parse sequential loot group {groupKey}");
            }
            return definition;
        }

        private static MonsterDropResult EmptyResult() =>
            new MonsterDropResult
            {
                Drops = new List<DropInfo>(),
            };

        private static MonsterDropResult ItemResult(
            ushort sceneSlot,
            int itemId) =>
            new MonsterDropResult
            {
                Drops = new List<DropInfo>
                {
                    DropInfo.CreateItem(sceneSlot, itemId, 1),
                },
            };

        private static int WithDatabase(
            string suffix,
            Func<IGameDatabase, DailyResetService, int> action)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo_sequential_loot_{suffix}_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                SeedCharacter(database, suffix);
                return action(database, new DailyResetService(database));
            }
            finally
            {
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static void SeedCharacter(IGameDatabase database, string suffix)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (57900, @mid, '');
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, 57900, @name, 0);";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@mid", "sequential-loot-" + suffix);
                command.Parameters.AddWithValue("@name", "loot-" + suffix);
                command.ExecuteNonQuery();
            }
        }

        private static void ForcePreviousGameDay(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE character_daily_reset
SET day_id = 0
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.ExecuteNonQuery();
            }
        }

        private static void InjectCounterFailure(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
CREATE TRIGGER fail_sequential_monster_counter
BEFORE INSERT ON character_daily_counters
WHEN NEW.counter_key LIKE 'sequential_monster_drop_v1:%'
BEGIN
    SELECT RAISE(ABORT, 'injected sequential monster counter failure');
END;";
                command.ExecuteNonQuery();
            }
        }

        private static void ReadResetState(
            IGameDatabase database,
            out int dayId,
            out int weekId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT day_id, week_id
FROM character_daily_reset
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        dayId = 0;
                        weekId = 0;
                        return;
                    }

                    dayId = reader.GetInt32(0);
                    weekId = reader.GetInt32(1);
                }
            }
        }

        private static long ReadCounterDirect(
            IGameDatabase database,
            string counterKey)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT value
FROM character_daily_counters
WHERE character_id = @cid AND counter_key = @key;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@key", counterKey);
                return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
            }
        }

        private sealed class LoopbackPacketCapture : IDisposable
        {
            private readonly TcpClient _reader;

            internal LoopbackPacketCapture()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    _reader = new TcpClient();
                    var connect = _reader.ConnectAsync(
                        IPAddress.Loopback,
                        port);
                    var writer = listener.AcceptTcpClient();
                    connect.GetAwaiter().GetResult();
                    _reader.ReceiveTimeout = 1000;
                    Session = new EnhancedClientSession(
                        writer,
                        new GamePacketHeader());
                }
                finally
                {
                    listener.Stop();
                }
            }

            internal EnhancedClientSession Session { get; }

            internal List<byte[]> ReadPackets(int minimumCount)
            {
                var packets = new List<byte[]>();
                var stream = _reader.GetStream();
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < deadline)
                {
                    var needsMinimum = packets.Count < minimumCount;
                    var wait = needsMinimum
                        ? deadline - DateTime.UtcNow
                        : TimeSpan.FromMilliseconds(75);
                    if (wait <= TimeSpan.Zero)
                        break;
                    var waitMicroseconds = (int)Math.Min(
                        int.MaxValue,
                        Math.Max(1, wait.TotalMilliseconds * 1000));
                    if (!_reader.Client.Poll(
                            waitMicroseconds,
                            SelectMode.SelectRead))
                    {
                        if (!needsMinimum)
                            break;
                        continue;
                    }
                    if (_reader.Client.Available <= 0)
                        break;

                    var header = ReadExact(stream, 15);
                    var length = BitConverter.ToInt32(header, 3);
                    if (length < 15)
                        throw new InvalidOperationException(
                            $"invalid captured packet length {length}");

                    var packet = new byte[length];
                    Buffer.BlockCopy(header, 0, packet, 0, header.Length);
                    var bodyLength = length - header.Length;
                    if (bodyLength > 0)
                    {
                        var body = ReadExact(stream, bodyLength);
                        Buffer.BlockCopy(
                            body,
                            0,
                            packet,
                            header.Length,
                            body.Length);
                    }
                    packets.Add(packet);
                }

                return packets;
            }

            public void Dispose()
            {
                Session?.Close();
                _reader?.Close();
            }

            private static byte[] ReadExact(Stream stream, int count)
            {
                var buffer = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var read = stream.Read(buffer, offset, count - offset);
                    if (read <= 0)
                        throw new EndOfStreamException();
                    offset += read;
                }
                return buffer;
            }
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
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
    }
}
