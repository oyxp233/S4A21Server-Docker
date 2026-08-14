using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonBossRouteDirection : byte
    {
        Above = 0,
        Below = 1,
        Left = 2,
        Right = 3,
    }

    internal readonly struct DungeonBossRouteEntryDefinition
    {
        internal DungeonBossRouteEntryDefinition(
            DungeonBossRouteDirection direction,
            int sourceX,
            int sourceY,
            int mapId)
        {
            Direction = direction;
            SourceX = sourceX;
            SourceY = sourceY;
            MapId = mapId;
        }

        internal DungeonBossRouteDirection Direction { get; }
        internal int SourceX { get; }
        internal int SourceY { get; }
        internal int MapId { get; }
    }

    internal sealed class DungeonBossRouteDefinition
    {
        internal DungeonBossRouteDefinition(
            int bossX,
            int bossY,
            IReadOnlyList<DungeonBossRouteEntryDefinition> routes)
        {
            if (routes == null || routes.Count < 2 || routes.Count > 64)
                throw new ArgumentException(
                    "A route-bound Boss requires two to sixty-four route candidates.",
                    nameof(routes));

            var copy = new DungeonBossRouteEntryDefinition[routes.Count];
            var routeKeys = new HashSet<(int X, int Y, int MapId)>();
            for (var index = 0; index < routes.Count; index++)
            {
                var route = routes[index];
                if (route.MapId <= 0
                    || !routeKeys.Add((
                        route.SourceX,
                        route.SourceY,
                        route.MapId)))
                {
                    throw new ArgumentException(
                        "Boss routes require positive, unique source/MAP pairs.",
                        nameof(routes));
                }
                copy[index] = route;
            }

            BossX = bossX;
            BossY = bossY;
            Routes = new ReadOnlyCollection<DungeonBossRouteEntryDefinition>(copy);
        }

        internal int BossX { get; }
        internal int BossY { get; }
        internal IReadOnlyList<DungeonBossRouteEntryDefinition> Routes { get; }

        internal bool IsBossRoom(int x, int y) => x == BossX && y == BossY;

        internal bool ContainsMapId(int mapId)
        {
            foreach (var route in Routes)
            {
                if (route.MapId == mapId)
                    return true;
            }
            return false;
        }

        internal bool TryResolveCandidates(
            int sourceX,
            int sourceY,
            int targetX,
            int targetY,
            out DungeonBossRouteDirection direction,
            out IReadOnlyList<int> mapIds)
        {
            if (IsBossRoom(targetX, targetY))
            {
                var candidates = new List<int>();
                var seen = new HashSet<int>();
                DungeonBossRouteDirection? matchedDirection = null;
                foreach (var candidate in Routes)
                {
                    if (candidate.SourceX == sourceX
                        && candidate.SourceY == sourceY)
                    {
                        matchedDirection ??= candidate.Direction;
                        if (seen.Add(candidate.MapId))
                            candidates.Add(candidate.MapId);
                    }
                }

                if (matchedDirection.HasValue && candidates.Count > 0)
                {
                    direction = matchedDirection.Value;
                    mapIds = new ReadOnlyCollection<int>(candidates);
                    return true;
                }
            }

            direction = default;
            mapIds = Array.Empty<int>();
            return false;
        }
    }

    internal sealed class DungeonBossRouteRuntime
    {
        private readonly object _syncRoot = new object();
        private int _selectedMapId;
        private DungeonBossRouteDirection? _selectedDirection;
        private bool _selectedByFallback;

        internal DungeonBossRouteRuntime(
            DungeonBossRouteDefinition definition,
            int fallbackMapId)
        {
            Definition = definition
                ?? throw new ArgumentNullException(nameof(definition));
            if (!definition.ContainsMapId(fallbackMapId))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fallbackMapId),
                    "The fallback MAP must belong to the route candidate pool.");
            }
            FallbackMapId = fallbackMapId;
        }

        internal DungeonBossRouteDefinition Definition { get; }
        internal int FallbackMapId { get; }
        internal int SelectedMapId
        {
            get
            {
                lock (_syncRoot)
                    return _selectedMapId;
            }
        }
        internal DungeonBossRouteDirection? SelectedDirection
        {
            get
            {
                lock (_syncRoot)
                    return _selectedDirection;
            }
        }
        internal bool SelectedByFallback
        {
            get
            {
                lock (_syncRoot)
                    return _selectedByFallback;
            }
        }

        internal bool TrySelectForMove(
            int sourceX,
            int sourceY,
            int targetX,
            int targetY,
            Func<int, int> selectIndex,
            out int selectedMapId,
            out bool transitioned)
        {
            selectedMapId = 0;
            transitioned = false;
            if (!Definition.IsBossRoom(targetX, targetY))
                return false;

            lock (_syncRoot)
            {
                if (_selectedMapId > 0)
                {
                    selectedMapId = _selectedMapId;
                    return true;
                }

                if (!Definition.TryResolveCandidates(
                        sourceX,
                        sourceY,
                        targetX,
                        targetY,
                        out var direction,
                        out var candidates))
                {
                    return false;
                }

                var selectedIndex = candidates.Count == 1
                    ? 0
                    : (selectIndex
                        ?? throw new ArgumentNullException(nameof(selectIndex)))(
                            candidates.Count);
                if (selectedIndex < 0 || selectedIndex >= candidates.Count)
                    throw new InvalidOperationException(
                        "Boss route selector returned an invalid index.");

                _selectedMapId = candidates[selectedIndex];
                _selectedDirection = direction;
                selectedMapId = _selectedMapId;
                transitioned = true;
                return true;
            }
        }

        internal int ResolveForStartMap(out bool fallbackCommitted)
        {
            lock (_syncRoot)
            {
                fallbackCommitted = false;
                if (_selectedMapId > 0)
                    return _selectedMapId;

                _selectedMapId = FallbackMapId;
                _selectedByFallback = true;
                fallbackCommitted = true;
                return _selectedMapId;
            }
        }
    }
}
