using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Network.Handlers.Dungeon;
using PvfLib;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.SelfTests
{
    public static class DungeonMapFallbackSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_MAP_FALLBACK selftest ===");
            var failures = 0;

            var mapSpecs = new List<MapSpecificationItem>
            {
                new MapSpecificationItem { Type = "map", X = 0, Y = 0, Index = 13417 },
            };
            var mapEntries = new List<LstEntry>
            {
                new LstEntry { Id = 13417, FilePath = "eternal_dream/01.map" },
                new LstEntry { Id = 14999, FilePath = "eternal_dream/q_7_0.map" },
            };
            var mapDirs = new List<string> { "eternal_dream" };

            var mapId = DfoServer.GameWorld.DungeonMapResolver.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 1004,
                mazeIndex: 0,
                x: 7,
                y: 0,
                mapSpecifications: mapSpecs,
                mapEntries: mapEntries,
                mapDirCandidates: mapDirs,
                preferQuestVariant: true,
                reason: out var reason);

            Check("quest start room prefers coordinate quest variant over first ordinary map spec",
                mapId == 14999 && reason.StartsWith("quest-variant", StringComparison.Ordinal),
                ref failures);

            try
            {
                var flatSpecialPassiveMap = MapFile.Parse(
                    "[special passive object]\n" +
                    "10001 10 20 0 " +
                    "10002 30 40 1\n");
                Check("special passive object parser keeps legacy flat rows",
                    flatSpecialPassiveMap.SpecialPassiveObjects.Count == 2
                    && flatSpecialPassiveMap.SpecialPassiveObjects[0].ObjectCode == 10001
                    && flatSpecialPassiveMap.SpecialPassiveObjects[1].ObjectCode == 10002
                    && flatSpecialPassiveMap.SpecialPassiveObjects[0].Spawns.Count == 0,
                    ref failures);

                var extendedSpecialPassiveMap = MapFile.Parse(
                    "[special passive object]\n" +
                    "14056 100 200 0 2 " +
                    "`[monster]` 61801 62 0 0 0 " +
                    "`[monster]` 59013 62 0 1 0\n");
                Check("special passive object parser reads inline spawn rows",
                    extendedSpecialPassiveMap.SpecialPassiveObjects.Count == 1
                    && extendedSpecialPassiveMap.SpecialPassiveObjects[0].Spawns.Count == 2
                    && extendedSpecialPassiveMap.SpecialPassiveObjects[0].Spawns[0].Code == 61801
                    && extendedSpecialPassiveMap.SpecialPassiveObjects[0].Spawns[1].Code == 59013,
                    ref failures);

                var projectedSpecialPassiveActors =
                    DfoServer.GameWorld.DungeonActorTemplateProjector.Project(
                        extendedSpecialPassiveMap,
                        dungeonBasicLevel: 62,
                        mapId: 1);
                Check("special passive parent remains MAP-owned while inline templates are projected",
                    !ContainsActorType(projectedSpecialPassiveActors, 9)
                    && CountActor(projectedSpecialPassiveActors, 61801) == 1
                    && CountActor(projectedSpecialPassiveActors, 59013) == 1
                    && projectedSpecialPassiveActors.TrueForAll(actor => actor.Flag0 == 1),
                    ref failures);

                var monsterTeamMap = MapFile.Parse(
                    "[monster]\n" +
                    "57022 1 0 100 200 0 1 1 `[fixed]` `[normal]` " +
                    "57054 1 0 300 200 0 1 1 `[fixed]` `[normal]`\n" +
                    "[monster team]\n" +
                    "100 0\n");
                var projectedMonsterTeams =
                    DfoServer.GameWorld.DungeonActorTemplateProjector.Project(
                        monsterTeamMap,
                        dungeonBasicLevel: 70,
                        mapId: 39118);
                Check("MAP monster team projects only team 100 as room-blocking",
                    projectedMonsterTeams.Count == 2
                    && projectedMonsterTeams[0].IsBlocking
                    && !projectedMonsterTeams[1].IsBlocking,
                    ref failures);

                var eventPositionMap = MapFile.Parse(
                    "[event monster position]\n" +
                    "10 20 0 30 40 1\n");
                Check("event monster position parser preserves xyz triplets",
                    eventPositionMap.EventMonsterPositionCount == 2
                    && eventPositionMap.EventMonsterPositions.Count == 2
                    && eventPositionMap.EventMonsterPositions[0].X == 10
                    && eventPositionMap.EventMonsterPositions[0].Y == 20
                    && eventPositionMap.EventMonsterPositions[0].Z == 0
                    && eventPositionMap.EventMonsterPositions[1].X == 30
                    && eventPositionMap.EventMonsterPositions[1].Y == 40
                    && eventPositionMap.EventMonsterPositions[1].Z == 1,
                    ref failures);

                var npcBossMap = MapFile.Parse(
                    "[monster]\n" +
                    "63024 1 0 735 284 0 1 1 `[fixed]` `[NPC]` 1020 `[boss]` " +
                    "63030 1 0 546 231 0 1 1 `[fixed]` `[normal]`\n");
                Check("monster parser keeps variable NPC actor rows aligned",
                    npcBossMap.MonsterCount == 2
                    && npcBossMap.Monsters.Count == 2
                    && npcBossMap.Monsters[0].MonsterId == 63024
                    && npcBossMap.Monsters[0].NpcId == 1020
                    && npcBossMap.Monsters[0].Type == MonsterType.Boss
                    && npcBossMap.Monsters[1].MonsterId == 63030
                    && npcBossMap.Monsters[1].NpcId == null
                    && npcBossMap.Monsters[1].Type == MonsterType.Normal,
                    ref failures);

                var conflagrationNpcBossMap = MapFile.Parse(
                    DfoServer.GameWorld.PvfArchiveAccessor.ReadText(Path.Combine(
                        "map",
                        "conflagration",
                        "maze_15394B.map")));
                Check("Conflagration quest Boss map keeps NPC-bound Boss and following actors",
                    conflagrationNpcBossMap.MonsterCount == 4
                    && conflagrationNpcBossMap.Monsters.Count == 4
                    && conflagrationNpcBossMap.Monsters[0].MonsterId == 63024
                    && conflagrationNpcBossMap.Monsters[0].NpcId == 1020
                    && conflagrationNpcBossMap.Monsters[0].Type == MonsterType.Boss
                    && conflagrationNpcBossMap.Monsters[1].MonsterId == 63030
                    && conflagrationNpcBossMap.Monsters[2].MonsterId == 63030
                    && conflagrationNpcBossMap.Monsters[3].MonsterId == 63030
                    && conflagrationNpcBossMap.Monsters[1].Type == MonsterType.Normal
                    && conflagrationNpcBossMap.Monsters[2].Type == MonsterType.Normal
                    && conflagrationNpcBossMap.Monsters[3].Type == MonsterType.Normal,
                    ref failures);

                var multilineGreedDungeon = DungeonFile.Parse(
                    "[maze info]\n" +
                    "[size]\n2 2\n" +
                    "[greed]\n`II00\n AACC`\n");
                var multilineGreedMaze = multilineGreedDungeon.Mazes[0];
                var greedCells = new HashSet<DungeonMazeRoomCoordinate>(
                    DungeonMazeTopology.ResolveGreedCoordinates(
                        multilineGreedMaze));
                Check("maze parser and topology preserve multiline two-character greed cells",
                    multilineGreedMaze.Greed == "II00\nAACC"
                    && greedCells.Contains(new DungeonMazeRoomCoordinate(0, 0))
                    && !greedCells.Contains(new DungeonMazeRoomCoordinate(1, 0))
                    && !greedCells.Contains(new DungeonMazeRoomCoordinate(0, 1))
                    && greedCells.Contains(new DungeonMazeRoomCoordinate(1, 1)),
                    ref failures);

                var linearGreedDungeon = DungeonFile.Parse(
                    "[maze info]\n" +
                    "[size]\n1 3\n" +
                    "[greed]\n`II\nCC\nEE`\n");
                Check("ordinary two-character linear maze keeps every configured room",
                    DungeonRoomTopology.CountConfiguredRooms(
                        linearGreedDungeon.Mazes[0]) == 3,
                    ref failures);

                var gblGoddessTempleMaze = DungeonData.GetDungeonMaze(163, 0);
                Check("GBL Goddess Temple excludes AA cells from clear reward room count",
                    gblGoddessTempleMaze != null
                    && gblGoddessTempleMaze.Width == 6
                    && gblGoddessTempleMaze.Height == 7
                    && DungeonRoomTopology.CountConfiguredRooms(
                        gblGoddessTempleMaze) == 23,
                    ref failures);

                var eventMazeDungeon = DungeonFile.Parse(
                    "[maze info]\n" +
                    "[size]\n5 4\n" +
                    "[minimap icon]\n" +
                    "4 1 `Interface/minimap.img` 37 1 " +
                    "2 3 `Interface/minimap.img` 38 2\n" +
                    "[event monster random map]\n0\n" +
                    "[maze info]\n" +
                    "[size]\n5 4\n" +
                    "[minimap icon]\n" +
                    "3 0 `Interface/minimap.img` 39 3\n" +
                    "[event monster random map]\n2\n" +
                    "[named monster]\n58502\n");
                Check("maze parser keeps minimap icons and event-map value per maze",
                    eventMazeDungeon.Mazes.Count == 2
                    && eventMazeDungeon.Mazes[0].MinimapIcons.Count == 2
                    && eventMazeDungeon.Mazes[0].MinimapIcons[0].X == 4
                    && eventMazeDungeon.Mazes[0].MinimapIcons[0].Y == 1
                    && eventMazeDungeon.Mazes[0].MinimapIcons[1].IconIndex == 38
                    && eventMazeDungeon.Mazes[0].MinimapIcons[1].Flag == 2
                    && eventMazeDungeon.Mazes[0].EventMonsterRandomMap == 0
                    && eventMazeDungeon.Mazes[1].MinimapIcons.Count == 1
                    && eventMazeDungeon.Mazes[1].MinimapIcons[0].X == 3
                    && eventMazeDungeon.Mazes[1].MinimapIcons[0].Y == 0
                    && eventMazeDungeon.Mazes[1].EventMonsterRandomMap == 2
                    && eventMazeDungeon.NamedMonster?.Length == 1
                    && eventMazeDungeon.NamedMonster[0] == 58502,
                    ref failures);

                var globalEventDungeon = DungeonFile.Parse(
                    "[maze info]\n" +
                    "[size]\n2 1\n" +
                    "[start map]\n0 0\n" +
                    "[boss map]\n1 0\n" +
                    "[event monster random map]\n0\n" +
                    "[event monster]\n1 1000 21100 1 1000 1 1 5\n");
                Check("global event-map metadata does not leak into the last maze",
                    globalEventDungeon.Mazes.Count == 1
                    && globalEventDungeon.Mazes[0].EventMonsterRandomMap == -1
                    && globalEventDungeon.EventMonsterRandomMap == "0"
                    && !string.IsNullOrWhiteSpace(globalEventDungeon.EventMonster),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] special passive object parser compatibility: {ex.Message}");
                failures++;
            }

            var passiveObjectMaze = new DungeonData.MazeSumInfo
            {
                Monsters = new List<DungeonData.MonsterSumInfo>
                {
                    new DungeonData.MonsterSumInfo { Code = 100, Type = 0, Level = 1, IsBlocking = true },
                    new DungeonData.MonsterSumInfo { Code = 14056, Type = 9, Level = 1, IsBlocking = false },
                },
            };
            Check("passive start-map objects do not count as tracked monsters",
                DungeonMapHandler.CountServerTrackedMonsters(passiveObjectMaze) == 1,
                ref failures);

            TestEventMonsterCandidateRooms(ref failures);
            TestDriftCaveMazeSelection(ref failures);
            TestExplicitMazeStartSpecification(ref failures);
            TestGentOuterAndVilmarkTaskMazeSelection(ref failures);
            TestTimeGateStartMapOwnership(ref failures);
            TestTimeGateQuestMazeSelection(ref failures);
            TestPersonalSkillQuestMazeSelection(ref failures);
            TestAntwerpAndTrainMazeSelection(ref failures);
            TestResolvedRoomTemplateIsolation(ref failures);
            TestNamedMonsterRoomFilter(ref failures);

            var compactQuestMapEntries = new List<LstEntry>
            {
                new LstEntry { Id = 13417, FilePath = "eternal_dream/01.map" },
                new LstEntry { Id = 15000, FilePath = "eternal_dream/q7_0.map" },
            };
            var compactQuestMapId = DfoServer.GameWorld.DungeonMapResolver.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 1004,
                mazeIndex: 0,
                x: 7,
                y: 0,
                mapSpecifications: mapSpecs,
                mapEntries: compactQuestMapEntries,
                mapDirCandidates: mapDirs,
                preferQuestVariant: true,
                reason: out reason);

            Check("quest variant detection accepts q-prefixed coordinate map names",
                compactQuestMapId == 15000 && reason.StartsWith("quest-variant", StringComparison.Ordinal),
                ref failures);

            var ordinaryMapId = DfoServer.GameWorld.DungeonMapResolver.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 1004,
                mazeIndex: 0,
                x: 7,
                y: 0,
                mapSpecifications: mapSpecs,
                mapEntries: mapEntries,
                mapDirCandidates: mapDirs,
                preferQuestVariant: false,
                reason: out reason);

            Check("ordinary fallback keeps first map spec when quest variant is not preferred",
                ordinaryMapId == 13417 && reason == "first map spec",
                ref failures);

            var rottenMapSpecs = new List<MapSpecificationItem>
            {
                new MapSpecificationItem { Type = "boss", X = 4, Y = 0, Index = 18914 },
            };
            var rottenMapEntries = new List<LstEntry>
            {
                new LstEntry { Id = 18914, FilePath = "158_DecayArea/18914(4,0)B.map" },
                new LstEntry { Id = 36041, FilePath = "158_DecayArea/maze(2,2).map" },
                new LstEntry { Id = 18911, FilePath = "158_DecayArea/18911(0,5)N.map" },
            };
            var rottenFallbackMapId = DfoServer.GameWorld.DungeonMapResolver.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 158,
                mazeIndex: 0,
                x: 1,
                y: 5,
                mapSpecifications: rottenMapSpecs,
                mapEntries: rottenMapEntries,
                mapDirCandidates: new List<string> { "158_DecayArea" },
                preferQuestVariant: false,
                reason: out reason);

            Check("unresolved rotten land room uses nearby normal coordinate before distant maze template",
                rottenFallbackMapId == 18911 && reason.StartsWith("nearest coordinate map", StringComparison.Ordinal),
                ref failures);

            var lowercaseBossEntries = new List<LstEntry>
            {
                new LstEntry { Id = 77001, FilePath = "generic/77001(2,4)b.map" },
                new LstEntry { Id = 77002, FilePath = "generic/77002(2,6)N.map" },
            };
            var lowercaseBossFallbackMapId = DfoServer.GameWorld.DungeonMapResolver.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 9000,
                mazeIndex: 0,
                x: 2,
                y: 5,
                mapSpecifications: null,
                mapEntries: lowercaseBossEntries,
                mapDirCandidates: new List<string> { "generic" },
                preferQuestVariant: false,
                reason: out reason);

            Check("coordinate fallback ignores lowercase boss suffix",
                lowercaseBossFallbackMapId == 77002 && reason.StartsWith("nearest coordinate map", StringComparison.Ordinal),
                ref failures);


            CheckSuitableLevelEligibility(ref failures);

            var towerClassificationIsSafe = false;
            try
            {
                towerClassificationIsSafe = !DungeonData.TryGetTowerOfDespairFloor(
                    int.MaxValue,
                    out _);
            }
            catch
            {
                towerClassificationIsSafe = false;
            }
            Check("tower classification treats an unknown dungeon as non-tower without throwing",
                towerClassificationIsSafe,
                ref failures);

            try
            {
                var despairTowerFirstFloor = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11008,
                    x: 0xFF,
                    y: 0xFF,
                    mazeIndex: 0);
                Check("tower of despair first floor resolves its PVF map and boss APC",
                    despairTowerFirstFloor.Index == 15130
                    && ContainsMonster(despairTowerFirstFloor, 20426),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] tower of despair first floor resolves its PVF map and boss APC: {ex.Message}");
                failures++;
            }

            CheckTowerMirrorApcInfo(ref failures);

            try
            {
                var floor15Start = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11022,
                    x: 0,
                    y: 0,
                    mazeIndex: 0);
                var floor15Middle = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11022,
                    x: 1,
                    y: 0,
                    mazeIndex: 0);
                var floor15Boss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11022,
                    x: 2,
                    y: 0,
                    mazeIndex: 0);
                Check("tower of despair floor 15 resolves all PVF room maps",
                    floor15Start.Index == 15144
                    && floor15Middle.Index == 15180
                    && floor15Boss.Index == 15181,
                    ref failures);

                var floor25Boss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11032,
                    x: 0,
                    y: 0,
                    mazeIndex: 0);
                var floor25Middle1 = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11032,
                    x: 1,
                    y: 0,
                    mazeIndex: 0);
                var floor25Middle2 = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11032,
                    x: 2,
                    y: 0,
                    mazeIndex: 0);
                var floor25Start = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11032,
                    x: 3,
                    y: 0,
                    mazeIndex: 0);
                Check("tower of despair floor 25 resolves all PVF room maps",
                    floor25Boss.Index == 15154
                    && floor25Middle1.Index == 15250
                    && floor25Middle2.Index == 15251
                    && floor25Start.Index == 15252,
                    ref failures);

                var floor24 = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11031,
                    x: 0,
                    y: 0,
                    mazeIndex: 0);
                Check("generic map parsing keeps tower APCs non-blocking",
                    CountApcs(floor24) == 3
                    && CountBlockingApcs(floor24) == 0,
                    ref failures);

                var floor24Run = new DungeonRun(11031, 0)
                {
                    RoomStartSequence = 1,
                    RoomMonsters = floor24.Monsters,
                };
                floor24Run.RoomKilledSeqIds.Add(1);
                bool floor24ClearedAfterOne;
                int floor24BlockingCount;
                int floor24KilledBlockingCount;
                lock (floor24Run.SyncRoot)
                {
                    floor24ClearedAfterOne = DungeonRoomTopology.ComputeRoomClearedLocked(
                        floor24Run,
                        out floor24BlockingCount,
                        out floor24KilledBlockingCount);
                }
                Check("tower of despair floor 24 does not clear after one of three hostile APCs dies",
                    floor24BlockingCount == 3
                    && floor24KilledBlockingCount == 1
                    && !floor24ClearedAfterOne,
                    ref failures);

                for (ushort seq = 2; seq <= 3; seq++)
                    floor24Run.RoomKilledSeqIds.Add(seq);
                bool floor24ClearedAfterAll;
                lock (floor24Run.SyncRoot)
                {
                    floor24ClearedAfterAll = DungeonRoomTopology.ComputeRoomClearedLocked(
                        floor24Run,
                        out floor24BlockingCount,
                        out floor24KilledBlockingCount);
                }
                Check("tower of despair floor 24 clears after all three hostile APCs die",
                    floor24BlockingCount == 3
                    && floor24KilledBlockingCount == 3
                    && floor24ClearedAfterAll,
                    ref failures);

                Check("generic map parsing does not embed floor 25 clear policy",
                    CountApcs(floor25Boss) > 0
                    && CountApcs(floor25Middle1) > 0
                    && CountApcs(floor25Middle2) > 0
                    && CountApcs(floor25Start) > 0
                    && CountBlockingApcs(floor25Boss) == 0
                    && CountBlockingApcs(floor25Middle1) == 0
                    && CountBlockingApcs(floor25Middle2) == 0
                    && CountBlockingApcs(floor25Start) == 0,
                    ref failures);

                var floor82 = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11089,
                    x: 0,
                    y: 0,
                    mazeIndex: 0);
                var floor82Run = new DungeonRun(11089, 0)
                {
                    RoomStartSequence = 1,
                    RoomMonsters = floor82.Monsters,
                };
                int floor82BlockingCount;
                lock (floor82Run.SyncRoot)
                {
                    DungeonRoomTopology.ComputeRoomClearedLocked(
                        floor82Run,
                        out floor82BlockingCount,
                        out _);
                }
                Check("tower clear policy excludes friendly APCs on floor 82",
                    CountFriendlyApcs(floor82) > 0
                    && floor82BlockingCount
                        == CountRawBlockingActors(floor82)
                           + CountHostileApcs(floor82),
                    ref failures);

                var floor55Start = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11062,
                    x: 0,
                    y: 0,
                    mazeIndex: 0);
                var floor55Boss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11062,
                    x: 1,
                    y: 0,
                    mazeIndex: 0);
                Check("tower of despair floor 55 resolves all PVF room maps",
                    floor55Start.Index == 15204
                    && floor55Boss.Index == 15182,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] tower of despair multi-room PVF map resolution: {ex.Message}");
                failures++;
            }

            try
            {
                var issue189StartMap = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 165,
                    x: 0xFF,
                    y: 0xFF,
                    mazeIndex: 4);
                Check("issue 189 quest maze start room uses map specification",
                    issue189StartMap.Index == 33060,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 189 quest maze start room uses map specification: {ex.Message}");
                failures++;
            }

            try
            {
                var upperBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 147,
                    x: 4,
                    y: 1,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 1 });
                var middleBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 147,
                    x: 4,
                    y: 2,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 2 });
                var lowerBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 147,
                    x: 4,
                    y: 3,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 3 });

                Check("issue 180 upper boss room uses boss actor map",
                    upperBoss.Index == 8179 && ContainsMonster(upperBoss, 65312),
                    ref failures);
                Check("issue 180 middle boss room skips duplicate non-boss map",
                    middleBoss.Index == 8180 && ContainsMonster(middleBoss, 65312),
                    ref failures);
                Check("issue 180 lower boss room uses boss actor map",
                    lowerBoss.Index == 8181 && ContainsMonster(lowerBoss, 65312),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 180 boss rooms use boss actor maps: {ex.Message}");
                failures++;
            }

            try
            {
                var iceCrystalUpperBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 145,
                    x: 2,
                    y: 0,
                    mazeIndex: 0,
                    bossPos: new[] { 2, 0 });
                var iceCrystalRightBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 145,
                    x: 3,
                    y: 1,
                    mazeIndex: 0,
                    bossPos: new[] { 3, 1 });

                Check("ice crystal forest upper boss coordinate skips ordinary map specification",
                    iceCrystalUpperBoss.Index == 14153
                    && ContainsMonster(iceCrystalUpperBoss, 65303),
                    ref failures);
                Check("ice crystal forest right boss coordinate skips ordinary map specification",
                    iceCrystalRightBoss.Index == 14157
                    && ContainsMonster(iceCrystalRightBoss, 65303),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] ice crystal forest boss map selection: {ex.Message}");
                failures++;
            }

            try
            {
                var issue227Boss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 0,
                    y: 5,
                    mazeIndex: 0,
                    bossPos: new[] { 0, 5 });
                var issue227Adjacent = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 1,
                    y: 5,
                    mazeIndex: 0,
                    bossPos: new[] { 0, 5 });

                Check("issue 227 selected boss room keeps boss actor map",
                    issue227Boss.Index == 18915 && ContainsMonster(issue227Boss, 65029),
                    ref failures);
                Check("issue 227 adjacent room uses declared PVF map without false boss",
                    (issue227Adjacent.Index == 18901
                        || issue227Adjacent.Index == 18905
                        || issue227Adjacent.Index == 18908)
                    && !ContainsMonster(issue227Adjacent, 65029),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 227 rotten land unresolved room map selection: {ex.Message}");
                failures++;
            }

            try
            {
                var issue361StartRoom = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 0,
                    y: 2,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 0 });
                var issue361SelectedUpperBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 4,
                    y: 0,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 0 });
                var issue361UnselectedLowerBossCoordinate = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 0,
                    y: 5,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 0 });
                var issue361LowerConnectorCoordinate = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 1,
                    y: 5,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 0 });

                Check("issue 361 start room still prefers start map variant",
                    issue361StartRoom.Index == 18916,
                    ref failures);
                Check("issue 361 selected upper boss room keeps boss actor map",
                    issue361SelectedUpperBoss.Index == 18914 && ContainsMonster(issue361SelectedUpperBoss, 65029),
                    ref failures);
                var repeatedUpperBossSelectionsKeepActor = true;
                for (var selection = 0; selection < 32; selection++)
                {
                    var repeatedUpperBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 158,
                        x: 4,
                        y: 0,
                        mazeIndex: 0,
                        bossPos: new[] { 4, 0 });
                    if (repeatedUpperBoss.Index != 18914
                        || !ContainsMonster(repeatedUpperBoss, 65029))
                    {
                        repeatedUpperBossSelectionsKeepActor = false;
                        break;
                    }
                }
                Check("issue 361 repeated upper boss selections keep boss actor map",
                    repeatedUpperBossSelectionsKeepActor,
                    ref failures);
                var issue361QuestMazeBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 4,
                    y: 0,
                    mazeIndex: 3,
                    bossPos: new[] { 4, 0 });
                Check("issue 361 quest maze keeps its explicit APC boss map",
                    issue361QuestMazeBoss.Index == 18919
                    && ContainsMonster(issue361QuestMazeBoss, 56408)
                    && !ContainsMonster(issue361QuestMazeBoss, 65029),
                    ref failures);
                Check("issue 361 unselected lower boss coordinate does not spawn false boss",
                    issue361UnselectedLowerBossCoordinate.Index != 18915
                    && !ContainsMonster(issue361UnselectedLowerBossCoordinate, 65029),
                    ref failures);
                Check("issue 361 lower connector uses declared PVF map specification",
                    (issue361LowerConnectorCoordinate.Index == 18901
                        || issue361LowerConnectorCoordinate.Index == 18905
                        || issue361LowerConnectorCoordinate.Index == 18908)
                    && !ContainsMonster(issue361LowerConnectorCoordinate, 65029),
                    ref failures);

                var issue361Maze = new MazeInfo
                {
                    Width = 5,
                    Height = 6,
                    StartMap = new[] { 0, 2 },
                    BossMap = new[] { 4, 0 },
                    MapSpecifications = new List<MapSpecificationItem>
                    {
                        new MapSpecificationItem { X = 0, Y = 2, Index = 18904 },
                        new MapSpecificationItem { X = 1, Y = 2, Index = 18905 },
                        new MapSpecificationItem { X = 2, Y = 2, Index = 18906 },
                        new MapSpecificationItem { X = 2, Y = 3, Index = 18907 },
                        new MapSpecificationItem { X = 2, Y = 4, Index = 18910 },
                        new MapSpecificationItem { X = 2, Y = 5, Index = 18913 },
                        new MapSpecificationItem { X = 0, Y = 5, Index = 18911 },
                        new MapSpecificationItem { X = 1, Y = 5, Index = 18901, MapCandidates = new[] { 18901, 18905, 18908 } },
                        new MapSpecificationItem { Type = "boss", X = 4, Y = 0, Index = 18914 },
                    },
                };
                var resolvedIssue361Move = DungeonRoomTopology.TryResolveMoveTarget(
                    dungeonId: 158,
                    mazeIndex: 0,
                    maze: issue361Maze,
                    currentRoom: new RoomKey(0, 5, -1),
                    requestedX: 1,
                    requestedY: 5,
                    bossMapPos: new[] { 4, 0 },
                    target: out var issue361MoveTarget,
                    reason: out var issue361MoveReason);
                Check("issue 361 sparse MOVE_MAP keeps declared lower connector room",
                    resolvedIssue361Move
                    && issue361MoveTarget.X == 1
                    && issue361MoveTarget.Y == 5
                    && issue361MoveReason == "known room",
                    ref failures);

                var issue361ShortMaze = new MazeInfo
                {
                    Width = 5,
                    Height = 3,
                    StartMap = new[] { 0, 2 },
                    BossMap = new[] { 4, 0 },
                    MapSpecifications = new List<MapSpecificationItem>
                    {
                        new MapSpecificationItem { X = 0, Y = 2, Index = 18904 },
                        new MapSpecificationItem { X = 1, Y = 2, Index = 18905 },
                        new MapSpecificationItem { X = 2, Y = 2, Index = 18906 },
                        new MapSpecificationItem { Type = "boss", X = 4, Y = 0, Index = 18914 },
                    },
                };
                var resolvedOutOfBoundsMove = DungeonRoomTopology.TryResolveMoveTarget(
                    dungeonId: 158,
                    mazeIndex: 1,
                    maze: issue361ShortMaze,
                    currentRoom: new RoomKey(0, 2, -1),
                    requestedX: 0,
                    requestedY: 3,
                    bossMapPos: new[] { 4, 0 },
                    target: out var outOfBoundsMoveTarget,
                    reason: out var outOfBoundsMoveReason);
                Check("issue 361 topology ignores same-directory PVF coordinates outside current maze bounds",
                    !resolvedOutOfBoundsMove
                    && outOfBoundsMoveTarget.X == 0
                    && outOfBoundsMoveTarget.Y == 0
                    && outOfBoundsMoveReason == "outside known dungeon room coordinates",
                    ref failures);

                var resolvedFarMove = DungeonRoomTopology.TryResolveMoveTarget(
                    dungeonId: 158,
                    mazeIndex: 0,
                    maze: issue361Maze,
                    currentRoom: new RoomKey(0, 5, -1),
                    requestedX: 4,
                    requestedY: 5,
                    bossMapPos: new[] { 4, 0 },
                    target: out var farMoveTarget,
                    reason: out var farMoveReason);
                Check("issue 361 topology does not normalize non-adjacent MOVE_MAP requests",
                    !resolvedFarMove
                    && farMoveTarget.X == 0
                    && farMoveTarget.Y == 0
                    && farMoveReason == "outside known dungeon room coordinates",
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 361 unselected boss coordinate map selection: {ex.Message}");
                failures++;
            }

            try
            {
                var iceCrystalUpperBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 145,
                    x: 2,
                    y: 0,
                    mazeIndex: 0,
                    bossPos: new[] { 2, 0 });
                var iceCrystalUnselectedLowerBossCoordinate =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 145,
                        x: 3,
                        y: 1,
                        mazeIndex: 0,
                        bossPos: new[] { 2, 0 });
                var iceCrystalLowerBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 145,
                    x: 3,
                    y: 1,
                    mazeIndex: 0,
                    bossPos: new[] { 3, 1 });
                var iceCrystalUnselectedUpperBossCoordinate =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 145,
                        x: 2,
                        y: 0,
                        mazeIndex: 0,
                        bossPos: new[] { 3, 1 });
                var iceCrystalQuestMazeBoss =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 145,
                        x: 2,
                        y: 1,
                        mazeIndex: 1,
                        bossPos: new[] { 2, 1 });

                Check("ice crystal selected upper boss coordinate uses boss variant",
                    iceCrystalUpperBoss.Index == 14153
                    && ContainsMonster(iceCrystalUpperBoss, 65303),
                    ref failures);
                Check("ice crystal unselected lower boss coordinate stays normal",
                    iceCrystalUnselectedLowerBossCoordinate.Index == 14163
                    && !ContainsMonster(iceCrystalUnselectedLowerBossCoordinate, 65303),
                    ref failures);
                Check("ice crystal selected lower boss coordinate uses boss variant",
                    iceCrystalLowerBoss.Index == 14157
                    && ContainsMonster(iceCrystalLowerBoss, 65303),
                    ref failures);
                Check("ice crystal unselected upper boss coordinate stays normal",
                    iceCrystalUnselectedUpperBossCoordinate.Index == 14162
                    && !ContainsMonster(iceCrystalUnselectedUpperBossCoordinate, 65303),
                    ref failures);
                Check("ice crystal quest maze keeps explicit quest boss map",
                    iceCrystalQuestMazeBoss.Index == 35027
                    && !ContainsMonster(iceCrystalQuestMazeBoss, 65303),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] ice crystal random boss map variant selection: {ex.Message}");
                failures++;
            }

            try
            {
                var issue167Boss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 89,
                    x: 0,
                    y: 1,
                    mazeIndex: 0,
                    bossPos: new[] { 0, 1 });
                var issue167QuestStart = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 89,
                    x: 5,
                    y: 1,
                    mazeIndex: 1);

                Check("issue 167 gent defense final room uses AI boss map",
                    issue167Boss.Index == 21314 && ContainsMonster(issue167Boss, 10409),
                    ref failures);
                Check("issue 167 scripted wave AI boss is the final blocking target",
                    ContainsBlockingMonster(issue167Boss, 10409),
                    ref failures);
                Check("issue 167 final room includes special passive wave monster templates",
                    CountMonster(issue167Boss, 61801) > 0 && CountMonster(issue167Boss, 61803) > 0,
                    ref failures);
                Check("issue 167 special passive wave templates do not block clear",
                    ContainsMonster(issue167Boss, 61801) && !ContainsBlockingMonster(issue167Boss, 61801),
                    ref failures);
                Check("issue 167 special passive wave templates preserve object grouping",
                    HasTemplate(issue167Boss, 61801, 0, 0, 0)
                    && HasTemplate(issue167Boss, 61801, 1, 0, 1)
                    && HasTemplate(issue167Boss, 61494, 2, 0, 2)
                    && HasTemplate(issue167Boss, 59013, 2, 1, 2),
                    ref failures);
                Check("issue 167 special passive parents remain MAP-owned",
                    !ContainsActorType(issue167Boss.Monsters, 9)
                    && IndexOfMonster(issue167Boss, 14056) < 0,
                    ref failures);
                Check("issue 167 hidden templates precede the final AI boss",
                    IndexOfFirstHiddenTemplate(issue167Boss) >= 0
                    && IndexOfMonster(issue167Boss, 10409) > IndexOfLastHiddenTemplate(issue167Boss),
                    ref failures);

                var crawlingPartyBoss = MapFile.Parse(
                    DfoServer.GameWorld.PvfArchiveAccessor.ReadText(
                        Path.Combine(
                            "map",
                            "RealSkyCastle",
                            "RealSkyCastle06.map")));
                var crawlingSoloBoss = MapFile.Parse(
                    DfoServer.GameWorld.PvfArchiveAccessor.ReadText(
                        Path.Combine(
                            "map",
                            "dimensiongate_tutorial",
                            "ActiveSkyCastle",
                            "39132RealSkyCastle06.map")));
                var crawlingPartyActors =
                    DfoServer.GameWorld.DungeonActorTemplateProjector.Project(
                        crawlingPartyBoss,
                        dungeonBasicLevel: 70,
                        mapId: 16205);
                var crawlingSoloActors =
                    DfoServer.GameWorld.DungeonActorTemplateProjector.Project(
                        crawlingSoloBoss,
                        dungeonBasicLevel: 70,
                        mapId: 39132);
                Check("crawling city party and solo parents are not duplicated in START_MAP",
                    !ContainsActorType(crawlingPartyActors, 9)
                    && !ContainsActorType(crawlingSoloActors, 9)
                    && CountActor(crawlingPartyActors, 61128) == 1
                    && CountActor(crawlingSoloActors, 120081) == 1,
                    ref failures);

                var firstWaveIndex = IndexOfFirstHiddenTemplate(issue167Boss);
                var finalBossIndex = IndexOfMonster(issue167Boss, 10409);
                var scriptedWaveRun = new DungeonRun(89, 0)
                {
                    RoomStartSequence = 62,
                    RoomMonsters = issue167Boss.Monsters,
                };
                scriptedWaveRun.RoomKilledSeqIds.Add(
                    (ushort)(scriptedWaveRun.RoomStartSequence + firstWaveIndex));
                var clearsAfterFirstWaveMonster =
                    DungeonRoomTopology.ComputeRoomClearedLocked(
                        scriptedWaveRun,
                        out var blockingBeforeBoss,
                        out var killedBlockingBeforeBoss);
                scriptedWaveRun.RoomKilledSeqIds.Add(
                    (ushort)(scriptedWaveRun.RoomStartSequence + finalBossIndex));
                var clearsAfterFinalBoss =
                    DungeonRoomTopology.ComputeRoomClearedLocked(
                        scriptedWaveRun,
                        out var blockingAfterBoss,
                        out var killedBlockingAfterBoss);
                Check("issue 167 first wave death waits for the scripted final AI boss",
                    firstWaveIndex >= 0
                    && finalBossIndex >= 0
                    && !clearsAfterFirstWaveMonster
                    && blockingBeforeBoss == 1
                    && killedBlockingBeforeBoss == 0
                    && clearsAfterFinalBoss
                    && blockingAfterBoss == 1
                    && killedBlockingAfterBoss == 1,
                    ref failures);
                Check("issue 167 friendly quest AI remains non-blocking",
                    ContainsMonster(issue167QuestStart, 10625) && !ContainsBlockingMonster(issue167QuestStart, 10625),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 167 gent defense AI boss room: {ex.Message}");
                failures++;
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool ContainsMonster(DungeonData.MazeSumInfo maze, int monsterCode)
        {
            if (maze.Monsters == null)
                return false;
            foreach (var monster in maze.Monsters)
                if (monster.Code == monsterCode)
                    return true;
            return false;
        }

        private static bool ContainsBlockingMonster(DungeonData.MazeSumInfo maze, int monsterCode)
        {
            if (maze.Monsters == null)
                return false;
            foreach (var monster in maze.Monsters)
                if (monster.Code == monsterCode && monster.IsBlocking)
                    return true;
            return false;
        }

        private static int CountApcs(DungeonData.MazeSumInfo maze)
        {
            var count = 0;
            if (maze.Monsters == null)
                return count;
            foreach (var monster in maze.Monsters)
                if (monster.Type >= 5 && monster.Type <= 8)
                    count++;
            return count;
        }

        private static int CountBlockingApcs(DungeonData.MazeSumInfo maze)
        {
            var count = 0;
            if (maze.Monsters == null)
                return count;
            foreach (var monster in maze.Monsters)
                if (monster.Type >= 5 && monster.Type <= 8 && monster.IsBlocking)
                    count++;
            return count;
        }

        private static int CountFriendlyApcs(DungeonData.MazeSumInfo maze)
        {
            var count = 0;
            if (maze.Monsters == null)
                return count;
            foreach (var actor in maze.Monsters)
            {
                if (actor.Type >= 5
                    && actor.Type <= 8
                    && actor.Faction == ApcFaction.Character)
                {
                    count++;
                }
            }
            return count;
        }

        private static int CountHostileApcs(DungeonData.MazeSumInfo maze)
        {
            var count = 0;
            if (maze.Monsters == null)
                return count;
            foreach (var actor in maze.Monsters)
            {
                if (actor.Type >= 5
                    && actor.Type <= 8
                    && actor.Faction == ApcFaction.Monster)
                {
                    count++;
                }
            }
            return count;
        }

        private static int CountRawBlockingActors(DungeonData.MazeSumInfo maze)
        {
            var count = 0;
            if (maze.Monsters == null)
                return count;
            foreach (var actor in maze.Monsters)
            {
                if (actor.IsBlocking)
                    count++;
            }
            return count;
        }

        private static void TestEventMonsterCandidateRooms(ref int failures)
        {
            const int dungeonId = 152;
            DungeonFile dungeon;
            try
            {
                dungeon = DungeonData.GetDungeonFile(dungeonId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] event monster candidate PVF load: {ex.Message}");
                failures++;
                return;
            }

            var candidateCount = 0;
            var bossRejectedCount = 0;
            for (var mazeIndex = 1; mazeIndex <= 4; mazeIndex++)
            {
                if (mazeIndex >= dungeon.Mazes.Count)
                    break;

                var maze = dungeon.Mazes[mazeIndex];
                var run = new DungeonRun((short)dungeonId, 0)
                {
                    MazeIndex = mazeIndex,
                    BossMapPos = maze.BossMap,
                };

                foreach (var icon in maze.MinimapIcons)
                {
                    var room = DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId,
                        icon.X,
                        icon.Y,
                        mazeIndex,
                        bossPos: maze.BossMap);
                    if (EventMonsterConditionCoordinator.TryDescribeCandidateRoom(
                            run,
                            room,
                            out var descriptor)
                        && descriptor.MapId == room.Index
                        && descriptor.TargetCode > 0
                        && descriptor.SpecialObjectCount == 2
                        && descriptor.EventPositionCount == 4)
                    {
                        candidateCount++;
                    }
                }

                var bossRoom = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId,
                    maze.BossMap[0],
                    maze.BossMap[1],
                    mazeIndex,
                    bossPos: maze.BossMap);
                if (!EventMonsterConditionCoordinator.TryDescribeCandidateRoom(
                        run,
                        bossRoom,
                        out _))
                {
                    bossRejectedCount++;
                }
            }

            Check("Despot Altar marks three event candidates in each ordinary maze",
                candidateCount == 12,
                ref failures);
            Check("Despot Altar excludes the selected Boss room in every ordinary maze",
                bossRejectedCount == 4,
                ref failures);

            var taskMaze = dungeon.Mazes[0];
            var taskRun = new DungeonRun((short)dungeonId, 0)
            {
                MazeIndex = 0,
                MazeQuestConnected = true,
                BossMapPos = taskMaze.BossMap,
            };
            var taskRoom = DungeonData.GetDungeonMapMonsterSummaryInformation(
                dungeonId,
                4,
                1,
                0,
                bossPos: taskMaze.BossMap);
            Check("Despot Altar quest maze does not enter event condition",
                !EventMonsterConditionCoordinator.TryDescribeCandidateRoom(
                    taskRun,
                    taskRoom,
                    out _),
                ref failures);

            var ordinaryMaze = dungeon.Mazes[1];
            var ordinaryRun = new DungeonRun((short)dungeonId, 0)
            {
                MazeIndex = 1,
                BossMapPos = ordinaryMaze.BossMap,
            };
            var ordinaryRoom = DungeonData.GetDungeonMapMonsterSummaryInformation(
                dungeonId,
                3,
                2,
                1,
                bossPos: ordinaryMaze.BossMap);
            Check("Despot Altar ordinary unmarked room does not enter event condition",
                !EventMonsterConditionCoordinator.TryDescribeCandidateRoom(
                    ordinaryRun,
                    ordinaryRoom,
                    out _),
                ref failures);
        }

        private static int CountMonster(DungeonData.MazeSumInfo maze, int monsterCode)
        {
            var count = 0;
            if (maze.Monsters == null)
                return count;
            foreach (var monster in maze.Monsters)
                if (monster.Code == monsterCode)
                    count++;
            return count;
        }

        private static bool HasTemplate(DungeonData.MazeSumInfo maze, int monsterCode, ushort templateOrder, int packetIndex, byte flag1)
        {
            if (maze.Monsters == null)
                return false;
            foreach (var monster in maze.Monsters)
            {
                if (monster.Code == monsterCode
                    && monster.Type == 0
                    && monster.TemplateOrder == templateOrder
                    && monster.PacketIndex == packetIndex
                    && monster.Flag0 == 1
                    && monster.Flag1 == flag1)
                    return true;
            }
            return false;
        }

        private static bool ContainsActorType(
            IReadOnlyList<DungeonData.MonsterSumInfo> actors,
            byte type)
        {
            if (actors == null)
                return false;
            foreach (var actor in actors)
                if (actor.Type == type)
                    return true;
            return false;
        }

        private static int CountActor(
            IReadOnlyList<DungeonData.MonsterSumInfo> actors,
            int actorCode)
        {
            var count = 0;
            if (actors == null)
                return count;
            foreach (var actor in actors)
                if (actor.Code == actorCode)
                    count++;
            return count;
        }

        private static int IndexOfMonster(DungeonData.MazeSumInfo maze, int monsterCode)
        {
            if (maze.Monsters == null)
                return -1;
            for (var i = 0; i < maze.Monsters.Count; i++)
                if (maze.Monsters[i].Code == monsterCode)
                    return i;
            return -1;
        }

        private static int IndexOfFirstHiddenTemplate(DungeonData.MazeSumInfo maze)
        {
            if (maze.Monsters == null)
                return -1;
            for (var i = 0; i < maze.Monsters.Count; i++)
                if (maze.Monsters[i].Flag0 == 1)
                    return i;
            return -1;
        }

        private static int IndexOfLastHiddenTemplate(DungeonData.MazeSumInfo maze)
        {
            var index = -1;
            if (maze.Monsters == null)
                return index;
            for (var i = 0; i < maze.Monsters.Count; i++)
                if (maze.Monsters[i].Flag0 == 1)
                    index = i;
            return index;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok) failures++;
        }

        private static void CheckTowerMirrorApcInfo(ref int failures)
        {
            var expectedAppearance = new[]
            {
                510000, 510001, 510002, 510003, 510004,
                510005, 510006, 510007, 510008, 510009,
                511011,
            };
            var appearanceEntries =
                new Game.Characters.CharacterAppearanceEntry[12];
            for (byte slot = 0; slot < 10; slot++)
            {
                appearanceEntries[slot] =
                    new Game.Characters.CharacterAppearanceEntry(
                        slot,
                        expectedAppearance[slot],
                        4,
                        Array.Empty<byte>(),
                        0,
                        0,
                        0,
                        0);
            }
            appearanceEntries[10] =
                new Game.Characters.CharacterAppearanceEntry(
                    10,
                    599999,
                    4,
                    Array.Empty<byte>(),
                    0,
                    0,
                    0,
                    0);
            appearanceEntries[11] =
                new Game.Characters.CharacterAppearanceEntry(
                    11,
                    expectedAppearance[10],
                    4,
                    Array.Empty<byte>(),
                    0,
                    0,
                    0,
                    0);

            var creatureName = new[]
            {
                (byte)'m', (byte)'i', (byte)'r',
                (byte)'r', (byte)'o', (byte)'r',
            };
            const uint creatureItemId = 512345;
            var player = new Game.Session.PlayerContext
            {
                Name = new[]
                {
                    (byte)'f', (byte)'l', (byte)'o',
                    (byte)'o', (byte)'r', (byte)'1', (byte)'0',
                },
                Level = 86,
                Job = 0,
                GrowType = 1,
                AppearanceEntries = appearanceEntries,
                Subtype0Tail =
                    new Game.SelectCharacter.UserInfoMinimumTailSnapshot
                    {
                        EquippedCreatureNameBytes = creatureName,
                        EquippedCreatureItemId = creatureItemId,
                    },
            };

            var built = Network.Builders.TowerOfDespairApcInfoBuilder.TryBuild(
                11017,
                player,
                out var baseLayer,
                out var currentLayer);
            Check("tower of despair floor 10 builds base and current player-mirror APC data",
                built
                && IsTowerApcInfoBody(
                    baseLayer,
                    player,
                    0,
                    expectedAppearance,
                    creatureName,
                    creatureItemId)
                && IsTowerApcInfoBody(
                    currentLayer,
                    player,
                    10,
                    expectedAppearance,
                    creatureName,
                    creatureItemId),
                ref failures);
        }

        private static bool IsTowerApcInfoBody(
            byte[] body,
            Game.Session.PlayerContext player,
            byte expectedLayer,
            IReadOnlyList<int> expectedAppearance,
            byte[] expectedCreatureName,
            uint expectedCreatureItemId)
        {
            var name = player?.Name ?? Array.Empty<byte>();
            expectedCreatureName = expectedCreatureName ?? Array.Empty<byte>();
            if (body == null
                || body.Length != 112 + name.Length + expectedCreatureName.Length
                || body[0] != expectedLayer
                || BitConverter.ToInt32(body, 1) != name.Length)
            {
                return false;
            }

            for (var index = 0; index < name.Length; index++)
            {
                if (body[5 + index] != name[index])
                    return false;
            }

            var offset = 5 + name.Length;
            if (body[offset++] != player.Level
                || body[offset++] != player.Job
                || body[offset++] != player.GrowType)
            {
                return false;
            }

            var guildNameLength = BitConverter.ToInt32(body, offset);
            offset += 4;
            if (guildNameLength != 0 || BitConverter.ToInt32(body, offset) != 0)
                return false;
            offset += 4;

            for (var index = 0; index < 22; index++)
            {
                var expectedItemId = index < expectedAppearance.Count
                    ? expectedAppearance[index]
                    : 0;
                if (BitConverter.ToInt32(body, offset) != expectedItemId)
                    return false;
                offset += 4;
            }

            if (BitConverter.ToInt32(body, offset)
                != expectedCreatureName.Length)
            {
                return false;
            }
            offset += 4;
            for (var index = 0; index < expectedCreatureName.Length; index++)
            {
                if (body[offset + index] != expectedCreatureName[index])
                    return false;
            }

            offset += expectedCreatureName.Length;
            return BitConverter.ToUInt32(body, offset)
                == expectedCreatureItemId;
        }

        private static void TestTimeGateStartMapOwnership(ref int failures)
        {
            try
            {
                var hiddenStart =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 301,
                        x: 0xFF,
                        y: 0xFF,
                        mazeIndex: 0,
                        bossPos: new[] { 1, 2 });
                Check(
                    "time-gate hidden dungeon keeps its explicit owned start map",
                    hiddenStart.Index == 32530
                    && hiddenStart.Monsters.Count == 5
                    && ContainsMonster(hiddenStart, 62123),
                    ref failures);

                var questStart =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 71,
                        x: 0xFF,
                        y: 0xFF,
                        mazeIndex: 1,
                        bossPos: new[] { 1, 3 });
                var questStartOk = questStart.Index == 15360
                    && CountMonster(questStart, 62513) == 10;
                if (!questStartOk)
                {
                    Console.WriteLine(
                        $"  resolved map={questStart.Index} " +
                        $"monsters={questStart.Monsters.Count} " +
                        $"codes=[{string.Join(",", questStart.Monsters.ConvertAll(item => item.Code))}]");
                }
                Check(
                    "time-gate quest maze resolves the owned typed start map",
                    questStartOk,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[FAIL] time-gate start map ownership: {ex.Message}");
                failures++;
            }
        }

        private static void TestGentOuterAndVilmarkTaskMazeSelection(
            ref int failures)
        {
            try
            {
                var gentDungeon = DungeonData.GetDungeonFile(80);
                var gentOrdinary = DungeonData.SelectDungeonMaze(80);
                var gentWithQuest = DungeonData.SelectDungeonMaze(
                    dungeonId: 80,
                    activeQuestIds: new HashSet<int> { 2121 });
                var gentOrdinaryStart =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 80,
                        x: 2,
                        y: 2,
                        mazeIndex: gentOrdinary.Index,
                        bossPos: gentOrdinary.Maze.BossMap);
                var gentQuestStart =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 80,
                        x: 2,
                        y: 2,
                        mazeIndex: gentWithQuest.Index,
                        bossPos: gentWithQuest.Maze.BossMap);
                var gentBoss =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 80,
                        x: 0,
                        y: 2,
                        mazeIndex: gentWithQuest.Index,
                        bossPos: gentWithQuest.Maze.BossMap);
                Check(
                    "Gent Outskirts quest cannot replace its only ordinary maze",
                    gentDungeon.Mazes.Count == 1
                    && gentDungeon.Mazes[0].QuestConnection == null
                    && gentOrdinary.Index == 0
                    && gentWithQuest.Index == 0
                    && gentOrdinaryStart.Index == 22400
                    && gentQuestStart.Index == 22400
                    && gentBoss.Index == 22410,
                    ref failures);

                var hasGentAdmission = WorldMap.TryGetAdmissionDefinition(
                    80,
                    out var gentAdmission);
                var gentAdmissionDecision = WorldMap.EvaluateDungeonAdmission(
                    80,
                    new HashSet<int>(),
                    new HashSet<int>());
                Check(
                    "Gent Outskirts remains an unrestricted persistent dungeon",
                    hasGentAdmission
                    && gentAdmission.Mode == DungeonAdmissionMode.Unrestricted
                    && gentAdmission.HasUnrestrictedEntry
                    && gentAdmission.PersistentQuestIds.Count == 0
                    && gentAdmission.ActiveQuestIds.Count == 0
                    && gentAdmissionDecision.Allowed
                    && !WorldMap.IsTaskExclusiveDungeon(80)
                    && WorldMap.ShouldPersistDungeonPermission(80),
                    ref failures);

                var hasVilmarkAdmission = WorldMap.TryGetAdmissionDefinition(
                    240,
                    out var vilmarkAdmission);
                var expectedAdmissionQuests = new HashSet<int>
                {
                    2091,
                    2092,
                    2097,
                    2098,
                };
                Check(
                    "Vilmark task dungeon uses the WDM active-only admission set",
                    hasVilmarkAdmission
                    && vilmarkAdmission.Mode
                        == DungeonAdmissionMode.ActiveQuestOnly
                    && !vilmarkAdmission.HasUnrestrictedEntry
                    && vilmarkAdmission.PersistentQuestIds.Count == 0
                    && new HashSet<int>(vilmarkAdmission.ActiveQuestIds)
                        .SetEquals(expectedAdmissionQuests)
                    && WorldMap.IsTaskExclusiveDungeon(240)
                    && !WorldMap.ShouldPersistDungeonPermission(240),
                    ref failures);

                var noQuestAdmission = WorldMap.EvaluateDungeonAdmission(
                    240,
                    new HashSet<int>(),
                    new HashSet<int> { 2091 });
                var allActiveAdmissionsPass = true;
                var allActiveQuestsProject = true;
                foreach (var questId in expectedAdmissionQuests)
                {
                    var decision = WorldMap.EvaluateDungeonAdmission(
                        240,
                        new HashSet<int> { questId },
                        new HashSet<int>());
                    allActiveAdmissionsPass &= decision.Allowed;

                    var projected = QuestDungeonPresentationPlanner
                        .ProjectActiveQuestIds(
                            new[]
                            {
                                new ActiveQuest
                                {
                                    Slot = 0,
                                    QuestId = (ushort)questId,
                                },
                            },
                            new Dictionary<int, int>());
                    allActiveQuestsProject &= projected.Contains(
                        (ushort)questId);
                }
                Check(
                    "Vilmark task dungeon rejects cleared-only state and accepts every configured active quest",
                    !noQuestAdmission.Allowed
                    && allActiveAdmissionsPass
                    && allActiveQuestsProject,
                    ref failures);

                var ordinaryVilmark = DungeonData.SelectDungeonMaze(240);
                var questVilmark = DungeonData.SelectDungeonMaze(
                    dungeonId: 240,
                    activeQuestIds: new HashSet<int> { 2091 });
                var clearedVilmark = DungeonData.SelectDungeonMaze(
                    dungeonId: 240,
                    clearedQuestIds: new HashSet<int> { 2091 });
                var laterQuestsKeepOrdinaryMaze = true;
                foreach (var questId in new[] { 2092, 2097, 2098 })
                {
                    laterQuestsKeepOrdinaryMaze &=
                        DungeonData.SelectDungeonMaze(
                            dungeonId: 240,
                            activeQuestIds: new HashSet<int> { questId })
                            .Index == 0;
                }

                var ordinaryVilmarkStart =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 240,
                        x: 0,
                        y: 0,
                        mazeIndex: ordinaryVilmark.Index,
                        bossPos: ordinaryVilmark.Maze.BossMap);
                var questVilmarkStart =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 240,
                        x: 0,
                        y: 0,
                        mazeIndex: questVilmark.Index,
                        bossPos: questVilmark.Maze.BossMap);
                var questVilmarkBoss =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 240,
                        x: 5,
                        y: 0,
                        mazeIndex: questVilmark.Index,
                        bossPos: questVilmark.Maze.BossMap);
                Check(
                    "Vilmark quest 2091 alone selects the APC start maze",
                    ordinaryVilmark.Index == 0
                    && clearedVilmark.Index == 0
                    && laterQuestsKeepOrdinaryMaze
                    && ordinaryVilmarkStart.Index == 37047
                    && !ContainsMonster(ordinaryVilmarkStart, 1528)
                    && questVilmark.Index == 1
                    && questVilmark.Maze.QuestConnection != null
                    && questVilmark.Maze.QuestConnection.Length >= 2
                    && questVilmark.Maze.QuestConnection[1] == 2091
                    && questVilmarkStart.Index == 909
                    && ContainsMonster(questVilmarkStart, 1528)
                    && questVilmarkBoss.Index == 37052,
                    ref failures);

                Check(
                    "Vilmark clear-map target keeps MAP 37052 and companion APC 1528 distinct",
                    QuestTargetIndex.TryGetClearMapDefinition(
                        2091,
                        out var clearMapDefinition)
                    && clearMapDefinition.TargetId == 37052
                    && clearMapDefinition.CompanionApcId == 1528
                    && QuestData.MatchesClearMapTarget(
                        2091,
                        dungeonId: 240,
                        mapId: 37052)
                    && !QuestData.MatchesClearMapTarget(
                        2091,
                        dungeonId: 240,
                        mapId: 909),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[FAIL] Gent/Vilmark task maze selection: {ex.Message}");
                failures++;
            }
        }

        private static void TestTimeGateQuestMazeSelection(ref int failures)
        {
            try
            {
                var questMazes = new Dictionary<int, Dictionary<int, int>>
                {
                    [70] = new Dictionary<int, int>
                    {
                        [2364] = 1, [2365] = 2, [2366] = 3,
                        [2367] = 4, [2368] = 5,
                    },
                    [71] = new Dictionary<int, int> { [2375] = 1, [2377] = 2 },
                    [72] = new Dictionary<int, int> { [2397] = 1, [2399] = 2 },
                    [73] = new Dictionary<int, int>
                    {
                        [2414] = 1, [2415] = 2, [2416] = 3,
                        [2417] = 4, [2420] = 5,
                    },
                    [74] = new Dictionary<int, int>
                    {
                        [2424] = 1, [2425] = 2, [2426] = 3,
                        [2436] = 4, [2437] = 5, [4726] = 6, [4497] = 7,
                    },
                    [75] = new Dictionary<int, int>
                    {
                        [2442] = 1, [4173] = 2, [4231] = 3,
                        [4232] = 4, [4233] = 5,
                    },
                    [76] = new Dictionary<int, int> { [2465] = 1 },
                    [77] = new Dictionary<int, int> { [2477] = 1 },
                };

                var allActiveSelectionsMatch = true;
                var clearedQuestsDoNotKeepTaskMazes = true;
                foreach (var dungeon in questMazes)
                {
                    foreach (var expected in dungeon.Value)
                    {
                        var active = DungeonData.SelectDungeonMaze(
                            dungeon.Key,
                            difficulty: 4,
                            activeQuestIds: new HashSet<int> { expected.Key });
                        if (active.Index != expected.Value
                            || active.Maze?.QuestConnection == null
                            || active.Maze.QuestConnection.Length < 2
                            || active.Maze.QuestConnection[1] != expected.Key)
                        {
                            Console.WriteLine(
                                $"  active mismatch dungeon={dungeon.Key} " +
                                $"quest={expected.Key} expected={expected.Value} actual={active.Index}");
                            allActiveSelectionsMatch = false;
                        }

                        var cleared = DungeonData.SelectDungeonMaze(
                            dungeon.Key,
                            difficulty: 4,
                            clearedQuestIds: new HashSet<int> { expected.Key });
                        if (cleared.Maze?.QuestConnection != null)
                        {
                            Console.WriteLine(
                                $"  cleared quest retained task maze dungeon={dungeon.Key} " +
                                $"quest={expected.Key} actual={cleared.Index}");
                            clearedQuestsDoNotKeepTaskMazes = false;
                        }
                    }
                }

                Check(
                    "time-gate active quests select every PVF-connected maze",
                    allActiveSelectionsMatch,
                    ref failures);
                Check(
                    "time-gate cleared quests no longer select doing-only mazes",
                    clearedQuestsDoNotKeepTaskMazes,
                    ref failures);

                var ordinaryBlackChurch = DungeonData.SelectDungeonMaze(
                    73,
                    difficulty: 4,
                    activeQuestIds: new HashSet<int> { 2412 });
                Check(
                    "Black Church hunt subquest without a quest connection keeps the ordinary maze",
                    ordinaryBlackChurch.Maze?.QuestConnection == null,
                    ref failures);

                var conflagrationSelection = DungeonData.SelectDungeonMaze(
                    70,
                    difficulty: 4,
                    activeQuestIds: new HashSet<int> { 2368 });
                var conflagrationStartIds = new HashSet<int>();
                for (var attempt = 0; attempt < 64; attempt++)
                {
                    var start = DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 70,
                        x: 0xFF,
                        y: 0xFF,
                        mazeIndex: conflagrationSelection.Index,
                        bossPos: new[] { 0, 1 });
                    conflagrationStartIds.Add(start.Index);
                }
                var conflagrationBoss =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 70,
                        x: 0,
                        y: 1,
                        mazeIndex: conflagrationSelection.Index,
                        bossPos: new[] { 0, 1 });
                Check(
                    "Conflagration final quest resolves its paired two-room start and Boss maps",
                    conflagrationSelection.Index == 5
                    && conflagrationStartIds.SetEquals(new[] { 15399 })
                    && conflagrationBoss.Index == 15400,
                    ref failures);

                var blackChurchSelection = DungeonData.SelectDungeonMaze(
                    73,
                    difficulty: 4,
                    activeQuestIds: new HashSet<int> { 2414 });
                var blackChurchStartIds = ResolveRepeatedStartMapIds(
                    dungeonId: 73,
                    mazeIndex: blackChurchSelection.Index,
                    bossPos: new[] { 0, 0 });
                var blackChurchBoss =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 73,
                        x: 0,
                        y: 0,
                        mazeIndex: blackChurchSelection.Index,
                        bossPos: new[] { 0, 0 });
                Check(
                    "Black Church meeting quest resolves the greed-compatible start and Boss resource group",
                    blackChurchSelection.Index == 1
                    && blackChurchStartIds.SetEquals(new[] { 15376 })
                    && blackChurchBoss.Index == 15368,
                    ref failures);

                var nightmareSelection = DungeonData.SelectDungeonMaze(
                    171,
                    difficulty: 4,
                    activeQuestIds: new HashSet<int> { 2072 });
                var nightmareFirstImplicitRoom =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 171,
                        x: 2,
                        y: 1,
                        mazeIndex: nightmareSelection.Index,
                        bossPos: new[] { 4, 0 });
                var nightmareSecondImplicitRoom =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 171,
                        x: 3,
                        y: 1,
                        mazeIndex: nightmareSelection.Index,
                        bossPos: new[] { 4, 0 });
                Check(
                    "Nightmare phantom quest resolves omitted FF rooms by MAP greed",
                    nightmareSelection.Index == 3
                    && nightmareFirstImplicitRoom.Index == 14313
                    && nightmareSecondImplicitRoom.Index == 14313,
                    ref failures);

                var winterSelection = DungeonData.SelectDungeonMaze(
                    76,
                    difficulty: 4,
                    activeQuestIds: new HashSet<int> { 2465 });
                var winterStartIds = ResolveRepeatedStartMapIds(
                    dungeonId: 76,
                    mazeIndex: winterSelection.Index,
                    bossPos: new[] { 1, 1 });
                var winterMiddle =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 76,
                        x: 1,
                        y: 0,
                        mazeIndex: winterSelection.Index,
                        bossPos: new[] { 1, 1 });
                var winterBoss =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 76,
                        x: 1,
                        y: 1,
                        mazeIndex: winterSelection.Index,
                        bossPos: new[] { 1, 1 });
                Check(
                    "Winter master quest resolves its complete paired resource group",
                    winterSelection.Index == 1
                    && winterStartIds.SetEquals(new[] { 15387 })
                    && winterMiddle.Index == 15388
                    && winterBoss.Index == 15389,
                    ref failures);

                var consciousnessSelection = DungeonData.SelectDungeonMaze(
                    77,
                    difficulty: 4,
                    activeQuestIds: new HashSet<int> { 2477 });
                var consciousnessStartIds = ResolveRepeatedStartMapIds(
                    dungeonId: 77,
                    mazeIndex: consciousnessSelection.Index,
                    bossPos: new[] { 4, 0 });
                var consciousnessQuestMap =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 77,
                        x: 0,
                        y: 0,
                        mazeIndex: consciousnessSelection.Index,
                        bossPos: new[] { 4, 0 });
                Check(
                    "Consciousness quest prefers its exact typed start before affinity fallback",
                    consciousnessSelection.Index == 1
                    && consciousnessStartIds.SetEquals(new[] { 15303 })
                    && consciousnessQuestMap.Index == 15411,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[FAIL] time-gate quest maze selection: {ex.Message}");
                failures++;
            }
        }

        private static void TestPersonalSkillQuestMazeSelection(ref int failures)
        {
            try
            {
                var timeCrackNpcQuest = DungeonData.SelectDungeonMaze(
                    dungeonId: 2007,
                    difficulty: 0,
                    activeQuestIds: new HashSet<int> { 2510 });
                var timeCrackNpcBoss =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 2007,
                        x: 9,
                        y: 0,
                        mazeIndex: timeCrackNpcQuest.Index,
                        bossPos: new[] { 9, 0 });
                Check(
                    "TimeCrack NPC personal quest selects its connected maze",
                    timeCrackNpcQuest.Index == 2
                        && timeCrackNpcQuest.Maze?.QuestConnection != null
                        && timeCrackNpcQuest.Maze.QuestConnection[1] == 2510
                        && timeCrackNpcBoss.Index == 35802,
                    ref failures);

                var timeCrackEarlierQuests = DungeonData.SelectDungeonMaze(
                    dungeonId: 2007,
                    difficulty: 0,
                    activeQuestIds: new HashSet<int>
                    {
                        13508,
                        13509,
                        13510,
                    });
                Check(
                    "earlier TimeCrack personal quests keep an ordinary maze",
                    timeCrackEarlierQuests.Index != 2
                        && timeCrackEarlierQuests.Maze?.QuestConnection == null,
                    ref failures);

                var madnessFinalQuest = DungeonData.SelectDungeonMaze(
                    dungeonId: 2012,
                    difficulty: 0,
                    activeQuestIds: new HashSet<int> { 12690 });
                var madnessQuestStart =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 2012,
                        x: 0xFF,
                        y: 0xFF,
                        mazeIndex: madnessFinalQuest.Index,
                        bossPos: new[] { 3, 1 });
                var madnessQuestBoss =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 2012,
                        x: 3,
                        y: 1,
                        mazeIndex: madnessFinalQuest.Index,
                        bossPos: new[] { 3, 1 });
                Check(
                    "Madness Adenvine final personal quest selects its connected route",
                    madnessFinalQuest.Index == 1
                        && madnessFinalQuest.Maze?.QuestConnection != null
                        && madnessFinalQuest.Maze.QuestConnection[1] == 12690
                        && madnessQuestStart.Index == 17174
                        && madnessQuestBoss.Index == 17178,
                    ref failures);

                var madnessEarlierQuest = DungeonData.SelectDungeonMaze(
                    dungeonId: 2012,
                    difficulty: 0,
                    activeQuestIds: new HashSet<int> { 12686 });
                var madnessCompletedQuest = DungeonData.SelectDungeonMaze(
                    dungeonId: 2012,
                    difficulty: 0,
                    clearedQuestIds: new HashSet<int> { 12690 });
                Check(
                    "Madness Adenvine non-final or completed quests keep the ordinary route",
                    madnessEarlierQuest.Index == 0
                        && madnessEarlierQuest.Maze?.QuestConnection == null
                        && madnessCompletedQuest.Index == 0
                        && madnessCompletedQuest.Maze?.QuestConnection == null,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[FAIL] personal-skill quest maze selection: {ex.Message}");
                failures++;
            }
        }

        private static HashSet<int> ResolveRepeatedStartMapIds(
            int dungeonId,
            int mazeIndex,
            int[] bossPos)
        {
            var mapIds = new HashSet<int>();
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var start = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId,
                    x: 0xFF,
                    y: 0xFF,
                    mazeIndex: mazeIndex,
                    bossPos: bossPos);
                mapIds.Add(start.Index);
            }

            return mapIds;
        }

        private static void TestAntwerpAndTrainMazeSelection(ref int failures)
        {
            try
            {
                var questMazes = new Dictionary<int, Dictionary<int, int>>
                {
                    [81] = new Dictionary<int, int> { [4212] = 0, [2140] = 2 },
                    [82] = new Dictionary<int, int>
                    {
                        [4360] = 0, [4361] = 1, [4362] = 2,
                        [4363] = 3, [2145] = 5,
                    },
                    [83] = new Dictionary<int, int>
                    {
                        [5720] = 1, [5729] = 2, [2195] = 3, [5737] = 4,
                    },
                    [84] = new Dictionary<int, int>
                    {
                        [5743] = 1, [5745] = 2, [2211] = 4, [2214] = 6,
                    },
                    [85] = new Dictionary<int, int>
                    {
                        [5765] = 1, [5766] = 2, [2218] = 3, [2219] = 4,
                        [2220] = 5, [2221] = 6, [2222] = 7, [2223] = 8,
                        [2224] = 9, [2225] = 10, [2228] = 11, [2227] = 12,
                    },
                    [88] = new Dictionary<int, int> { [2159] = 1, [2160] = 2 },
                    [89] = new Dictionary<int, int> { [2179] = 1, [2182] = 2 },
                    [86] = new Dictionary<int, int> { [2245] = 2 },
                    [87] = new Dictionary<int, int>
                    {
                        [5911] = 1, [5866] = 2, [2253] = 3,
                    },
                    [92] = new Dictionary<int, int>
                    {
                        [4039] = 1, [4040] = 2, [2269] = 3, [2274] = 4,
                    },
                    [93] = new Dictionary<int, int>
                    {
                        [4052] = 1, [4049] = 2, [2283] = 3, [2288] = 4,
                    },
                };

                var activeSelectionsMatch = true;
                var clearedSelectionsAreOrdinary = true;
                foreach (var dungeon in questMazes)
                {
                    foreach (var expected in dungeon.Value)
                    {
                        var active = DungeonData.SelectDungeonMaze(
                            dungeon.Key,
                            difficulty: 4,
                            activeQuestIds: new HashSet<int> { expected.Key });
                        if (active.Index != expected.Value
                            || active.Maze?.QuestConnection == null
                            || active.Maze.QuestConnection.Length < 2
                            || active.Maze.QuestConnection[1] != expected.Key)
                        {
                            activeSelectionsMatch = false;
                        }

                        var cleared = DungeonData.SelectDungeonMaze(
                            dungeon.Key,
                            difficulty: 4,
                            clearedQuestIds: new HashSet<int> { expected.Key });
                        if (cleared.Maze?.QuestConnection != null)
                            clearedSelectionsAreOrdinary = false;
                    }
                }

                Check(
                    "Antwerp and train active quests select every PVF-connected maze",
                    activeSelectionsMatch,
                    ref failures);
                Check(
                    "Antwerp and train cleared doing-only quests return to ordinary mazes",
                    clearedSelectionsAreOrdinary,
                    ref failures);

                var ardenCompanionMaze = DungeonData.GetDungeonFile(93).Mazes[4];
                var ardenCompanionStart =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 93,
                        x: 0xFF,
                        y: 0xFF,
                        mazeIndex: 4,
                        bossPos: ardenCompanionMaze.BossMap);
                Check(
                    "Arden companion quest falls back to a valid APC start MAP",
                    ardenCompanionStart.Index == 12506
                    && CountActor(ardenCompanionStart.Monsters, 11516) == 1,
                    ref failures);

                var blackEarthQuestRoom =
                    DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 182,
                        x: 2,
                        y: 0,
                        mazeIndex: 1,
                        bossPos: new[] { 0, 0 });
                Check(
                    "Black Earth objective MAP keeps hostile actor blocking and story actor non-blocking",
                    blackEarthQuestRoom.Index == 39118
                    && blackEarthQuestRoom.Monsters.Count == 2
                    && blackEarthQuestRoom.Monsters[0].Code == 57022
                    && blackEarthQuestRoom.Monsters[0].IsBlocking
                    && blackEarthQuestRoom.Monsters[1].Code == 57054
                    && !blackEarthQuestRoom.Monsters[1].IsBlocking,
                    ref failures);

                string trainDifficultyDiagnostic = null;
                var trainBelowRequiredDifficulty = DungeonData.SelectDungeonMaze(
                    87,
                    difficulty: 3,
                    activeQuestIds: new HashSet<int> { 5911 },
                    diagnosticSink: value => trainDifficultyDiagnostic = value);
                var trainAtRequiredDifficulty = DungeonData.SelectDungeonMaze(
                    87,
                    difficulty: 4,
                    activeQuestIds: new HashSet<int> { 5911 });
                Check(
                    "Sea Train quest 5911 keeps the PVF difficulty boundary",
                    trainBelowRequiredDifficulty.Maze?.QuestConnection == null
                    && trainAtRequiredDifficulty.Index == 1
                    && trainAtRequiredDifficulty.Maze?.QuestConnection?[1] == 5911,
                    ref failures);
                Check(
                    "Sea Train route diagnostic explains a difficulty rejection",
                    trainDifficultyDiagnostic != null
                    && trainDifficultyDiagnostic.Contains("quest=5911")
                    && trainDifficultyDiagnostic.Contains("result=difficulty_miss")
                    && trainDifficultyDiagnostic.Contains("selectedMaze="),
                    ref failures);

                var supplyCutStartMaps = ResolveRepeatedStartMapIds(
                    dungeonId: 84,
                    mazeIndex: 0,
                    bossPos: null);
                Check(
                    "Supply Cut omitted start cell resolves its unique dungeon-start-area MAP",
                    supplyCutStartMaps.SetEquals(new[] { 21352 }),
                    ref failures);

                var pursuitStartMaps = ResolveRepeatedStartMapIds(
                    dungeonId: 85,
                    mazeIndex: 0,
                    bossPos: null);
                Check(
                    "Pursuit keeps only its three explicit PVF start variants",
                    pursuitStartMaps.IsSubsetOf(new[] { 21415, 21416, 21417 })
                    && pursuitStartMaps.Count > 0,
                    ref failures);

                var pirateOrdinaryMazes = new HashSet<int>();
                var pirateSelectionsValid = true;
                for (var attempt = 0; attempt < 128; attempt++)
                {
                    var selected = DungeonData.SelectDungeonMaze(86);
                    pirateOrdinaryMazes.Add(selected.Index);
                    if ((selected.Index != 0 && selected.Index != 1)
                        || selected.Maze.QuestConnection != null)
                    {
                        pirateSelectionsValid = false;
                    }
                }
                Check(
                    "Train Pirate ordinary selection randomizes only its two PVF routes",
                    pirateSelectionsValid
                    && pirateOrdinaryMazes.SetEquals(new[] { 0, 1 }),
                    ref failures);

                var hazeMaze = DungeonData.GetDungeonFile(92).Mazes[0];
                var hazeStarts = CollectRandomizedPositions(
                    hazeMaze.StartMap,
                    DungeonData.RandomizeStartPosition);
                var hazeBosses = CollectRandomizedPositions(
                    hazeMaze.BossMap,
                    DungeonData.RandomizeBossPosition);
                var leftStart = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    92, 1, 5, mazeIndex: 0, bossPos: new[] { 0, 0 });
                var rightStart = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    92, 3, 5, mazeIndex: 0, bossPos: new[] { 4, 0 });
                Check(
                    "Haze PVF randomizes both start and Boss coordinate pairs",
                    hazeStarts.SetEquals(new[]
                    {
                        new DungeonRoomPoint(1, 5),
                        new DungeonRoomPoint(3, 5),
                    })
                    && hazeBosses.SetEquals(new[]
                    {
                        new DungeonRoomPoint(0, 0),
                        new DungeonRoomPoint(4, 0),
                    })
                    && leftStart.Index == 15189
                    && rightStart.Index == 15195,
                    ref failures);

                var hazeLeftBossMaps = ResolveRepeatedRoomMapIds(
                    dungeonId: 92,
                    mazeIndex: 0,
                    x: 0,
                    y: 0,
                    bossPos: new[] { 0, 0 });
                var hazeRightBossMaps = ResolveRepeatedRoomMapIds(
                    dungeonId: 92,
                    mazeIndex: 0,
                    x: 4,
                    y: 0,
                    bossPos: new[] { 4, 0 });
                Check(
                    "Haze ordinary maze keeps its base Boss resource at either randomized endpoint",
                    hazeLeftBossMaps.SetEquals(new[] { 15183 })
                    && hazeRightBossMaps.SetEquals(new[] { 15183 }),
                    ref failures);

                var southGateTaskMaze = DungeonData.SelectDungeonMaze(
                    82,
                    difficulty: 4,
                    activeQuestIds: new HashSet<int> { 4361 });
                var southGateStarts = CollectRandomizedPositions(
                    southGateTaskMaze.Maze.StartMap,
                    DungeonData.RandomizeStartPosition);
                Check(
                    "Gent South Gate task maze keeps both configured start positions",
                    southGateTaskMaze.Index == 1
                    && southGateStarts.SetEquals(new[]
                    {
                        new DungeonRoomPoint(1, 2),
                        new DungeonRoomPoint(3, 2),
                    }),
                    ref failures);

                var singleStartMaze = DungeonData.GetDungeonFile(80).Mazes[0];
                var singleStart = DungeonData.RandomizeStartPosition(
                    singleStartMaze.StartMap);
                Check(
                    "single-start dungeons keep their configured start position",
                    singleStart != null
                    && singleStart[0] == singleStartMaze.StartMap[0]
                    && singleStart[1] == singleStartMaze.StartMap[1],
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[FAIL] Antwerp and train maze selection: {ex.Message}");
                failures++;
            }
        }

        private static HashSet<DungeonRoomPoint> CollectRandomizedPositions(
            int[] positions,
            Func<int[], int[]> pick)
        {
            var result = new HashSet<DungeonRoomPoint>();
            for (var attempt = 0; attempt < 128; attempt++)
            {
                var selected = pick(positions);
                if (selected != null)
                    result.Add(new DungeonRoomPoint(selected[0], selected[1]));
            }

            return result;
        }

        private static HashSet<int> ResolveRepeatedRoomMapIds(
            int dungeonId,
            int mazeIndex,
            int x,
            int y,
            int[] bossPos)
        {
            var mapIds = new HashSet<int>();
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var room = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId,
                    x,
                    y,
                    mazeIndex,
                    bossPos: bossPos);
                mapIds.Add(room.Index);
            }

            return mapIds;
        }

        private static void TestDriftCaveMazeSelection(ref int failures)
        {
            try
            {
                var goldDungeon = DungeonData.GetDungeonFile(153);
                Check(
                    "Golden Cave PVF keeps two ordinary route mazes",
                    goldDungeon.Mazes.Count >= 3
                    && goldDungeon.Mazes[0].QuestConnection == null
                    && goldDungeon.Mazes[1].QuestConnection == null
                    && goldDungeon.Mazes[0].StartMap[0] == 0
                    && goldDungeon.Mazes[0].BossMap[0] == 1
                    && goldDungeon.Mazes[1].StartMap[0] == 4
                    && goldDungeon.Mazes[1].BossMap[0] == 3,
                    ref failures);

                var selectedGoldMazes = new HashSet<int>();
                var goldSelectionsValid = true;
                for (var attempt = 0; attempt < 64; attempt++)
                {
                    var selected = DungeonData.SelectDungeonMaze(153);
                    selectedGoldMazes.Add(selected.Index);
                    if ((selected.Index != 0 && selected.Index != 1)
                        || selected.Maze.QuestConnection != null)
                    {
                        goldSelectionsValid = false;
                        break;
                    }
                }
                Check(
                    "Golden Cave ordinary selection randomizes only the two PVF routes",
                    goldSelectionsValid && selectedGoldMazes.SetEquals(new[] { 0, 1 }),
                    ref failures);

                var goldLeftBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 153,
                    x: 1,
                    y: 3,
                    mazeIndex: 0,
                    bossPos: new[] { 1, 3 });
                var goldRightBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 153,
                    x: 3,
                    y: 3,
                    mazeIndex: 1,
                    bossPos: new[] { 3, 3 });
                Check(
                    "Golden Cave route-specific Boss rooms resolve both PVF maps",
                    goldLeftBoss.Index == 8113
                    && goldRightBoss.Index == 8114
                    && ContainsMonster(goldLeftBoss, 65026)
                    && ContainsMonster(goldRightBoss, 65026),
                    ref failures);

                var ordinaryAncient = DungeonData.SelectDungeonMaze(154);
                var questAncient = DungeonData.SelectDungeonMaze(
                    154,
                    activeQuestIds: new HashSet<int> { 1896 });
                Check(
                    "Ancient Tomb selects its quest-connected maze only for active quest 1896",
                    ordinaryAncient.Index == 0
                    && questAncient.Index == 1
                    && questAncient.Maze.QuestConnection != null
                    && questAncient.Maze.QuestConnection[1] == 1896,
                    ref failures);

                var ordinaryStart = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 154,
                    x: 0xFF,
                    y: 0xFF,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 1 });
                var questStart = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 154,
                    x: 0xFF,
                    y: 0xFF,
                    mazeIndex: 1,
                    bossPos: new[] { 4, 1 });
                Check(
                    "Ancient Tomb logical start uses the typed physical start map",
                    ordinaryStart.X == 0
                    && ordinaryStart.Y == 1
                    && ordinaryStart.Index == 8200
                    && questStart.Index == 8200
                    && ContainsMonster(ordinaryStart, 63515)
                    && ContainsMonster(questStart, 63515),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] Drift Cave PVF maze selection: {ex.Message}");
                failures++;
            }
        }

        private static void TestExplicitMazeStartSpecification(ref int failures)
        {
            try
            {
                var questStart = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 169,
                    x: 0xFF,
                    y: 0xFF,
                    mazeIndex: 0);
                var ordinaryStart = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 169,
                    x: 0xFF,
                    y: 0xFF,
                    mazeIndex: 2);

                Check(
                    "explicit maze start specification prevents cross-maze typed start leakage",
                    questStart.Index == 14221
                    && ordinaryStart.Index == 14289,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[FAIL] explicit maze start specification ownership: {ex.Message}");
                failures++;
            }
        }

        private static void TestNamedMonsterRoomFilter(ref int failures)
        {
            try
            {
                var parsed = DungeonFile.Parse(
                    "[named monster]\n100 200\n" +
                    "[named monster map pos]\n1 2 3 4 5\n");
                Check(
                    "named monster map positions parse ordered coordinate pairs and ignore an incomplete tail",
                    parsed.NamedMonsterMapPositions.Count == 2
                    && parsed.NamedMonsterMapPositions[0].X == 1
                    && parsed.NamedMonsterMapPositions[0].Y == 2
                    && parsed.NamedMonsterMapPositions[1].X == 3
                    && parsed.NamedMonsterMapPositions[1].Y == 4,
                    ref failures);

                var gentDungeon = DungeonData.GetDungeonFile(89);
                Check(
                    "Gent Defence named monster map positions preserve special-object indexes 2, 5 and 8",
                    gentDungeon.NamedMonsterMapPositions.Count == 9
                    && IsPosition(gentDungeon.NamedMonsterMapPositions[2], 4, 0)
                    && IsPosition(gentDungeon.NamedMonsterMapPositions[5], 2, 1)
                    && IsPosition(gentDungeon.NamedMonsterMapPositions[8], 4, 2),
                    ref failures);

                var instance = new DungeonInstance(89, 2);
                var unfiltered = LoadGentBossRoom();
                var removed = NamedMonsterRoomFilter.Apply(
                    instance,
                    gentDungeon,
                    ref unfiltered);
                Check(
                    "Gent Defence keeps all Boss-wave named monsters before mapped rooms are cleared",
                    removed == 0
                    && CountSourceActor(unfiltered, 59013, 2) == 1
                    && CountSourceActor(unfiltered, 59015, 5) == 1
                    && CountSourceActor(unfiltered, 59014, 8) == 1,
                    ref failures);

                MarkInstanceRoomCleared(instance, 4, 0);
                var oneCleared = LoadGentBossRoom();
                oneCleared.Monsters.Add(new DungeonData.MonsterSumInfo
                {
                    Code = 59013,
                    Flag0 = 0,
                    SourceSpecialPassiveObjectIndex = 2,
                });
                removed = NamedMonsterRoomFilter.Apply(
                    instance,
                    gentDungeon,
                    ref oneCleared);
                Check(
                    "Gent Defence removes only the named monster mapped to the cleared room",
                    removed == 1
                    && CountSourceActor(oneCleared, 59013, 2) == 1
                    && CountSourceHiddenActor(oneCleared, 59013, 2) == 0
                    && CountSourceActor(oneCleared, 59015, 5) == 1
                    && CountSourceActor(oneCleared, 59014, 8) == 1
                    && CountSourceActor(oneCleared, 61494, 2) == 1,
                    ref failures);

                MarkInstanceRoomCleared(instance, 2, 1);
                MarkInstanceRoomCleared(instance, 4, 2);
                var allCleared = LoadGentBossRoom();
                removed = NamedMonsterRoomFilter.Apply(
                    instance,
                    gentDungeon,
                    ref allCleared);
                Check(
                    "Gent Defence shared instance suppresses all three cleared named monsters without deleting wave escorts",
                    removed == 3
                    && CountSourceActor(allCleared, 59013, 2) == 0
                    && CountSourceActor(allCleared, 59015, 5) == 0
                    && CountSourceActor(allCleared, 59014, 8) == 0
                    && CountSourceActor(allCleared, 61494, 2) == 1
                    && CountSourceActor(allCleared, 61494, 5) == 1
                    && CountSourceActor(allCleared, 61494, 8) == 1,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] Gent Defence named monster room filter: {ex.Message}");
                failures++;
            }
        }

        private static DungeonData.MazeSumInfo LoadGentBossRoom()
            => DungeonData.GetDungeonMapMonsterSummaryInformation(
                dungeonId: 89,
                x: 0,
                y: 1,
                mazeIndex: 0,
                bossPos: new[] { 0, 1 });

        private static void TestResolvedRoomTemplateIsolation(
            ref int failures)
        {
            try
            {
                var first = LoadGentBossRoom();
                var actorCount = first.Monsters?.Count ?? 0;
                var specialObjectCount = first.SpecialPassiveObjects?.Count ?? 0;
                var firstSpecialObjectCode = specialObjectCount > 0
                    ? first.SpecialPassiveObjects[0].ObjectCode
                    : 0;

                first.Monsters?.Clear();
                if (specialObjectCount > 0)
                    first.SpecialPassiveObjects[0].ObjectCode = -1;

                var second = LoadGentBossRoom();
                Check(
                    "resolved room actor mutations do not leak into cached templates",
                    actorCount > 0
                    && second.Monsters?.Count == actorCount,
                    ref failures);
                Check(
                    "resolved room passive-object mutations do not leak into cached definitions",
                    specialObjectCount == 0
                    || (second.SpecialPassiveObjects?.Count == specialObjectCount
                        && second.SpecialPassiveObjects[0].ObjectCode
                            == firstSpecialObjectCode),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[FAIL] resolved room template isolation: {ex.Message}");
                failures++;
            }
        }

        private static void MarkInstanceRoomCleared(
            DungeonInstance instance,
            int x,
            int y)
        {
            var key = new RoomKey(x, y, 0);
            var room = instance.GetOrCreateRoom(
                key,
                roomId => new DungeonInstanceRoom(
                    roomId,
                    key,
                    new DungeonData.MazeSumInfo
                    {
                        X = x,
                        Y = y,
                        Index = 1,
                        Monsters = new List<DungeonData.MonsterSumInfo>(),
                    },
                    seed: 1),
                out _);
            room.TryActivate();
            room.TryClear();
        }

        private static int CountSourceActor(
            DungeonData.MazeSumInfo maze,
            int code,
            int sourceObjectIndex)
        {
            var count = 0;
            foreach (var actor in maze.Monsters)
            {
                if (actor.Code == code
                    && actor.SourceSpecialPassiveObjectIndex == sourceObjectIndex)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountSourceHiddenActor(
            DungeonData.MazeSumInfo maze,
            int code,
            int sourceObjectIndex)
        {
            var count = 0;
            foreach (var actor in maze.Monsters)
            {
                if (actor.Code == code
                    && actor.Flag0 == 1
                    && actor.SourceSpecialPassiveObjectIndex == sourceObjectIndex)
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsPosition(
            NamedMonsterMapPosition position,
            int x,
            int y)
            => position != null && position.X == x && position.Y == y;

        private static void CheckSuitableLevelEligibility(ref int failures)
        {
            Check("suitable dungeon range uses minimum and basis level",
                DungeonData.TryGetSuitableLevelRange(144, out var minLevel, out var maxLevel)
                && minLevel == 1
                && maxLevel == 5,
                ref failures);
            Check("suitable dungeon rejects level below min",
                !DungeonData.IsSuitableLevelDungeon(144, 0),
                ref failures);
            Check("suitable dungeon accepts min level",
                DungeonData.IsSuitableLevelDungeon(144, 1),
                ref failures);
            Check("suitable dungeon accepts max level",
                DungeonData.IsSuitableLevelDungeon(144, 5),
                ref failures);
            Check("suitable dungeon rejects level above max",
                !DungeonData.IsSuitableLevelDungeon(144, 6),
                ref failures);
        }
    }
}
