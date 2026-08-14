using System;
using System.Collections.Generic;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Dungeon
{
    internal static class DungeonRandomizedObjectSelectionService
    {
        internal static IReadOnlyList<RidableObjectSpawnEntry> Select(
            DungeonRandomizedObjectDefinition definition,
            Func<int, int> next = null)
        {
            if (definition?.Groups == null || definition.Groups.Count == 0)
                return Array.Empty<RidableObjectSpawnEntry>();

            next ??= ServerRandom.Next;
            var result = new List<RidableObjectSpawnEntry>();
            var minimapGroupIndex = 0;
            foreach (var group in definition.Groups)
            {
                if (group == null)
                    continue;

                var currentMinimapGroup = group.MinimapIcon.HasValue
                    ? minimapGroupIndex++
                    : -1;
                if (group.Objects == null || group.Objects.Count == 0)
                    continue;

                var candidates = new List<DungeonRandomizedObjectEntryDefinition>(
                    group.Objects);
                if (group.SelectCount > 0 && group.SelectCount < candidates.Count)
                {
                    for (var index = candidates.Count - 1; index > 0; index--)
                    {
                        var selectedIndex = next(index + 1);
                        if ((uint)selectedIndex > (uint)index)
                        {
                            throw new InvalidOperationException(
                                "Randomized object selector returned an out-of-range index.");
                        }

                        (candidates[index], candidates[selectedIndex]) =
                            (candidates[selectedIndex], candidates[index]);
                    }

                    candidates.RemoveRange(
                        group.SelectCount,
                        candidates.Count - group.SelectCount);
                }

                foreach (var item in candidates)
                {
                    result.Add(new RidableObjectSpawnEntry
                    {
                        ObjectIndex = item.ObjectIndex,
                        MonsterIndex = 0,
                        PosX = item.PosX,
                        PosY = item.PosY,
                        Faction = item.Faction,
                        SpawnMode = DungeonRandomizedObjectTemplateCatalog.ResolveSpawnMode(item.ObjectIndex),
                        MapX = checked((byte)item.MapX),
                        MapY = checked((byte)item.MapY),
                        HasMinimapIcon = group.MinimapIcon.HasValue,
                        MinimapGroupIndex = currentMinimapGroup,
                    });
                }
            }

            return result;
        }
    }
}
