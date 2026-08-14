using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal static class DisjointMachineUpgradeService
    {
        internal const byte ErrorCannotUpgrade = 19;
        internal const byte ErrorCharacterLevelTooLow = 14;
        internal const byte ErrorInsufficientGold = 22;

        internal static bool TryUpgrade(
            InventoryService inventory,
            DisjointMachineState state,
            uint expertJobExperience,
            int characterLevel,
            out DisjointMachineUpgradeResult result)
        {
            result = new DisjointMachineUpgradeResult { ErrorCode = ErrorCannotUpgrade };
            if (inventory == null || state == null || characterLevel <= 0)
                return false;

            var config = DisjointMachineConfigProvider.Config;
            var currentRule = config.GetRepairRule(state.MachineGrade);
            var targetGrade = state.MachineGrade + 1;
            var targetRule = config.GetRepairRule(targetGrade);
            var upgradeCost = config.GetUpgradeCost(targetGrade);
            if (currentRule == null
                || targetRule == null
                || state.Endurance != currentRule.MaximumEndurance
                || config.GetExpertJobLevel(expertJobExperience) < targetGrade
                || upgradeCost <= 0)
                return false;

            if (characterLevel < config.GetMinimumCharacterLevel(targetGrade))
            {
                result.ErrorCode = ErrorCharacterLevelTooLow;
                return false;
            }

            var gold = inventory.GetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
            if (gold < upgradeCost)
            {
                result.ErrorCode = ErrorInsufficientGold;
                return false;
            }

            gold -= upgradeCost;
            if (!inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    gold))
                return false;

            state.MachineGrade = (byte)targetGrade;
            state.Endurance = targetRule.MaximumEndurance;
            result = new DisjointMachineUpgradeResult
            {
                Gold = gold,
                Grade = targetGrade,
                Endurance = state.Endurance,
                Cost = upgradeCost,
            };
            return true;
        }
    }
}
