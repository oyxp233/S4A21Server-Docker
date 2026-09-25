using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        private static readonly Lazy<IReadOnlyDictionary<int, int>> DungeonOwners =
            new Lazy<IReadOnlyDictionary<int, int>>(LoadDungeonOwners);

        internal static LstFile LoadMapList() => MapList.Value;

        internal static int GetDungeonOwner(int mapId)
            => DungeonOwners.Value.TryGetValue(mapId, out var dungeonId) ? dungeonId : -1;

        private static IReadOnlyDictionary<int, int> LoadDungeonOwners()
        {
            var owners = new Dictionary<int, int>();
            foreach (var entry in LoadMapList().Entries)
            {
                var map = MapFile.Parse(PvfArchiveAccessor.ReadText(
                    Path.Combine("map", entry.FilePath)));
                owners[entry.Id] = map.DungeonId;
            }
            // 保留紧凑的资源归属索引，完整 MAP 定义仍按房间需要加载。
            return owners;
        }

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
