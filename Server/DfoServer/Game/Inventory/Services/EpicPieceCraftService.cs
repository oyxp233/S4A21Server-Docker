using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class EpicPieceCraftResult
    {
        private readonly List<InventoryMaterialConsumptionEntry> _consumedMaterials =
            new List<InventoryMaterialConsumptionEntry>();

        internal bool Success { get; set; }
        internal byte ErrorCode { get; set; } = EpicPieceCraftService.DefaultErrorCode;
        internal int OutputEquipmentId { get; set; }
        internal int EpicPieceId { get; set; }
        internal int EpicPieceBalance { get; set; }
        internal bool DeliveredByMail { get; set; }
        internal short OutputSlotIndex { get; set; } = -1;
        internal InventoryMutationSet Changes { get; } = new InventoryMutationSet();
        internal IReadOnlyList<InventoryMaterialConsumptionEntry> ConsumedMaterials => _consumedMaterials;

        internal void AddConsumedMaterial(InventoryMaterialConsumptionEntry entry)
        {
            if (entry == null)
                return;

            _consumedMaterials.Add(entry);
            Changes.AddSlot(InventoryListType.Main, entry.SlotIndex);
        }
    }

    internal static class EpicPieceCraftService
    {
        internal const byte DefaultErrorCode = 4;

        internal static bool CanCraft(
            InventoryService inventory,
            int outputEquipmentId,
            out EpicPieceCraftResult result)
        {
            result = new EpicPieceCraftResult
            {
                OutputEquipmentId = outputEquipmentId,
            };

            if (inventory == null
                || outputEquipmentId <= 0
                || !EpicPieceCatalogService.TryGetRecipeByOutputId(
                    outputEquipmentId,
                    out var recipe))
            {
                return false;
            }

            result.EpicPieceId = recipe.EpicPieceId;
            if (inventory.EpicPieces.GetCountByPieceId(recipe.EpicPieceId)
                < recipe.EpicPieceCount)
            {
                return false;
            }

            return InventoryMaterialConsumptionService.HasEnough(
                inventory,
                BuildMaterialRequirements(recipe));
        }

        internal static bool TryCraft(
            InventoryService inventory,
            int outputEquipmentId,
            IInventoryOverflowRewardSink overflowSink,
            out EpicPieceCraftResult result)
        {
            result = new EpicPieceCraftResult
            {
                OutputEquipmentId = outputEquipmentId,
            };

            if (inventory == null
                || outputEquipmentId <= 0
                || !EpicPieceCatalogService.TryGetRecipeByOutputId(
                    outputEquipmentId,
                    out var recipe))
            {
                return false;
            }

            result.EpicPieceId = recipe.EpicPieceId;
            if (inventory.EpicPieces.GetCountByPieceId(recipe.EpicPieceId)
                < recipe.EpicPieceCount)
            {
                return false;
            }

            var materialRequirements = BuildMaterialRequirements(recipe);
            if (!InventoryMaterialConsumptionService.HasEnough(
                    inventory,
                    materialRequirements))
            {
                return false;
            }

            var outputRequest = InventoryRewardGrantRequest.Create(
                outputEquipmentId,
                1,
                ItemCreateReason.Unknown);
            var planningInventory =
                InventorySpecialConsumableService.CreatePlanningInventory(inventory);
            if (!TryConsumeRecipe(
                    planningInventory,
                    recipe,
                    materialRequirements,
                    null))
            {
                return false;
            }

            var canInsert = TryPlanOutputInsert(
                planningInventory,
                outputRequest,
                out var outputPlan);
            if (!canInsert
                && !TryDeliverOutputByMail(
                    inventory,
                    overflowSink,
                    outputRequest))
            {
                return false;
            }

            if (!TryConsumeRecipe(
                    inventory,
                    recipe,
                    materialRequirements,
                    result))
            {
                return false;
            }

            if (canInsert)
            {
                if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                        inventory,
                        outputPlan,
                        out var grantBatch)
                    || grantBatch == null
                    || !grantBatch.Success
                    || grantBatch.Results.Count != 1
                    || grantBatch.Results[0].Kind != InventoryRewardGrantKind.InventoryItem)
                {
                    return false;
                }

                var grant = grantBatch.Results[0];
                result.OutputSlotIndex = grant.SlotIndex;
                result.Changes.AddRange(grant.Changes);
            }
            else
            {
                result.DeliveredByMail = true;
            }

            result.Success = true;
            return true;
        }

        private static List<InventoryMaterialRequirement> BuildMaterialRequirements(
            EpicPieceRecipe recipe)
        {
            var result = new List<InventoryMaterialRequirement>();
            if (recipe?.Materials == null)
                return result;

            foreach (var material in recipe.Materials)
            {
                if (material == null
                    || material.ItemId <= 0
                    || material.Count <= 0)
                {
                    continue;
                }

                result.Add(new InventoryMaterialRequirement(
                    material.ItemId,
                    material.Count));
            }

            return result;
        }

        private static bool TryConsumeRecipe(
            InventoryService inventory,
            EpicPieceRecipe recipe,
            IReadOnlyList<InventoryMaterialRequirement> materialRequirements,
            EpicPieceCraftResult result)
        {
            if (inventory == null || recipe == null)
                return false;

            if (!inventory.EpicPieces.TryConsumeByPieceId(
                    recipe.EpicPieceId,
                    recipe.EpicPieceCount,
                    out var pieceBalance))
            {
                return false;
            }

            var consumed = new List<InventoryMaterialConsumptionEntry>();
            if (!InventoryMaterialConsumptionService.TryConsume(
                    inventory,
                    materialRequirements,
                    consumed))
            {
                return false;
            }

            if (result != null)
            {
                result.EpicPieceBalance = pieceBalance;
                foreach (var entry in consumed)
                    result.AddConsumedMaterial(entry);
            }

            return true;
        }

        private static bool TryPlanOutputInsert(
            InventoryService planningInventory,
            InventoryRewardGrantRequest outputRequest,
            out InventoryRewardGrantBatchPlan outputPlan)
        {
            outputPlan = null;
            if (!InventoryRewardGrantService.TryPlanBatch(
                    planningInventory,
                    new[] { outputRequest },
                    out var plan)
                || plan == null
                || !plan.Success
                || plan.Entries.Count != 1
                || plan.Entries[0].Kind != InventoryRewardGrantKind.InventoryItem)
            {
                return false;
            }

            outputPlan = plan;
            return true;
        }

        private static bool TryDeliverOutputByMail(
            InventoryService inventory,
            IInventoryOverflowRewardSink overflowSink,
            InventoryRewardGrantRequest outputRequest)
        {
            overflowSink = overflowSink ?? RejectingInventoryOverflowRewardSink.Instance;
            return overflowSink.TryDeliver(
                inventory,
                new[] { outputRequest },
                out _);
        }
    }
}
