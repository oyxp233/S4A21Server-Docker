using System;
using System.Collections.Generic;
using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Network.Handlers.Dungeon;
using PvfLib;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.SelfTests
{
    public static class DungeonMapParsingSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_MAP_PARSING selftest ===");
            var failures = 0;

            var fallbackMapId = DungeonMapResolver
                .SelectFallbackMapIdForUnresolvedRoom(
                    dungeonId: 1004,
                    mazeIndex: 0,
                    x: 7,
                    y: 0,
                    mapSpecifications: new List<MapSpecificationItem>
                    {
                        new MapSpecificationItem
                        {
                            Type = "map",
                            X = 0,
                            Y = 0,
                            Index = 13417,
                        },
                    },
                    mapEntries: new List<LstEntry>
                    {
                        new LstEntry
                        {
                            Id = 13417,
                            FilePath = "eternal_dream/01.map",
                        },
                        new LstEntry
                        {
                            Id = 14999,
                            FilePath = "eternal_dream/q_7_0.map",
                        },
                    },
                    mapDirCandidates: new List<string>
                    {
                        "eternal_dream",
                    },
                    preferQuestVariant: true,
                    reason: out var fallbackReason);
            Check(
                "unresolved quest room prefers its coordinate variant",
                fallbackMapId == 14999
                && fallbackReason.StartsWith(
                    "quest-variant",
                    StringComparison.Ordinal),
                ref failures);

            var flatSpecialPassiveMap = MapFile.Parse(
                "[special passive object]\n" +
                "10001 10 20 0 " +
                "10002 30 40 1\n");
            Check(
                "special passive parser keeps flat rows aligned",
                flatSpecialPassiveMap.SpecialPassiveObjects.Count == 2
                && flatSpecialPassiveMap.SpecialPassiveObjects[0].ObjectCode
                    == 10001
                && flatSpecialPassiveMap.SpecialPassiveObjects[1].ObjectCode
                    == 10002,
                ref failures);

            var extendedSpecialPassiveMap = MapFile.Parse(
                "[special passive object]\n" +
                "14056 100 200 0 2 " +
                "`[monster]` 61801 62 0 0 0 " +
                "`[monster]` 59013 62 0 1 0\n");
            var projectedSpecialPassiveActors =
                DungeonActorTemplateProjector.Project(
                    extendedSpecialPassiveMap,
                    dungeonBasicLevel: 62,
                    mapId: 1);
            Check(
                "inline special passive spawns become actor templates",
                extendedSpecialPassiveMap.SpecialPassiveObjects.Count == 1
                && extendedSpecialPassiveMap.SpecialPassiveObjects[0]
                    .Spawns.Count == 2
                && !ContainsActorType(projectedSpecialPassiveActors, 9)
                && CountActor(projectedSpecialPassiveActors, 61801) == 1
                && CountActor(projectedSpecialPassiveActors, 59013) == 1,
                ref failures);

            var monsterTeamMap = MapFile.Parse(
                "[monster]\n" +
                "57022 1 0 100 200 0 1 1 `[fixed]` `[normal]` " +
                "57054 1 0 300 200 0 1 1 `[fixed]` `[normal]`\n" +
                "[monster team]\n" +
                "100 0\n");
            var projectedMonsterTeams = DungeonActorTemplateProjector.Project(
                monsterTeamMap,
                dungeonBasicLevel: 70,
                mapId: 39118);
            Check(
                "monster team controls room-blocking ownership",
                projectedMonsterTeams.Count == 2
                && projectedMonsterTeams[0].IsBlocking
                && !projectedMonsterTeams[1].IsBlocking,
                ref failures);

            var eventPositionMap = MapFile.Parse(
                "[event monster position]\n" +
                "10 20 0 30 40 1\n");
            Check(
                "event monster positions preserve xyz triplets",
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
            Check(
                "variable NPC monster rows keep following actors aligned",
                npcBossMap.MonsterCount == 2
                && npcBossMap.Monsters.Count == 2
                && npcBossMap.Monsters[0].MonsterId == 63024
                && npcBossMap.Monsters[0].NpcId == 1020
                && npcBossMap.Monsters[0].Type == MonsterType.Boss
                && npcBossMap.Monsters[1].MonsterId == 63030
                && npcBossMap.Monsters[1].NpcId == null
                && npcBossMap.Monsters[1].Type == MonsterType.Normal,
                ref failures);

            var multilineGreedDungeon = DungeonFile.Parse(
                "[maze info]\n" +
                "[size]\n2 2\n" +
                "[greed]\n`II00\n AACC`\n");
            var multilineMaze = multilineGreedDungeon.Mazes[0];
            var greedCells = new HashSet<DungeonMazeRoomCoordinate>(
                DungeonMazeTopology.ResolveGreedCoordinates(multilineMaze));
            Check(
                "multiline two-character greed cells preserve topology",
                multilineMaze.Greed == "II00\nAACC"
                && greedCells.Contains(
                    new DungeonMazeRoomCoordinate(0, 0))
                && !greedCells.Contains(
                    new DungeonMazeRoomCoordinate(1, 0))
                && !greedCells.Contains(
                    new DungeonMazeRoomCoordinate(0, 1))
                && greedCells.Contains(
                    new DungeonMazeRoomCoordinate(1, 1)),
                ref failures);

            var linearGreedDungeon = DungeonFile.Parse(
                "[maze info]\n" +
                "[size]\n1 3\n" +
                "[greed]\n`II\nCC\nEE`\n");
            Check(
                "linear greed topology counts configured rooms",
                DungeonRoomTopology.CountConfiguredRooms(
                    linearGreedDungeon.Mazes[0]) == 3,
                ref failures);

            var passiveObjectMaze = new DungeonData.MazeSumInfo
            {
                Monsters = new List<DungeonData.MonsterSumInfo>
                {
                    new DungeonData.MonsterSumInfo
                    {
                        Code = 100,
                        Type = 0,
                        Level = 1,
                        IsBlocking = true,
                    },
                    new DungeonData.MonsterSumInfo
                    {
                        Code = 14056,
                        Type = 9,
                        Level = 1,
                        IsBlocking = false,
                    },
                },
            };
            Check(
                "passive start-map objects are not tracked monsters",
                DungeonMapHandler.CountServerTrackedMonsters(
                    passiveObjectMaze) == 1,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "DungeonMapParsingSelfTest OK"
                    : $"DungeonMapParsingSelfTest FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool ContainsActorType(
            IReadOnlyList<DungeonData.MonsterSumInfo> actors,
            byte type)
        {
            foreach (var actor in actors)
            {
                if (actor.Type == type)
                    return true;
            }

            return false;
        }

        private static int CountActor(
            IReadOnlyList<DungeonData.MonsterSumInfo> actors,
            int code)
        {
            var count = 0;
            foreach (var actor in actors)
            {
                if (actor.Code == code)
                    count++;
            }

            return count;
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                $"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
