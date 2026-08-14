using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Game.ExpertJob
{
    internal static class DisjointMachineService
    {
        internal const byte ErrorInventoryFull = 4;
        internal const byte ErrorInsufficientGold = 21;
        internal const byte ErrorInvalidItem = 19;
        internal const byte ErrorMachineGradeTooLow = 0xD4;
        internal const byte ErrorNoEndurance = 189;

        internal static bool TryDisjoint(
            InventoryService requesterInventory,
            InventoryService ownerInventory,
            ExpertJobStoreSession store,
            short targetSlotIndex,
            int ownerGoldCarryLimit,
            out DisjointMachineOperationResult result)
        {
            result = new DisjointMachineOperationResult { ErrorCode = ErrorInvalidItem };
            if (requesterInventory == null
                || ownerInventory == null
                || store == null
                || store.Kind != ExpertJobStoreKind.DisjointMachine
                || targetSlotIndex < 0)
                return false;
            var machine = store.DisjointMachine;
            if (machine == null)
                return false;
            if (machine.Endurance <= 0)
            {
                result.ErrorCode = ErrorNoEndurance;
                return false;
            }

            var selfService = requesterInventory.CharacterId == ownerInventory.CharacterId;
            var requesterGold = GetGold(requesterInventory);
            var ownerGold = GetGold(ownerInventory);
            if (!selfService && requesterGold < store.Cost)
            {
                result.ErrorCode = ErrorInsufficientGold;
                return false;
            }
            if (!selfService)
            {
                if ((long)ownerGold + store.Cost > ownerGoldCarryLimit)
                {
                    result.ErrorCode = ErrorInsufficientGold;
                    return false;
                }
            }

            var request = new DisjointItemRequest
            {
                TargetSlotIndex = targetSlotIndex,
                ItemSpace = InventoryListType.Main,
                DisjointItemSlotIndex = -1,
            };
            if (!InventoryDisjointService.TryDisjointItem(
                    requesterInventory,
                    request,
                    (ItemCore source,
                        ItemMetadata metadata,
                        out List<DisjointMaterialResult> materials,
                        out byte resolveErrorCode) =>
                    {
                        materials = DisjointMachineResultCalculator.Calculate(
                            source,
                            metadata,
                            machine.MachineGrade,
                            selfService);
                        if (materials.Count > 0)
                        {
                            resolveErrorCode = 0;
                            return true;
                        }

                        resolveErrorCode = (source.AmplifyType & 0x80) != 0
                            ? ErrorMachineGradeTooLow
                            : ErrorInvalidItem;
                        return false;
                    },
                    out var disjointResult))
            {
                result.ErrorCode = disjointResult?.ErrorCode ?? ErrorInvalidItem;
                return false;
            }

            if (!selfService)
            {
                requesterGold -= store.Cost;
                ownerGold += store.Cost;
                if (!requesterInventory.SetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart,
                        requesterGold)
                    || !ownerInventory.SetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart,
                        ownerGold))
                {
                    throw new InvalidOperationException("disjoint machine gold mutation failed after validation");
                }
            }

            var config = DisjointMachineConfigProvider.Config;
            var enduranceReduction = NextInclusive(
                config.EnduranceReduceMin,
                config.EnduranceReduceMax);
            var experienceGain = NextInclusive(config.GainExpMin, config.GainExpMax);
            machine.Endurance = Math.Max(0, machine.Endurance - enduranceReduction);
            result = new DisjointMachineOperationResult
            {
                ErrorCode = 0,
                DisjointResult = disjointResult,
                RequesterGold = requesterGold,
                OwnerGold = ownerGold,
                Endurance = machine.Endurance,
                ExperienceGain = experienceGain,
            };
            return true;
        }

        private static int GetGold(InventoryService inventory)
            => inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;

        private static int NextInclusive(int minimum, int maximum)
            => maximum <= minimum ? minimum : minimum + ServerRandom.Next(maximum - minimum + 1);
    }
}
