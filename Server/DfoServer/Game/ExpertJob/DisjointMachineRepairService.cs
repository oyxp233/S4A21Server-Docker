using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal static class DisjointMachineRepairService
    {
        internal const byte ErrorCannotRepair = ExpertJobMachineRepairService.ErrorCannotRepair;
        internal const byte ErrorInvalidState = ExpertJobMachineRepairService.ErrorInvalidState;

        internal static bool TryRepair(
            InventoryService inventory,
            DisjointMachineState state,
            out ExpertJobMachineRepairResult result)
        {
            var rule = state == null
                ? null
                : DisjointMachineConfigProvider.Config.GetRepairRule(state.MachineGrade);
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
