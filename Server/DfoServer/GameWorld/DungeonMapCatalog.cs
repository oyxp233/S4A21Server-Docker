using System;
using System.Collections.Concurrent;
using System.IO;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal static class DungeonMapCatalog
    {
        private static readonly Lazy<LstFile> MapList =
            new Lazy<LstFile>(() => DungeonCatalog.LoadListFile(
                Path.Combine("map", "map.lst")));

        private static readonly ConcurrentDictionary<int, MapFile> MapFiles =
            new ConcurrentDictionary<int, MapFile>();

        internal static LstFile LoadMapList() => MapList.Value;

        // Returned definitions are shared PVF cache entries and must remain read-only.
        internal static MapFile GetMapFile(int mapId)
        {
            if (mapId <= 0)
                throw new ArgumentOutOfRangeException(nameof(mapId));

            return MapFiles.GetOrAdd(mapId, id =>
            {
                var path = DungeonCatalog.ResolveFilePath(
                    LoadMapList(),
                    id,
                    "map");
                return MapFile.Parse(PvfArchiveAccessor.ReadText(
                    Path.Combine("map", path)));
            });
        }
    }
}
