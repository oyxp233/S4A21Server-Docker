using System.Collections.Generic;
using PvfLib;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.Dungeon
{
    internal static class NamedMonsterRoomFilter
    {
        internal static int Apply(
            DungeonInstance instance,
            DungeonFile dungeon,
            ref DungeonData.MazeSumInfo maze)
        {
            if (instance == null
                || dungeon?.NamedMonster == null
                || dungeon.NamedMonster.Length == 0
                || dungeon.NamedMonsterMapPositions == null
                || dungeon.NamedMonsterMapPositions.Count == 0
                || maze.Monsters == null
                || maze.Monsters.Count == 0)
            {
                return 0;
            }

            var namedMonsterCodes = new HashSet<int>(dungeon.NamedMonster);
            List<DungeonData.MonsterSumInfo> filtered = null;
            var removed = 0;

            for (var actorIndex = 0; actorIndex < maze.Monsters.Count; actorIndex++)
            {
                var actor = maze.Monsters[actorIndex];
                var sourceObjectIndex = actor.SourceSpecialPassiveObjectIndex;
                var remove = actor.Flag0 == 1
                    && sourceObjectIndex.HasValue
                    && sourceObjectIndex.Value >= 0
                    && sourceObjectIndex.Value < dungeon.NamedMonsterMapPositions.Count
                    && namedMonsterCodes.Contains(actor.Code)
                    && IsMappedRoomCleared(
                        instance,
                        dungeon.NamedMonsterMapPositions[sourceObjectIndex.Value]);

                if (!remove)
                {
                    filtered?.Add(actor);
                    continue;
                }

                if (filtered == null)
                {
                    filtered = new List<DungeonData.MonsterSumInfo>(maze.Monsters.Count - 1);
                    for (var previous = 0; previous < actorIndex; previous++)
                        filtered.Add(maze.Monsters[previous]);
                }

                removed++;
            }

            if (removed > 0)
                maze.Monsters = filtered;
            return removed;
        }

        private static bool IsMappedRoomCleared(
            DungeonInstance instance,
            NamedMonsterMapPosition position)
            => position != null && instance.IsRoomCleared(position.X, position.Y);
    }
}
