using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.CraneMiniGame
{
    internal sealed class CraneMiniGameStartResult
    {
        public ushort MachineId { get; set; }
        public short MaterialSlot { get; set; }
        public int MaterialRemainingCount { get; set; }
        public IReadOnlyList<CraneMiniGameItem> DisplayItems { get; set; }
    }

    internal sealed class CraneMiniGameStartService
    {
        private readonly CraneMiniGameCatalog _catalog;

        internal CraneMiniGameStartService(CraneMiniGameCatalog catalog = null)
        {
            _catalog = catalog ?? CraneMiniGameCatalog.Load();
        }

        internal bool TryStart(
            InventoryService inventory,
            ushort machineId,
            out CraneMiniGameStartResult result)
        {
            result = null;
            if (inventory == null || machineId == 0)
                return false;

            var displayItems = SelectDisplayItems(_catalog.Items, _catalog.ViewCount);
            if (displayItems.Count != _catalog.ViewCount
                || !inventory.TryConsumeMainItem(
                    _catalog.MaterialItemId,
                    _catalog.MaterialCount,
                    out var consumed)
                || !consumed.Success)
                return false;

            result = new CraneMiniGameStartResult
            {
                MachineId = machineId,
                MaterialSlot = consumed.SlotIndex,
                MaterialRemainingCount = consumed.RemainingCount,
                DisplayItems = displayItems,
            };
            return true;
        }

        internal static IReadOnlyList<CraneMiniGameItem> SelectDisplayItems(
            IReadOnlyList<CraneMiniGameItem> source,
            int count,
            Func<int, int> next = null)
        {
            var remaining = new List<CraneMiniGameItem>(source ?? Array.Empty<CraneMiniGameItem>());
            var selected = new List<CraneMiniGameItem>();
            next ??= ServerRandom.Next;

            while (selected.Count < count && remaining.Count > 0)
            {
                long total = 0;
                foreach (var item in remaining)
                    total += Math.Max(0L, (long)Math.Round(item.ViewWeight * 10000d));
                if (total <= 0 || total > int.MaxValue)
                    break;

                var roll = next((int)total);
                var cumulative = 0L;
                var selectedIndex = remaining.Count - 1;
                for (var i = 0; i < remaining.Count; i++)
                {
                    cumulative += Math.Max(0L, (long)Math.Round(remaining[i].ViewWeight * 10000d));
                    if (roll < cumulative)
                    {
                        selectedIndex = i;
                        break;
                    }
                }

                selected.Add(remaining[selectedIndex]);
                remaining.RemoveAt(selectedIndex);
            }

            return selected;
        }
    }
}
