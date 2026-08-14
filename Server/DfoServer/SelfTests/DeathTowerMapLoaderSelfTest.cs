using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.Game.DeathTower;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class DeathTowerMapLoaderSelfTest
    {
        private const int DeathTowerDungeonId = 11000;

        private static readonly Dictionary<int, RandomFloorExpectation> RandomFloors =
            new Dictionary<int, RandomFloorExpectation>
            {
                { 7, new RandomFloorExpectation(150, 4) },
                { 13, new RandomFloorExpectation(250, 2) },
                { 19, new RandomFloorExpectation(300, 3) },
                { 24, new RandomFloorExpectation(400, 4) },
                { 35, new RandomFloorExpectation(600, 4) },
                { 38, new RandomFloorExpectation(400, 4) },
                { 39, new RandomFloorExpectation(400, 4) },
                { 40, new RandomFloorExpectation(400, 4) },
                { 41, new RandomFloorExpectation(500, 4) },
                { 42, new RandomFloorExpectation(600, 4) },
                { 43, new RandomFloorExpectation(700, 4) },
                { 44, new RandomFloorExpectation(800, 4) },
                { 45, new RandomFloorExpectation(900, 4) },
            };

        public static int Run()
        {
            Console.WriteLine("=== DEATH_TOWER_MAP_LOADER selftest ===");

            var failures = 0;
            var config = DeathTowerData.GetConfig(DeathTowerDungeonId);
            Check("death tower 11000 has all 45 floors",
                config != null && config.StageMapIds != null && config.StageMapIds.Count == 45,
                ref failures);
            if (config == null || config.StageMapIds == null || config.StageMapIds.Count != 45)
                return Finish(failures);

            var mapList = LstFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", "map.lst")));
            var aiList = LstFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("AICharacter", "AICharacter.lst")));
            var appearancePoints = LoadAppearancePoints(aiList);

            for (var stage = 0; stage < config.StageMapIds.Count; stage++)
            {
                var floor = stage + 1;
                var tower = CreateTowerAtStage(config, stage);
                var actual = DeathTowerMapLoader.LoadStageMonsters(tower);
                var map = LoadMap(mapList, config.StageMapIds[stage]);

                Check($"floor {floor} has a stage population", actual.Count > 0, ref failures);
                Check($"floor {floor} has only valid wire records",
                    actual.All(monster => monster.MonsterIndex > 0
                        && monster.MonsterLevel > 0
                        && ((monster.MonsterType <= 3) || (monster.MonsterType >= 5 && monster.MonsterType <= 8))),
                    ref failures);
                Check($"floor {floor} ordinary monsters match MapFile",
                    BuildExpectedMonsterMultiset(map, config.BasisLevel)
                        .SequenceEqual(BuildActualMonsterMultiset(actual)),
                    ref failures);
                Check($"floor {floor} fixed APCs match MapFile and AIC",
                    BuildExpectedFixedApcMultiset(map, aiList)
                        .SequenceEqual(BuildActualFixedApcMultiset(actual)),
                    ref failures);

                if (RandomFloors.TryGetValue(floor, out var randomExpectation))
                {
                    var randomApcs = actual
                        .Where(monster => monster.ListIndex >= 64)
                        .ToList();
                    var randomIds = randomApcs.Select(monster => monster.MonsterIndex).ToList();
                    var allCandidatesKnown = randomIds.All(appearancePoints.ContainsKey);
                    var spentPoints = allCandidatesKnown
                        ? randomIds.Sum(id => appearancePoints[id])
                        : int.MaxValue;

                    Check($"floor {floor} creates random APCs", randomApcs.Count > 0, ref failures);
                    Check($"floor {floor} random APC count respects spawn slots",
                        randomApcs.Count <= randomExpectation.SpawnSlots,
                        ref failures);
                    Check($"floor {floor} random APC candidates are unique",
                        randomIds.Distinct().Count() == randomIds.Count,
                        ref failures);
                    Check($"floor {floor} random APC point budget is respected",
                        allCandidatesKnown && spentPoints <= randomExpectation.PointBudget,
                        ref failures);
                }
                else
                {
                    Check($"floor {floor} has no unexpected random APCs",
                        actual.All(monster => monster.ListIndex < 64),
                        ref failures);
                }
            }

            var floor42 = CreateTowerAtStage(config, 41);
            var floor42Monsters = DeathTowerMapLoader.LoadStageMonsters(floor42);
            Check("floor 42 contains fixed APC 20603 at AIC level 25",
                floor42Monsters.Any(monster => monster.ListIndex < 64
                    && monster.MonsterIndex == 20603
                    && monster.MonsterLevel == 25
                    && monster.MonsterType == 5),
                ref failures);
            Check("floor 42 excludes legacy bogus monster ids",
                floor42Monsters.All(monster => monster.MonsterIndex != 0
                    && monster.MonsterIndex != 4
                    && monster.MonsterIndex != 448),
                ref failures);

            return Finish(failures);
        }

        private static DeathTowerSession CreateTowerAtStage(
            DeathTowerData.TowerConfig config,
            int targetStage)
        {
            var tower = new DeathTowerSession(config);
            while (tower.CurrentStage < targetStage)
            {
                tower.SetFighting();
                if (!tower.TryAdvanceStage())
                    throw new InvalidOperationException($"Unable to advance tower to stage {targetStage}.");
            }
            return tower;
        }

        private static MapFile LoadMap(LstFile mapList, int mapId)
        {
            var entry = mapList.GetById(mapId);
            if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                throw new InvalidOperationException($"Map {mapId} is missing from map.lst.");
            return MapFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", entry.FilePath)));
        }

        private static Dictionary<int, int> LoadAppearancePoints(LstFile aiList)
        {
            var result = new Dictionary<int, int>();
            foreach (var entry in aiList.Entries)
            {
                var aic = LoadAic(entry);
                var point = ParseFirstInt(aic.AppearancePoint);
                if (point > 0 && TryParseAicLevel(aic, out _))
                    result[entry.Id] = point;
            }
            return result;
        }

        private static string[] BuildExpectedMonsterMultiset(MapFile map, int basisLevel)
        {
            return map.Monsters
                .Where(monster => monster.MonsterId.GetValueOrDefault() > 0)
                .Select(monster =>
                {
                    var rawLevel = monster.Lv.GetValueOrDefault() != 0
                        ? basisLevel + monster.AutoLv.GetValueOrDefault()
                        : monster.AutoLv.GetValueOrDefault();
                    var level = Math.Max(1, Math.Min(255, rawLevel));
                    var type = (byte)monster.Type;
                    if (type > 3)
                        type = 0;
                    return $"{monster.MonsterId.Value}:{level}:{type}";
                })
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        private static string[] BuildActualMonsterMultiset(IEnumerable<StageMonster> monsters)
        {
            return monsters
                .Where(monster => monster.MonsterType <= 3)
                .Select(monster => $"{monster.MonsterIndex}:{monster.MonsterLevel}:{monster.MonsterType}")
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        private static string[] BuildExpectedFixedApcMultiset(MapFile map, LstFile aiList)
        {
            return map.AICharacters
                .Where(apc => apc.Code > 0)
                .Select(apc =>
                {
                    var entry = aiList.GetById(apc.Code);
                    if (entry == null || !TryParseAicLevel(LoadAic(entry), out var level))
                        return $"INVALID:{apc.Code}";
                    var type = (byte)apc.AIType;
                    if (type < 5 || type > 8)
                        type = 5;
                    return $"{apc.Code}:{level}:{type}";
                })
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        private static string[] BuildActualFixedApcMultiset(IEnumerable<StageMonster> monsters)
        {
            return monsters
                .Where(monster => monster.ListIndex < 64 && monster.MonsterType >= 5)
                .Select(monster => $"{monster.MonsterIndex}:{monster.MonsterLevel}:{monster.MonsterType}")
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        private static AiConfigFile LoadAic(LstEntry entry)
        {
            return AiConfigFile.Parse(PvfArchiveAccessor.ReadText(
                Path.Combine("AICharacter", entry.FilePath)));
        }

        private static bool TryParseAicLevel(AiConfigFile aic, out byte level)
        {
            level = 0;
            var match = Regex.Match(aic.MinimumInfo ?? string.Empty,
                @"`[^`]*`\s+\d+\s+\d+\s+\d+\s+\d+\s+(\d+)");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var parsed)
                || parsed <= 0 || parsed > byte.MaxValue)
                return false;
            level = (byte)parsed;
            return true;
        }

        private static int ParseFirstInt(string value)
        {
            var match = Regex.Match(value ?? string.Empty, @"-?\d+");
            return match.Success && int.TryParse(match.Value, out var parsed) ? parsed : 0;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private static int Finish(int failures)
        {
            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private readonly struct RandomFloorExpectation
        {
            public RandomFloorExpectation(int pointBudget, int spawnSlots)
            {
                PointBudget = pointBudget;
                SpawnSlots = spawnSlots;
            }

            public int PointBudget { get; }
            public int SpawnSlots { get; }
        }
    }
}
