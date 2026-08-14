using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal static class ExpertJobMachineRepairService
    {
        internal const byte ErrorCannotRepair = 22;
        internal const byte ErrorInvalidState = 60;

        internal static bool TryRepair(
            InventoryService inventory,
            int currentEndurance,
            int maximumEndurance,
            int fullRepairCost,
            out ExpertJobMachineRepairResult result)
        {
            result = new ExpertJobMachineRepairResult { ErrorCode = ErrorInvalidState };
            if (inventory == null
                || currentEndurance < 0
                || maximumEndurance <= 0
                || fullRepairCost <= 0)
                return false;

            var current = Math.Min(currentEndurance, maximumEndurance);
            var cost = (int)Math.Min(
                int.MaxValue,
                (long)fullRepairCost
                    - (long)current * fullRepairCost / maximumEndurance);
            if (cost <= 0)
            {
                result.ErrorCode = ErrorCannotRepair;
                return false;
            }

            var gold = inventory.GetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
            if (gold < cost)
            {
                var unitCost = fullRepairCost / maximumEndurance;
                if (unitCost <= 0)
                    return false;

                cost = gold - gold % unitCost;
                var repaired = cost / unitCost;
                if (cost <= 0 || repaired <= 0)
                {
                    result.ErrorCode = ErrorCannotRepair;
                    return false;
                }
                current = Math.Min(maximumEndurance, current + repaired);
            }
            else
            {
                current = maximumEndurance;
            }

            gold -= cost;
            if (!inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    gold))
                return false;

            result = new ExpertJobMachineRepairResult
            {
                Gold = gold,
                Endurance = current,
                Cost = cost,
            };
            return true;
        }
    }
}
