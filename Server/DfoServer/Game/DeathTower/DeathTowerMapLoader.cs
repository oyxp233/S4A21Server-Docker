using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.DeathTower
{
    public static class DeathTowerMapLoader
    {
        private const int ApcRandomListIndexStart = 64;

        private static readonly Regex FirstIntegerRegex =
            new Regex(@"-?\d+", RegexOptions.Compiled);
        private static readonly Regex ApcLevelRegex =
            new Regex(@"`[^`]*`\s+\d+\s+\d+\s+\d+\s+\d+\s+(\d+)", RegexOptions.Compiled);
        private static readonly object ApcCacheLock = new object();
        private static readonly Dictionary<int, ApcDefinition> ApcDefinitions =
            new Dictionary<int, ApcDefinition>();
        private static readonly HashSet<int> InvalidApcCodes = new HashSet<int>();

        private static LstFile _aiCharacterList;
        private static List<ApcDefinition> _randomApcCandidates;

        public static List<StageMonster> LoadStageMonsters(DeathTowerSession tower)
        {
            var mapId = tower.GetCurrentMapId();
            var result = new List<StageMonster>();
            if (mapId <= 0)
            {
                FileLogger.Log($"[DeathTower] LoadStageMonsters: invalid mapId={mapId} for stage={tower.CurrentStage}");
                return result;
            }

            try
            {
                var mapContent = ReadMapContent(mapId);
                if (mapContent == null)
                {
                    FileLogger.Log($"[DeathTower] LoadStageMonsters: map file not found for mapId={mapId}");
                    return result;
                }

                var map = MapFile.Parse(mapContent);
                AppendOrdinaryMonsters(result, tower, map, mapId);
                AppendFixedApcs(result, tower, map, mapId);
                AppendRandomApcs(result, tower, map, mapId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DeathTower] LoadStageMonsters failed mapId={mapId}: {ex.Message}");
            }

            return result;
        }

        public static List<StageTowerItem> LoadStageItems(
            DeathTowerSession tower,
            IReadOnlyList<StageMonster> monsters)
        {
            var result = new List<StageTowerItem>();
            if (tower == null || monsters == null || !tower.Config.ItemDropsEnabled)
                return result;

            foreach (var monster in monsters)
            {
                if (monster.MonsterType < 5
                    || monster.MonsterType > 8
                    || !TryGetApcDefinition(monster.MonsterIndex, out var definition))
                {
                    continue;
                }

                foreach (var configuredItem in definition.DeathTowerItems)
                {
                    if (result.Count >= byte.MaxValue)
                    {
                        FileLogger.Log($"[DeathTower] Stage item list truncated to {byte.MaxValue}: stage={tower.CurrentStage} map={tower.GetCurrentMapId()}");
                        return result;
                    }
                    if (configuredItem.ItemId <= 0 || configuredItem.DropRate < 0)
                    {
                        FileLogger.Log($"[DeathTower] Skip invalid APC tower item: apc={monster.MonsterIndex} item={configuredItem.ItemId} rate={configuredItem.DropRate}");
                        continue;
                    }

                    result.Add(new StageTowerItem
                    {
                        SourceListIndex = monster.ListIndex,
                        SourceMonsterUniqueId = monster.MonsterUniqueId,
                        ItemUniqueId = tower.NextItemSeq(),
                        ItemId = configuredItem.ItemId,
                        DropRate = configuredItem.DropRate,
                        StackCount = 1,
                    });
                }
            }

            return result;
        }

        private static void AppendOrdinaryMonsters(
            ICollection<StageMonster> result,
            DeathTowerSession tower,
            MapFile map,
            int mapId)
        {
            var listIndex = 0;
            foreach (var monster in map.Monsters)
            {
                var monsterCode = monster.MonsterId.GetValueOrDefault();
                if (monsterCode <= 0)
                {
                    FileLogger.Log($"[DeathTower] Skip invalid monster code={monsterCode} map={mapId}");
                    continue;
                }

                var rawLevel = monster.Lv.GetValueOrDefault() != 0
                    ? tower.Config.BasisLevel + monster.AutoLv.GetValueOrDefault()
                    : monster.AutoLv.GetValueOrDefault();
                var level = rawLevel > 0 ? rawLevel : tower.Config.BasisLevel;
                var type = (byte)monster.Type;
                if (type > 3)
                {
                    FileLogger.Log($"[DeathTower] Clamp monster type={type} to 0 code={monsterCode} map={mapId}");
                    type = 0;
                }

                result.Add(new StageMonster
                {
                    ListIndex = listIndex++,
                    MonsterUniqueId = tower.NextMonsterSeq(),
                    MonsterIndex = monsterCode,
                    MonsterLevel = ClampLevel(level),
                    MonsterType = type,
                    IsBoxMonster = 0,
                    BoxIndex = 0,
                });
            }
        }

        private static void AppendFixedApcs(
            ICollection<StageMonster> result,
            DeathTowerSession tower,
            MapFile map,
            int mapId)
        {
            var listIndex = 0;
            foreach (var apc in map.AICharacters)
            {
                if (apc.Code <= 0 || !TryGetApcDefinition(apc.Code, out var definition))
                {
                    FileLogger.Log($"[DeathTower] Skip APC code={apc.Code} without valid AIC map={mapId}");
                    continue;
                }

                var type = (byte)apc.AIType;
                if (type < 5 || type > 8)
                {
                    FileLogger.Log($"[DeathTower] Clamp APC type={type} to 5 code={apc.Code} map={mapId}");
                    type = 5;
                }

                result.Add(new StageMonster
                {
                    ListIndex = listIndex++,
                    MonsterUniqueId = tower.NextMonsterSeq(),
                    MonsterIndex = apc.Code,
                    MonsterLevel = definition.Level,
                    MonsterType = type,
                    IsBoxMonster = 0,
                    BoxIndex = 0,
                });
            }
        }

        private static void AppendRandomApcs(
            ICollection<StageMonster> result,
            DeathTowerSession tower,
            MapFile map,
            int mapId)
        {
            var pointBudget = ReadNodeFirstInteger(map, "apc random point");
            if (pointBudget <= 0)
                return;

            var spawnSlots = ReadNodeFirstInteger(map, "monster spawn pos");
            if (spawnSlots <= 0)
            {
                FileLogger.Log($"[DeathTower] Random APC budget has no spawn slots map={mapId} budget={pointBudget}");
                return;
            }

            var remainingPoints = pointBudget;
            var available = new List<ApcDefinition>(GetRandomApcCandidates());
            var selectedCount = 0;

            while (selectedCount < spawnSlots)
            {
                var eligible = available
                    .Where(candidate => candidate.AppearancePoint <= remainingPoints)
                    .ToList();
                if (eligible.Count == 0)
                    break;

                var selected = eligible[Infrastructure.ServerRandom.Next(eligible.Count)];
                available.Remove(selected);
                remainingPoints -= selected.AppearancePoint;

                result.Add(new StageMonster
                {
                    ListIndex = ApcRandomListIndexStart + selectedCount,
                    MonsterUniqueId = tower.NextMonsterSeq(),
                    MonsterIndex = selected.Code,
                    MonsterLevel = selected.Level,
                    MonsterType = 5,
                    IsBoxMonster = 0,
                    BoxIndex = 0,
                });
                selectedCount++;
            }

            FileLogger.Log($"[DeathTower] Random APC map={mapId} selected={selectedCount}/{spawnSlots} points={pointBudget - remainingPoints}/{pointBudget}");
        }

        private static int ReadNodeFirstInteger(MapFile map, string tag)
        {
            var node = map.Root?.GetChild(tag);
            if (node == null || node.DataItems.Count == 0)
                return 0;

            var match = FirstIntegerRegex.Match(node.GetFirstDataContent(map.Content) ?? string.Empty);
            return match.Success && int.TryParse(match.Value, out var value) ? value : 0;
        }

        private static IReadOnlyList<ApcDefinition> GetRandomApcCandidates()
        {
            lock (ApcCacheLock)
            {
                if (_randomApcCandidates != null)
                    return _randomApcCandidates;

                EnsureAiCharacterList();
                var candidates = new List<ApcDefinition>();
                foreach (var entry in _aiCharacterList.Entries)
                {
                    if (TryLoadApcDefinitionLocked(entry.Id, out var definition)
                        && definition.AppearancePoint > 0)
                    {
                        candidates.Add(definition);
                    }
                }

                _randomApcCandidates = candidates;
                FileLogger.Log($"[DeathTower] Loaded random APC candidates={candidates.Count}");
                return _randomApcCandidates;
            }
        }

        private static bool TryGetApcDefinition(int code, out ApcDefinition definition)
        {
            lock (ApcCacheLock)
            {
                return TryLoadApcDefinitionLocked(code, out definition);
            }
        }

        private static bool TryLoadApcDefinitionLocked(int code, out ApcDefinition definition)
        {
            if (ApcDefinitions.TryGetValue(code, out definition))
                return true;
            if (InvalidApcCodes.Contains(code))
                return false;

            try
            {
                EnsureAiCharacterList();
                var entry = _aiCharacterList.GetById(code);
                if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                {
                    InvalidApcCodes.Add(code);
                    return false;
                }

                var content = PvfArchiveAccessor.ReadText(Path.Combine("AICharacter", entry.FilePath));
                var aic = AiConfigFile.Parse(content);
                var levelMatch = ApcLevelRegex.Match(aic.MinimumInfo ?? string.Empty);
                if (!levelMatch.Success
                    || !int.TryParse(levelMatch.Groups[1].Value, out var parsedLevel)
                    || parsedLevel <= 0
                    || parsedLevel > byte.MaxValue)
                {
                    InvalidApcCodes.Add(code);
                    return false;
                }

                definition = new ApcDefinition(
                    code,
                    (byte)parsedLevel,
                    ParseFirstInteger(aic.AppearancePoint),
                    aic.DeathTowerItems.ToArray());
                ApcDefinitions[code] = definition;
                return true;
            }
            catch (Exception ex)
            {
                InvalidApcCodes.Add(code);
                FileLogger.Log($"[DeathTower] Failed to load AIC code={code}: {ex.Message}");
                definition = null;
                return false;
            }
        }

        private static void EnsureAiCharacterList()
        {
            if (_aiCharacterList == null)
            {
                _aiCharacterList = LstFile.Parse(
                    PvfArchiveAccessor.ReadText(Path.Combine("AICharacter", "AICharacter.lst")));
            }
        }

        private static int ParseFirstInteger(string value)
        {
            var match = FirstIntegerRegex.Match(value ?? string.Empty);
            return match.Success && int.TryParse(match.Value, out var parsed) ? parsed : 0;
        }

        private static byte ClampLevel(int level)
        {
            return (byte)Math.Max(1, Math.Min(byte.MaxValue, level));
        }

        private static string ReadMapContent(int mapId)
        {
            var list = LstFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", "map.lst")));
            var entry = list.GetById(mapId);
            if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                return null;
            return PvfArchiveAccessor.ReadText(Path.Combine("map", entry.FilePath));
        }

        private sealed class ApcDefinition
        {
            public ApcDefinition(
                int code,
                byte level,
                int appearancePoint,
                IReadOnlyList<AiDeathTowerItem> deathTowerItems)
            {
                Code = code;
                Level = level;
                AppearancePoint = appearancePoint;
                DeathTowerItems = deathTowerItems ?? Array.Empty<AiDeathTowerItem>();
            }

            public int Code { get; }
            public byte Level { get; }
            public int AppearancePoint { get; }
            public IReadOnlyList<AiDeathTowerItem> DeathTowerItems { get; }
        }
    }
}
