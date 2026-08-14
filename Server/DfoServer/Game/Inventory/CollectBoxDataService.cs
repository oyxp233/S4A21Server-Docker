using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DfoServer.Game.Inventory
{
    // 收集箱槛位配置，来源 PVF localization/collectbox/collectboxinfo.etc。
    public static class CollectBoxDataService
    {
        private static readonly Lazy<CollectBoxFile> File = new Lazy<CollectBoxFile>(Load, LazyThreadSafetyMode.ExecutionAndPublication);

        public static CollectBoxEntry GetByIndex(int index)
        {
            return File.Value.GetByIndex(index);
        }

        // 按 PVF 出现顺序返回全部 Index，供 0381 按 occurrenceIndex 逐个推送。
        public static IReadOnlyList<int> GetAllIndexes()
        {
            return File.Value.Entries.Select(e => e.Index).ToList();
        }

        private static CollectBoxFile Load()
        {
            var content = PvfArchiveAccessor.ReadText("localization/collectbox/collectboxinfo.etc");
            return CollectBoxFile.Parse(content);
        }
    }
}
