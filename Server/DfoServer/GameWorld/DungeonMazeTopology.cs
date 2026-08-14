using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.GameWorld
{
    internal readonly struct DungeonMazeRoomCoordinate : IEquatable<DungeonMazeRoomCoordinate>
    {
        internal DungeonMazeRoomCoordinate(int x, int y)
        {
            X = x;
            Y = y;
        }

        internal int X { get; }
        internal int Y { get; }

        public bool Equals(DungeonMazeRoomCoordinate other) =>
            X == other.X && Y == other.Y;

        public override bool Equals(object obj) =>
            obj is DungeonMazeRoomCoordinate other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 397) ^ Y;
            }
        }
    }

    internal static class DungeonMazeTopology
    {
        private static readonly object PvfCoordinateCacheLock = new object();
        private static readonly Dictionary<string, DungeonMazeRoomCoordinate[]>
            PvfCoordinateCache =
                new Dictionary<string, DungeonMazeRoomCoordinate[]>(
                    StringComparer.Ordinal);

        internal static IReadOnlyCollection<DungeonMazeRoomCoordinate>
            ResolveRoomCoordinates(
                int dungeonId,
                int mazeIndex,
                MazeInfo maze,
                bool includePvfCoordinates = true)
        {
            var cells = new HashSet<DungeonMazeRoomCoordinate>();
            if (maze == null)
                return cells;

            AddGreedCells(maze, cells);

            if (maze.MapSpecifications != null)
            {
                foreach (var specification in maze.MapSpecifications)
                {
                    cells.Add(new DungeonMazeRoomCoordinate(
                        specification.X,
                        specification.Y));
                }
            }

            if (maze.StartMap != null && maze.StartMap.Length >= 2)
            {
                cells.Add(new DungeonMazeRoomCoordinate(
                    maze.StartMap[0],
                    maze.StartMap[1]));
            }
            if (maze.BossMap != null && maze.BossMap.Length >= 2)
            {
                for (var index = 0; index + 1 < maze.BossMap.Length; index += 2)
                {
                    cells.Add(new DungeonMazeRoomCoordinate(
                        maze.BossMap[index],
                        maze.BossMap[index + 1]));
                }
            }

            if (includePvfCoordinates)
            {
                foreach (var coordinate in GetCachedPvfCoordinates(
                    dungeonId,
                    mazeIndex,
                    maze))
                {
                    if (IsWithinMazeBounds(maze, coordinate))
                        cells.Add(coordinate);
                }
            }

            return cells;
        }

        internal static IReadOnlyCollection<DungeonMazeRoomCoordinate>
            ResolveGreedCoordinates(MazeInfo maze)
        {
            var cells = new HashSet<DungeonMazeRoomCoordinate>();
            if (maze != null)
                AddGreedCells(maze, cells);
            return cells;
        }

        private static IEnumerable<DungeonMazeRoomCoordinate>
            GetCachedPvfCoordinates(
                int dungeonId,
                int mazeIndex,
                MazeInfo maze)
        {
            var key = dungeonId.ToString() + ":" + mazeIndex.ToString();
            lock (PvfCoordinateCacheLock)
            {
                if (PvfCoordinateCache.TryGetValue(key, out var cached))
                    return cached;
            }

            var coordinates = Dungeon.GetDungeonRoomCoordinates(
                    dungeonId,
                    mazeIndex,
                    maze)
                .Select(coordinate => new DungeonMazeRoomCoordinate(
                    coordinate.X,
                    coordinate.Y))
                .ToArray();

            lock (PvfCoordinateCacheLock)
                PvfCoordinateCache[key] = coordinates;

            return coordinates;
        }

        private static bool IsWithinMazeBounds(
            MazeInfo maze,
            DungeonMazeRoomCoordinate point)
        {
            if (maze.Width <= 0 || maze.Height <= 0)
                return true;

            return point.X >= 0
                && point.Y >= 0
                && point.X < maze.Width
                && point.Y < maze.Height;
        }

        private static void AddGreedCells(
            MazeInfo maze,
            HashSet<DungeonMazeRoomCoordinate> cells)
        {
            if (maze.Width <= 0
                || maze.Height <= 0
                || string.IsNullOrWhiteSpace(maze.Greed))
            {
                return;
            }

            var values = maze.Greed
                .Where(ch => !char.IsWhiteSpace(ch) && ch != '`' && ch != ',')
                .ToArray();
            var cellCount = maze.Width * maze.Height;
            var charsPerCell = values.Length >= cellCount * 2 ? 2 : 1;
            if (values.Length < cellCount * charsPerCell)
                return;

            for (var y = 0; y < maze.Height; y++)
            {
                for (var x = 0; x < maze.Width; x++)
                {
                    var valueIndex = (y * maze.Width + x) * charsPerCell;
                    var first = values[valueIndex];
                    var second = charsPerCell == 2
                        ? values[valueIndex + 1]
                        : '\0';
                    if (IsOpenGreedCell(first, second, charsPerCell))
                        cells.Add(new DungeonMazeRoomCoordinate(x, y));
                }
            }
        }

        private static bool IsOpenGreedCell(
            char first,
            char second,
            int charsPerCell)
        {
            if (charsPerCell == 2)
            {
                if ((first == 'A' && second == 'A')
                    || (first == '0' && second == '0')
                    || (first == '.' && second == '.')
                    || ((first == 'x' || first == 'X')
                        && (second == 'x' || second == 'X')))
                {
                    return false;
                }
            }

            return first != '0'
                && first != '.'
                && first != 'x'
                && first != 'X';
        }
    }
}
