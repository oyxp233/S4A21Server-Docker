using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal static class ResolvedRoomTemplateProvider
    {
        private static readonly ConcurrentDictionary<RoomTemplateKey, FrozenRoomTemplate>
            RoomTemplates =
                new ConcurrentDictionary<RoomTemplateKey, FrozenRoomTemplate>();

        internal static Dungeon.MazeSumInfo Resolve(
            int dungeonId,
            int x,
            int y,
            int mazeIndex,
            int overrideMapId,
            int[] bossPosition)
        {
            var dungeonBasicLevel = DungeonCatalog.GetBasicLevel(dungeonId);
            var dungeonFile = DungeonCatalog.GetDungeonFile(dungeonId);
            MazeInfo maze;
            if (mazeIndex >= 0
                && dungeonFile.Mazes != null
                && mazeIndex < dungeonFile.Mazes.Count)
            {
                maze = dungeonFile.Mazes[mazeIndex];
            }
            else
            {
                maze = DungeonCatalog.GetDefaultMaze(dungeonId);
            }

            if (x == 0xFF && y == 0xFF)
            {
                x = maze.StartMap[0];
                y = maze.StartMap[1];
            }

            var mapId = overrideMapId > 0
                ? overrideMapId
                : DungeonMapResolver.ResolveMapId(
                    dungeonId,
                    x,
                    y,
                    maze,
                    mazeIndex,
                    bossPosition);
            if (mapId == -1)
            {
                FileLogger.Log(
                    $"[Dungeon] GetDungeonMapMonsterSummaryInformation WARNING: " +
                    $"no map resolved for dungeon={dungeonId} maze={mazeIndex} " +
                    $"room=({x},{y})");
                return new Dungeon.MazeSumInfo
                {
                    X = x,
                    Y = y,
                    Index = 0,
                    Monsters = new List<Dungeon.MonsterSumInfo>(),
                };
            }

            // Preserve the legacy facade's error wording for missing room MAPs.
            _ = DungeonCatalog.ResolveFilePath(
                DungeonMapCatalog.LoadMapList(),
                mapId,
                "门");
            var key = new RoomTemplateKey(mapId, dungeonBasicLevel);
            var template = RoomTemplates.GetOrAdd(
                key,
                value => new FrozenRoomTemplate(
                    value.MapId,
                    value.DungeonBasicLevel,
                    DungeonMapCatalog.GetMapFile(value.MapId)));
            return template.CreateResolved(x, y);
        }

        internal static List<Dungeon.MonsterSumInfo>
            GetMonsterConditionActors(
                int mapId,
                int dungeonId,
                int x,
                int y,
                ICollection<int> monsterCodes)
        {
            return GetConditionalActors(
                mapId,
                dungeonId,
                x,
                y,
                monsterCodes,
                conditionalSummon: false);
        }

        internal static List<Dungeon.MonsterSumInfo>
            GetConditionalSummonActors(
                int mapId,
                int dungeonId,
                int x,
                int y,
                ICollection<int> monsterCodes)
        {
            return GetConditionalActors(
                mapId,
                dungeonId,
                x,
                y,
                monsterCodes,
                conditionalSummon: true);
        }

        private static List<Dungeon.MonsterSumInfo> GetConditionalActors(
            int mapId,
            int dungeonId,
            int x,
            int y,
            ICollection<int> monsterCodes,
            bool conditionalSummon)
        {
            if (mapId <= 0 || monsterCodes == null || monsterCodes.Count == 0)
                return new List<Dungeon.MonsterSumInfo>();

            try
            {
                var mapFile = DungeonMapCatalog.GetMapFile(mapId);
                return DungeonActorTemplateProjector.ProjectConditional(
                    conditionalSummon
                        ? mapFile.ConditionalSummonMonsters
                        : mapFile.MonsterConditionMonsters,
                    DungeonCatalog.GetBasicLevel(dungeonId),
                    monsterCodes,
                    conditionalSummon);
            }
            catch (Exception ex)
            {
                var kind = conditionalSummon
                    ? "conditional summon"
                    : "monster condition";
                FileLogger.Log(
                    $"[Dungeon] {kind} load failed: dungeon={dungeonId} " +
                    $"room=({x},{y}) map={mapId}: {ex.Message}");
                return new List<Dungeon.MonsterSumInfo>();
            }
        }

        private static EventMonsterPositionInfo Clone(
            EventMonsterPositionInfo source)
        {
            return source == null
                ? null
                : new EventMonsterPositionInfo
                {
                    X = source.X,
                    Y = source.Y,
                    Z = source.Z,
                };
        }

        private static SpecialPassiveObjectInfo Clone(
            SpecialPassiveObjectInfo source)
        {
            if (source == null)
                return null;

            var clone = new SpecialPassiveObjectInfo
            {
                ObjectCode = source.ObjectCode,
                X = source.X,
                Y = source.Y,
                Flags = source.Flags,
            };
            if (source.Spawns != null)
            {
                foreach (var spawn in source.Spawns)
                {
                    if (spawn == null)
                        continue;
                    clone.Spawns.Add(new SpecialPassiveObjectSpawnInfo
                    {
                        Kind = spawn.Kind,
                        Code = spawn.Code,
                        Level = spawn.Level,
                        Param0 = spawn.Param0,
                        Param1 = spawn.Param1,
                        Param2 = spawn.Param2,
                    });
                }
            }

            if (source.HellPartyEntries != null)
            {
                foreach (var entry in source.HellPartyEntries)
                {
                    if (entry == null)
                        continue;
                    clone.HellPartyEntries.Add(new HellPartyMapEntry
                    {
                        GroupId = entry.GroupId,
                        Rate = entry.Rate,
                        Order = entry.Order,
                    });
                }
            }

            return clone;
        }

        private readonly struct RoomTemplateKey : IEquatable<RoomTemplateKey>
        {
            internal RoomTemplateKey(int mapId, byte dungeonBasicLevel)
            {
                MapId = mapId;
                DungeonBasicLevel = dungeonBasicLevel;
            }

            internal int MapId { get; }
            internal byte DungeonBasicLevel { get; }

            public bool Equals(RoomTemplateKey other) =>
                MapId == other.MapId
                && DungeonBasicLevel == other.DungeonBasicLevel;

            public override bool Equals(object obj) =>
                obj is RoomTemplateKey other && Equals(other);

            public override int GetHashCode() =>
                HashCode.Combine(MapId, DungeonBasicLevel);
        }

        private sealed class FrozenRoomTemplate
        {
            private readonly Dungeon.MonsterSumInfo[] _actors;
            private readonly EventMonsterPositionInfo[] _eventMonsterPositions;
            private readonly SpecialPassiveObjectInfo[] _specialPassiveObjects;

            internal FrozenRoomTemplate(
                int mapId,
                byte dungeonBasicLevel,
                MapFile mapFile)
            {
                MapId = mapId;
                _actors = DungeonActorTemplateProjector.Project(
                    mapFile,
                    dungeonBasicLevel,
                    mapId).ToArray();

                var eventPositions = mapFile.EventMonsterPositions
                    ?? new List<EventMonsterPositionInfo>();
                _eventMonsterPositions = new EventMonsterPositionInfo[
                    eventPositions.Count];
                for (var index = 0; index < eventPositions.Count; index++)
                    _eventMonsterPositions[index] = Clone(eventPositions[index]);

                var specialObjects = mapFile.SpecialPassiveObjects
                    ?? new List<SpecialPassiveObjectInfo>();
                _specialPassiveObjects = new SpecialPassiveObjectInfo[
                    specialObjects.Count];
                for (var index = 0; index < specialObjects.Count; index++)
                    _specialPassiveObjects[index] = Clone(specialObjects[index]);
            }

            private int MapId { get; }

            internal Dungeon.MazeSumInfo CreateResolved(int x, int y)
            {
                var eventPositions = new EventMonsterPositionInfo[
                    _eventMonsterPositions.Length];
                for (var index = 0;
                     index < _eventMonsterPositions.Length;
                     index++)
                {
                    eventPositions[index] = Clone(
                        _eventMonsterPositions[index]);
                }

                var specialObjects = new SpecialPassiveObjectInfo[
                    _specialPassiveObjects.Length];
                for (var index = 0;
                     index < _specialPassiveObjects.Length;
                     index++)
                {
                    specialObjects[index] = Clone(
                        _specialPassiveObjects[index]);
                }

                return new Dungeon.MazeSumInfo
                {
                    X = x,
                    Y = y,
                    Index = MapId,
                    Monsters = new List<Dungeon.MonsterSumInfo>(_actors),
                    EventMonsterPositions =
                        new ReadOnlyCollection<EventMonsterPositionInfo>(
                            eventPositions),
                    SpecialPassiveObjects =
                        new ReadOnlyCollection<SpecialPassiveObjectInfo>(
                            specialObjects),
                };
            }
        }
    }
}
