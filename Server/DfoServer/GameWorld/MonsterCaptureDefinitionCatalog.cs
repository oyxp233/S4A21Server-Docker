using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace DfoServer.GameWorld
{
    public readonly struct MonsterCaptureItemDefinition
    {
        internal MonsterCaptureItemDefinition(
            int itemId,
            int count,
            int dropRate)
        {
            ItemId = itemId;
            Count = count;
            DropRate = dropRate;
        }

        public int ItemId { get; }
        public int Count { get; }
        public int DropRate { get; }
    }

    internal static class MonsterCaptureDefinitionCatalog
    {
        private static readonly IReadOnlyList<MonsterCaptureItemDefinition> Empty =
            Array.Empty<MonsterCaptureItemDefinition>();
        private static readonly Lazy<LstFile> MonsterList =
            new Lazy<LstFile>(() => Dungeon.LoadLstFile(
                Path.Combine("monster", "monster.lst")));
        private static readonly ConcurrentDictionary<int,
            IReadOnlyList<MonsterCaptureItemDefinition>> Definitions =
                new ConcurrentDictionary<int,
                    IReadOnlyList<MonsterCaptureItemDefinition>>();

        internal static IReadOnlyList<MonsterCaptureItemDefinition> GetItems(
            int monsterCode)
        {
            if (monsterCode <= 0)
                return Empty;

            return Definitions.GetOrAdd(monsterCode, LoadItems);
        }

        private static IReadOnlyList<MonsterCaptureItemDefinition> LoadItems(
            int monsterCode)
        {
            try
            {
                var entry = MonsterList.Value.GetById(monsterCode);
                if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
                    return Empty;

                var monster = MonsterFile.Parse(PvfArchiveAccessor.ReadText(
                    Path.Combine("monster", entry.FilePath)));
                if (monster.CatchItems == null || monster.CatchItems.Count == 0)
                    return Empty;

                var items = new List<MonsterCaptureItemDefinition>();
                foreach (var item in monster.CatchItems)
                {
                    if (item == null
                        || item.ItemId <= 0
                        || item.Count <= 0
                        || item.DropRate < 0
                        || item.DropRate > 100)
                    {
                        FileLogger.Log(
                            $"[MonsterCaptureDefinitionCatalog] invalid entry: " +
                            $"monster={monsterCode} item={item?.ItemId ?? 0} " +
                            $"count={item?.Count ?? 0} rate={item?.DropRate ?? 0}");
                        continue;
                    }

                    items.Add(new MonsterCaptureItemDefinition(
                        item.ItemId,
                        item.Count,
                        item.DropRate));
                }

                return items.Count == 0
                    ? Empty
                    : new ReadOnlyCollection<MonsterCaptureItemDefinition>(
                        items.ToArray());
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[MonsterCaptureDefinitionCatalog] load failed: " +
                    $"monster={monsterCode} error={ex.Message}");
                return Empty;
            }
        }
    }
}
