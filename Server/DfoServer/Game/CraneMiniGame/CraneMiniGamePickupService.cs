using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.CraneMiniGame
{
    internal static class CraneMiniGamePickupService
    {
        internal static bool TryResolveSelection(
            CraneMiniGameStartResult state,
            ushort displaySlot,
            int itemId,
            out CraneMiniGameItem item)
        {
            item = null;
            if (state?.DisplayItems == null)
                return false;

            foreach (var candidate in state.DisplayItems)
            {
                if (candidate != null
                    && candidate.CatalogIndex == displaySlot
                    && candidate.ItemId == itemId)
                {
                    item = candidate;
                    return true;
                }
            }

            return false;
        }

        internal static bool RollSuccess(CraneMiniGameItem item, Func<int, int> next = null)
        {
            if (item == null || item.PickChance <= 0)
                return false;
            if (item.PickChance >= 100)
                return true;

            next ??= ServerRandom.Next;
            var threshold = (int)Math.Round(item.PickChance * 100d);
            return next(10000) < threshold;
        }
    }
}
