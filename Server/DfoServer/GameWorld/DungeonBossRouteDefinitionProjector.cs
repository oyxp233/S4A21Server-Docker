using DfoServer.Game.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    internal static class DungeonBossRouteDefinitionProjector
    {
        private static readonly (
            DungeonBossRouteDirection Direction,
            int DeltaX,
            int DeltaY,
            int InvasionMask)[] OrderedDirections =
        {
            (DungeonBossRouteDirection.Above, 0, -1, 2),
            (DungeonBossRouteDirection.Below, 0, 1, 8),
            (DungeonBossRouteDirection.Left, -1, 0, 4),
            (DungeonBossRouteDirection.Right, 1, 0, 1),
        };

        internal static DungeonBossRouteDefinition Project(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze,
            int[] bossPosition)
        {
            if (maze == null
                || bossPosition == null
                || bossPosition.Length < 2)
            {
                return null;
            }

            var bossX = bossPosition[0];
            var bossY = bossPosition[1];
            var candidateMapIds = DungeonMapResolver
                .GetExplicitBossCandidateMapIds(maze, bossX, bossY);
            if (candidateMapIds.Count < 2 || candidateMapIds.Count > 16
                || !DungeonMapResolver.TryGetMazeCellGreed(
                    maze,
                    bossX,
                    bossY,
                    out var bossGreed)
                || !DungeonMapResolver.TryDecodeGreedSymbol(
                    bossGreed,
                    out var bossPathMask))
            {
                return null;
            }

            var entranceMasks = new Dictionary<int, int>();
            foreach (var mapId in candidateMapIds)
            {
                if (!DungeonMapResolver.TryGetMapEntranceMask(
                    mapId,
                    out var entranceMask))
                {
                    return null;
                }
                entranceMasks[mapId] = entranceMask;
            }

            var configuredRooms = new HashSet<DungeonMazeRoomCoordinate>(
                DungeonMazeTopology.ResolveRoomCoordinates(
                    dungeonId,
                    mazeIndex,
                    maze));
            var entrances = new List<(
                DungeonBossRouteDirection Direction,
                int X,
                int Y,
                int InvasionMask)>();
            foreach (var direction in OrderedDirections)
            {
                if ((bossPathMask & direction.InvasionMask) == 0)
                    continue;

                var sourceX = bossX + direction.DeltaX;
                var sourceY = bossY + direction.DeltaY;
                if (configuredRooms.Contains(
                        new DungeonMazeRoomCoordinate(sourceX, sourceY)))
                {
                    entrances.Add((
                        direction.Direction,
                        sourceX,
                        sourceY,
                        direction.InvasionMask));
                }
            }

            if (entrances.Count < 2)
                return null;

            var routes = new List<DungeonBossRouteEntryDefinition>();
            var candidateSets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entrance in entrances)
            {
                var accepted = new List<int>();
                foreach (var mapId in candidateMapIds)
                {
                    if ((entranceMasks[mapId] & entrance.InvasionMask) == 0)
                        continue;

                    accepted.Add(mapId);
                    routes.Add(new DungeonBossRouteEntryDefinition(
                        entrance.Direction,
                        entrance.X,
                        entrance.Y,
                        mapId));
                }

                if (accepted.Count == 0)
                    return null;
                candidateSets.Add(string.Join(",", accepted));
            }

            // If every entrance accepts the same pool, the existing frozen random
            // fallback is already equivalent and no route runtime is required.
            if (candidateSets.Count < 2)
                return null;

            return new DungeonBossRouteDefinition(bossX, bossY, routes);
        }
    }
}
