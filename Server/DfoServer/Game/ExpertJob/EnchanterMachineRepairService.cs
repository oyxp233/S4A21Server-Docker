using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal static class EnchanterMachineRepairService
    {
        internal const byte ErrorCannotRepair = ExpertJobMachineRepairService.ErrorCannotRepair;
        internal const byte ErrorInvalidState = ExpertJobMachineRepairService.ErrorInvalidState;

        internal static bool TryRepair(
            InventoryService inventory,
            EnchanterMachineState state,
            uint experience,
            out ExpertJobMachineRepairResult result)
        {
            var config = EnchanterConfigProvider.Config;
            var rule = config.GetRepairRule(config.GetLevel(experience));
            if (state == null || rule == null)
            {
                result = new ExpertJobMachineRepairResult { ErrorCode = ErrorInvalidState };
                return false;
            }

            if (!ExpertJobMachineRepairService.TryRepair(
                    inventory,
                    state.Endurance,
                    rule.MaximumEndurance,
                    rule.FullRepairCost,
                    out result))
                return false;

            state.Endurance = result.Endurance;
            return true;
        }
    }
}
