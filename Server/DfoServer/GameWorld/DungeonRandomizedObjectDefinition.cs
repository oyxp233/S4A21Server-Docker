using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal sealed class DungeonRandomizedObjectDefinition
    {
        internal static readonly DungeonRandomizedObjectDefinition Empty =
            new DungeonRandomizedObjectDefinition(
                Array.Empty<DungeonRandomizedObjectGroupDefinition>());

        internal DungeonRandomizedObjectDefinition(
            IReadOnlyList<DungeonRandomizedObjectGroupDefinition> groups)
        {
            if (groups == null || groups.Count == 0)
            {
                Groups = Array.Empty<DungeonRandomizedObjectGroupDefinition>();
                return;
            }

            var copy = new DungeonRandomizedObjectGroupDefinition[groups.Count];
            for (var index = 0; index < groups.Count; index++)
                copy[index] = groups[index];
            Groups = new ReadOnlyCollection<DungeonRandomizedObjectGroupDefinition>(copy);
        }

        internal IReadOnlyList<DungeonRandomizedObjectGroupDefinition> Groups { get; }
    }

    internal sealed class DungeonRandomizedObjectGroupDefinition
    {
        internal DungeonRandomizedObjectGroupDefinition(
            int selectCount,
            bool regenerate,
            int? minimapIcon,
            IReadOnlyList<DungeonRandomizedObjectEntryDefinition> objects)
        {
            SelectCount = selectCount;
            Regenerate = regenerate;
            MinimapIcon = minimapIcon;

            if (objects == null || objects.Count == 0)
            {
                Objects = Array.Empty<DungeonRandomizedObjectEntryDefinition>();
                return;
            }

            var copy = new DungeonRandomizedObjectEntryDefinition[objects.Count];
            for (var index = 0; index < objects.Count; index++)
                copy[index] = objects[index];
            Objects = new ReadOnlyCollection<DungeonRandomizedObjectEntryDefinition>(copy);
        }

        internal int SelectCount { get; }
        internal bool Regenerate { get; }
        internal int? MinimapIcon { get; }
        internal IReadOnlyList<DungeonRandomizedObjectEntryDefinition> Objects { get; }
    }

    internal readonly struct DungeonRandomizedObjectEntryDefinition
    {
        internal DungeonRandomizedObjectEntryDefinition(
            int objectIndex,
            int posX,
            int posY,
            int faction,
            int mapX,
            int mapY)
        {
            ObjectIndex = objectIndex;
            PosX = posX;
            PosY = posY;
            Faction = faction;
            MapX = mapX;
            MapY = mapY;
        }

        internal int ObjectIndex { get; }
        internal int PosX { get; }
        internal int PosY { get; }
        internal int Faction { get; }
        internal int MapX { get; }
        internal int MapY { get; }
    }

    internal static class DungeonRandomizedObjectTemplateCatalog
    {
        private static readonly Lazy<HashSet<int>> PassiveObjectIds =
            new Lazy<HashSet<int>>(() =>
            {
                var result = new HashSet<int>();
                var list = DungeonCatalog.LoadListFile(
                    System.IO.Path.Combine("passiveobject", "passiveobject.lst"));
                foreach (var entry in list.Entries)
                    if (entry.Id > 0) result.Add(entry.Id);
                return result;
            });

        internal static int ResolveSpawnMode(int templateId)
            => templateId > 0 && PassiveObjectIds.Value.Contains(templateId) ? 1 : 0;
    }
    internal static class DungeonRandomizedObjectDefinitionProjector
    {
        internal static DungeonRandomizedObjectDefinition Project(MazeInfo maze)
        {
            if (maze == null)
                return DungeonRandomizedObjectDefinition.Empty;

            var scripts = maze.RidableScripts != null && maze.RidableScripts.Count > 0
                ? maze.RidableScripts
                : maze.RidableScript == null
                    ? null
                    : new List<RidableObjectScript> { maze.RidableScript };
            if (scripts == null || scripts.Count == 0)
                return DungeonRandomizedObjectDefinition.Empty;

            var groups = new List<DungeonRandomizedObjectGroupDefinition>(scripts.Count);
            foreach (var script in scripts)
            {
                if (script == null)
                    continue;

                var objects = new List<DungeonRandomizedObjectEntryDefinition>(
                    script.Objects?.Count ?? 0);
                if (script.Objects != null)
                {
                    foreach (var item in script.Objects)
                    {
                        if (item == null || item.ObjectIndex <= 0)
                            continue;

                        objects.Add(new DungeonRandomizedObjectEntryDefinition(
                            item.ObjectIndex,
                            item.PosX,
                            item.PosY,
                            item.Faction,
                            item.MapX,
                            item.MapY));
                    }
                }

                groups.Add(new DungeonRandomizedObjectGroupDefinition(
                    script.SelectCount,
                    script.Regenerate,
                    script.MinimapIcon >= 0 ? script.MinimapIcon : null,
                    objects));
            }

            return groups.Count == 0
                ? DungeonRandomizedObjectDefinition.Empty
                : new DungeonRandomizedObjectDefinition(groups);
        }
    }
}
