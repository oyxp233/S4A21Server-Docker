using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal static class DungeonMinimapProjectionService
    {
        internal static IReadOnlyList<IReadOnlyList<(byte, byte)>> Resolve(
            IReadOnlyList<IReadOnlyList<(byte, byte)>> mechanismGroups,
            IReadOnlyList<RidableObjectSpawnEntry> randomizedObjects)
        {
            if (mechanismGroups != null && mechanismGroups.Count > 0)
                return mechanismGroups;
            if (randomizedObjects == null || randomizedObjects.Count == 0)
                return null;

            var groups = new SortedDictionary<int, List<(byte, byte)>>();
            var seenByGroup = new Dictionary<int, HashSet<int>>();
            foreach (var entry in randomizedObjects)
            {
                if (!entry.HasMinimapIcon || entry.MinimapGroupIndex < 0)
                    continue;

                if (!groups.TryGetValue(entry.MinimapGroupIndex, out var points))
                {
                    points = new List<(byte, byte)>();
                    groups.Add(entry.MinimapGroupIndex, points);
                    seenByGroup.Add(entry.MinimapGroupIndex, new HashSet<int>());
                }

                var key = (entry.MapX << 8) | entry.MapY;
                if (seenByGroup[entry.MinimapGroupIndex].Add(key))
                    points.Add((entry.MapX, entry.MapY));
            }

            if (groups.Count == 0)
                return null;

            var result = new List<IReadOnlyList<(byte, byte)>>(groups.Count);
            foreach (var group in groups.Values)
                result.Add(group);
            return result;
        }
    }
}
