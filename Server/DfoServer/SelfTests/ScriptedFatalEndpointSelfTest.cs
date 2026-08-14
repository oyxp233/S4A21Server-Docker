using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    public static class ScriptedFatalEndpointSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== SCRIPTED_FATAL_ENDPOINT selftest ===");
            var failures = 0;

            TestScopedQuestConnectionParsing(ref failures);
            TestMonsterRelationParsing(ref failures);
            TestActRelationParsing(ref failures);
            TestRealPvfRelation(ref failures);
            TestRuntimeLifecycle(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void TestScopedQuestConnectionParsing(ref int failures)
        {
            const string content =
                "[quest connection]\n" +
                "0 321 -1\n" +
                "[maze info]\n" +
                "[size]\n" +
                "2 1\n" +
                "[quest connection]\n" +
                "0 654 -1\n";
            var dungeon = DungeonFile.Parse(content);

            Check(
                "DGN top-level quest connection remains top-level",
                dungeon.QuestConnection != null
                    && dungeon.QuestConnection.Length == 3
                    && dungeon.QuestConnection[1] == 321,
                ref failures);
            Check(
                "maze quest connection keeps its own scope",
                dungeon.Mazes.Count == 1
                    && dungeon.Mazes[0].QuestConnection != null
                    && dungeon.Mazes[0].QuestConnection[1] == 654,
                ref failures);
        }

        private static void TestMonsterRelationParsing(ref int failures)
        {
            const string content =
                "[category]\n" +
                "`[dragon]` `[fixture]`\n" +
                "[/category]\n" +
                "[waiting action]\n" +
                "`Action/Stay.act`\n" +
                "[etc action]\n" +
                "`Action/First.act`\n" +
                "[/etc action]\n" +
                "[etc action]\n" +
                "`Action/Second.act`\n" +
                "[/etc action]\n";
            var monster = MonsterFile.Parse(content);

            Check(
                "MOB parser preserves all category values",
                monster.Categories.Count == 2
                    && monster.Categories[0] == "[dragon]"
                    && monster.Categories[1] == "[fixture]",
                ref failures);
            Check(
                "MOB parser preserves ordered ETC action list",
                monster.EtcActions.Count == 2
                    && monster.EtcActions[0] == "Action/First.act"
                    && monster.EtcActions[1] == "Action/Second.act",
                ref failures);
        }

        private static void TestActRelationParsing(ref int failures)
        {
            const string waitingContent =
                "[TRIGGER]\n" +
                "[WHICH]\n[PASSIVE]\n" +
                "[CHECKUP]\n[IS INDEX]\n700\n[/IS INDEX]\n[/CHECKUP]\n" +
                "[CHECKED NO]\n[<=]\n0\n" +
                "[DO BEHAVIOR]\n[ME]\n0\n[/TRIGGER]\n" +
                "[BEHAVIOR]\n[SET ACTION]\n[CUSTOM]\n1\n[NOW]\n[/BEHAVIOR]\n";
            var waiting = ActFile.Parse(waitingContent);
            var transitionParsed =
                ScriptedFatalEndpointData.TryResolveCustomTransition(
                    waiting,
                    out var passiveObjectCode,
                    out var customActionIndex,
                    out _);
            Check(
                "ACT parser resolves passive disappearance to CUSTOM index",
                transitionParsed
                    && passiveObjectCode == 700
                    && customActionIndex == 1,
                ref failures);

            const string fatalContent =
                "[TRIGGER]\n" +
                "[WHICH]\n[ALL ENEMY]\n" +
                "[CHECKUP]\n[IS OBJECT TYPE]\n[CHARACTER]\n[MONSTER]\n[/IS OBJECT TYPE]\n[/CHECKUP]\n" +
                "[DO BEHAVIOR]\n[CHECKUP OBJECT]\n0\n[/TRIGGER]\n" +
                "[BEHAVIOR]\n[RESTORE]\n[HP]\n-100\n[%]\n[/BEHAVIOR]\n";
            Check(
                "ACT parser recognizes all-enemy fatal character behavior",
                ScriptedFatalEndpointData.HasFatalAllEnemyCharacterBehavior(
                    ActFile.Parse(fatalContent)),
                ref failures);

            Check(
                "sub-fatal HP restore does not enable scripted endpoint",
                !ScriptedFatalEndpointData.HasFatalAllEnemyCharacterBehavior(
                    ActFile.Parse(fatalContent.Replace("-100", "-99"))),
                ref failures);
        }

        private static void TestRealPvfRelation(ref int failures)
        {
            const int dungeonId = 3540;
            const int questId = 20727;
            const int endpointMapId = 25291;
            const int fixtureMonsterCode = 100010;
            const int triggerPassiveObjectCode = 54086;

            var maze = Dungeon.GetDungeonMaze(dungeonId, 0);
            var resolved = ScriptedFatalEndpointData.TryResolve(
                dungeonId,
                mazeIndex: 0,
                maze,
                bossPosition: new[] { 1, 0 },
                selectedBossMapId: -1,
                difficulty: 0,
                activeQuestIds: new HashSet<int> { questId },
                out var definition,
                out var reason);
            Check(
                "real PVF closes quest -> endpoint -> fixture -> passive -> fatal ACT chain",
                resolved
                    && definition != null
                    && definition.QuestId == questId
                    && definition.MapId == endpointMapId
                    && definition.Actors.Count == 1
                    && definition.Actors[0].MonsterCode == fixtureMonsterCode
                    && definition.Actors[0].TriggerPassiveObjectCode ==
                        triggerPassiveObjectCode,
                ref failures,
                reason);

            Check(
                "real top-level quest connection marks selection only for active quest",
                Dungeon.IsQuestConnectedSelection(
                    dungeonId,
                    maze,
                    new HashSet<int> { questId },
                    difficulty: 0)
                    && !Dungeon.IsQuestConnectedSelection(
                        dungeonId,
                        maze,
                        new HashSet<int> { questId + 1 },
                        difficulty: 0),
                ref failures);

            Check(
                "unrelated active quest does not enable scripted endpoint",
                !ScriptedFatalEndpointData.TryResolve(
                    dungeonId,
                    mazeIndex: 0,
                    maze,
                    bossPosition: new[] { 1, 0 },
                    selectedBossMapId: -1,
                    difficulty: 0,
                    activeQuestIds: new HashSet<int> { questId + 1 },
                    out _,
                    out _),
                ref failures);
        }

        private static void TestRuntimeLifecycle(ref int failures)
        {
            const int fixtureMonsterCode = 42;
            const int triggerPassiveObjectCode = 77;
            var definition = new ScriptedFatalEndpointDefinition
            {
                QuestId = 1,
                MazeIndex = 0,
                EndpointX = 1,
                EndpointY = 2,
                MapId = 300,
                Actors = new[]
                {
                    new ScriptedFatalEndpointActorDefinition
                    {
                        MonsterCode = fixtureMonsterCode,
                        TriggerPassiveObjectCode = triggerPassiveObjectCode,
                    },
                },
            };

            using (var tcp = new TcpClient())
            {
                var session = new EnhancedClientSession(
                    tcp,
                    new GamePacketHeader());
                var run = new DungeonRun(100, 0)
                {
                    RoomKey = new RoomKey(1, 2, 0),
                    ScriptedFatalEndpoint =
                        new ScriptedFatalEndpointRuntime(definition),
                };
                run.RoomStates[run.RoomKey] = new RoomState
                {
                    Maze = new Dungeon.MazeSumInfo
                    {
                        X = 1,
                        Y = 2,
                        Index = 300,
                    },
                    KilledSeqIds = new HashSet<ushort>(),
                };
                session.Player.CurrentRun = run;

                var ordinaryDeath =
                    DungeonMechanismCoordinator.OnCharacterDied(session);
                Check(
                    "ordinary death before script trigger keeps normal respawn",
                    !ordinaryDeath.SuppressRespawn
                        && !ordinaryDeath.ClearRequest.ShouldClearDungeon,
                    ref failures);

                DungeonMechanismCoordinator.OnPassiveObjectDestroyed(
                    session,
                    triggerPassiveObjectCode + 1);
                Check(
                    "unrelated passive object does not arm endpoint",
                    !run.ScriptedFatalEndpoint.Armed,
                    ref failures);

                DungeonMechanismCoordinator.OnPassiveObjectDestroyed(
                    session,
                    triggerPassiveObjectCode);
                var firstDeath =
                    DungeonMechanismCoordinator.OnCharacterDied(session);
                var duplicateDeath =
                    DungeonMechanismCoordinator.OnCharacterDied(session);
                Check(
                    "script death suppresses respawn and requests one clear",
                    firstDeath.SuppressRespawn
                        && firstDeath.ClearRequest.ShouldClearDungeon
                        && duplicateDeath.SuppressRespawn
                        && !duplicateDeath.ClearRequest.ShouldClearDungeon,
                    ref failures);

                var clone = run.ScriptedFatalEndpoint.CloneFresh();
                Check(
                    "cloned run shares read-only definition but not runtime state",
                    clone != null
                        && ReferenceEquals(clone.Definition, definition)
                        && !clone.Armed
                        && !clone.ClearIssued,
                    ref failures);

                var staleRun = new DungeonRun(101, 0)
                {
                    RoomKey = new RoomKey(1, 2, 0),
                    ScriptedFatalEndpoint = clone,
                };
                staleRun.RoomStates[staleRun.RoomKey] = new RoomState
                {
                    Maze = new Dungeon.MazeSumInfo
                    {
                        X = 1,
                        Y = 2,
                        Index = 300,
                    },
                    KilledSeqIds = new HashSet<ushort>(),
                };
                session.Player.CurrentRun = staleRun;
                DungeonMechanismCoordinator.OnPassiveObjectDestroyed(
                    session,
                    triggerPassiveObjectCode);
                session.Player.CurrentRun = new DungeonRun(102, 0);
                var staleDeath =
                    DungeonMechanismCoordinator.OnCharacterDied(
                        session,
                        staleRun);
                Check(
                    "stale scripted death cannot consume or clear a replacement run",
                    !staleDeath.SuppressRespawn
                        && !staleDeath.ClearRequest.ShouldClearDungeon
                        && staleRun.ScriptedFatalEndpoint.Armed
                        && !staleRun.ScriptedFatalEndpoint.ClearIssued,
                    ref failures);
            }
        }

        private static void Check(
            string name,
            bool passed,
            ref int failures,
            string detail = null)
        {
            if (passed)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }

            failures++;
            Console.WriteLine(
                $"[FAIL] {name}" +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}"));
        }
    }
}
