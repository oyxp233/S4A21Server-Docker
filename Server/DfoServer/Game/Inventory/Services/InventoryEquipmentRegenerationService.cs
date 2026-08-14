using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryEquipmentRegenerationService
    {
        private const int LegacyRarity = 2;
        private const int MaterialGroupLegacy = 99;
        // Keep the PVF [probability legacy] value intact, but reduce the
        // effective legacy output weight for the server-side compound pool.
        private const double LegacyWeightScale = 0.01;

        internal static bool TryRegenerate(
            InventoryService inventory,
            EquipmentRegenerationRequest request,
            out EquipmentRegenerationResult result)
        {
            result = new EquipmentRegenerationResult
            {
                SourceSlotIndex = request?.SourceSlotIndex ?? (short)-1,
                Mode = request?.Mode ?? 0,
                Part = request?.Part ?? 0,
            };
            if (inventory == null
                || request == null
                || request.SourceSlotIndex < InventoryService.MainSlotStart
                || request.Mode > 1)
                return false;

            var source = inventory.GetItem(InventoryListType.Main, request.SourceSlotIndex);
            if (source == null || source.ItemKind != ItemCore.KindEquipment)
                return false;
            if (!ItemMetadataResolver.TryLoadEquipmentFile(source.ItemId, out var sourceFile)
                || sourceFile == null)
                return false;
            if (!IsTradableCompoundResult(sourceFile.AttachType))
                return false;

            var config = EquipmentRegenerationConfigProvider.LoadCurrent();
            if (config.ExceptItemIds.Contains(source.ItemId))
                return false;
            if (EquipmentRegenerationCandidateCatalog.IsGeneratedModifiedOption(
                    sourceFile.Rarity,
                    sourceFile.CreationRate,
                    sourceFile.ForceResultItemRule))
                return false;

            result.SourceItemTemplateId = source.ItemId;
            var sourceLegacy = IsLegacy(sourceFile);
            if (!HasCompoundDropEligibility(
                    sourceFile.Rarity,
                    sourceFile.MinimumLevel,
                    sourceLegacy,
                    sourceFile.CreationRate,
                    config.ExceptionWeights.ContainsKey(source.ItemId)))
                return false;
            var materialGroup = sourceLegacy ? MaterialGroupLegacy : sourceFile.Rarity;
            // 86JP sends mode 0 for a directed compound and the selected
            // choose-part id in the final ushort. Non-zero mode is random.
            var specific = request.Mode == 0;
            var requirements = config.GetMaterials(materialGroup, specific, sourceFile.MinimumLevel)
                .Select(material => new InventoryMaterialRequirement(material.ItemTemplateId, material.Count))
                .ToList();
            if (requirements.Count == 0 || !InventoryMaterialConsumptionService.HasEnough(inventory, requirements))
                return false;
            var requestedPart = ResolveRequestedPart(request, sourceFile, config);
            if (requestedPart == ushort.MaxValue)
                return false;

            var candidates = BuildCandidates(sourceFile, sourceLegacy, requestedPart, materialGroup, config);
            if (candidates.Count == 0)
                return false;

            var selected = PickCandidate(candidates);
            if (selected == null)
                return false;

            if (!InventoryCreateService.TryCreateCore(
                    selected.ItemTemplateId,
                    ItemCreateReason.Unknown,
                    1,
                    out var newCore))
                return false;

            // The candidate pool only contains sealing templates, but compound
            // output preserves whether the consumed equipment was sealed or opened.
            newCore.SealFlag = source.SealFlag;
            if (!TryPlanMutation(
                    inventory,
                    request.SourceSlotIndex,
                    requirements,
                    newCore,
                    out var insertPlan))
                return false;

            if (!InventoryCreateService.TryCreateDetails(
                    inventory,
                    newCore,
                    ItemCreateReason.Unknown,
                    null,
                    out var createResult))
                return false;

            var consumed = new List<InventoryMaterialConsumptionEntry>();
            if (!InventoryMaterialConsumptionService.TryConsume(inventory, requirements, consumed))
            {
                InventoryCreateService.DetachCreatedDetails(inventory, createResult);
                return false;
            }

            if (!InventoryDeleteService.TryRemoveSlot(
                    inventory,
                    InventoryListType.Main,
                    request.SourceSlotIndex,
                    out var deleteResult)
                || !deleteResult.Success)
            {
                InventoryCreateService.DetachCreatedDetails(inventory, createResult);
                return false;
            }

            if (!InventoryInsertService.TryApplyInsertPlan(
                    inventory,
                    newCore,
                    insertPlan,
                    out var insertResult)
                || !insertResult.Success)
            {
                InventoryCreateService.DetachCreatedDetails(inventory, createResult);
                return false;
            }

            result.ResultItemTemplateId = selected.ItemTemplateId;
            result.ResultSlotIndex = insertResult.SlotIndex;
            result.TargetLevel = selected.TargetLevel;
            result.LegacyResult = selected.Legacy;
            result.CandidateCount = candidates.Count;
            result.SelectedWeight = selected.Weight;
            foreach (var entry in consumed)
                result.ConsumedEntries.Add(new EquipmentRegenerationConsumedEntry
                {
                    SlotIndex = entry.SlotIndex,
                    ItemTemplateId = entry.ItemTemplateId,
                    Count = entry.Count,
                });
            result.ErrorCode = 0;
            return true;
        }

        private static bool TryPlanMutation(
            InventoryService inventory,
            short sourceSlotIndex,
            IReadOnlyList<InventoryMaterialRequirement> requirements,
            ItemCore newCore,
            out InventoryInsertPlan insertPlan)
        {
            insertPlan = null;
            var preview = InventoryCompoundPlanning.CloneInventory(inventory);
            var previewConsumed = new List<InventoryMaterialConsumptionEntry>();
            if (!InventoryMaterialConsumptionService.TryConsume(preview, requirements, previewConsumed)
                || !InventoryDeleteService.TryRemoveSlot(
                    preview,
                    InventoryListType.Main,
                    sourceSlotIndex,
                    out var previewDelete)
                || !previewDelete.Success)
            {
                return false;
            }

            var previewCore = newCore.Copy();
            return InventoryInsertService.TryPlanInsertByDefaultRule(
                    preview,
                    previewCore,
                    1,
                    out insertPlan)
                && insertPlan != null
                && insertPlan.Success
                && InventoryInsertService.TryApplyInsertPlan(
                    preview,
                    previewCore,
                    insertPlan,
                    out var previewInsert)
                && previewInsert.Success;
        }

        private static ushort ResolveRequestedPart(
            EquipmentRegenerationRequest request,
            EquipmentFile source,
            EquipmentRegenerationConfigProvider.Config config)
        {
            // Non-zero mode is the all-parts random compound. Directed mode 0
            // must retain the requested choose-part id.
            if (request.Mode != 0)
                return 0;
            if (request.Part == 0 || request.Part > 15)
                return ushort.MaxValue;
            return config.IsLegalPart(source.ItemGroupName, request.Part)
                ? request.Part
                : ushort.MaxValue;
        }

        private static List<EquipmentRegenerationCandidate> BuildCandidates(
            EquipmentFile source,
            bool sourceLegacy,
            ushort requestedPart,
            int materialGroup,
            EquipmentRegenerationConfigProvider.Config config)
        {
            var levelPools = BuildCandidatePools(
                source.Rarity,
                source.MinimumLevel,
                source.ItemGroupName,
                sourceLegacy,
                requestedPart,
                materialGroup,
                config);
            if (levelPools.Count == 0)
                return new List<EquipmentRegenerationCandidate>();

            var totalLevelWeight = levelPools.Sum(pool => Math.Max(0, pool.LevelWeight));
            var levelRoll = NextWeightedRoll(totalLevelWeight);
            foreach (var pool in levelPools)
            {
                levelRoll -= Math.Max(0, pool.LevelWeight);
                if (levelRoll < 0)
                    return pool.Candidates.ToList();
            }
            return levelPools[levelPools.Count - 1].Candidates.ToList();
        }

        internal static IReadOnlyList<EquipmentRegenerationCandidatePool> BuildCandidatePools(
            int sourceRarity,
            int sourceMinimumLevel,
            string sourceItemGroupName,
            bool sourceLegacy,
            ushort requestedPart,
            int materialGroup,
            EquipmentRegenerationConfigProvider.Config config)
        {
            var targetLevels = config.GetTargetLevels(materialGroup, Math.Max(0, sourceMinimumLevel));
            var levelPools = new List<EquipmentRegenerationCandidatePool>();

            foreach (var target in targetLevels)
            {
                var candidates = new List<EquipmentRegenerationCandidate>();
                foreach (var entry in EquipmentRegenerationCandidateCatalog.GetCandidates(sourceRarity, target.Level))
                {
                    if (config.ExceptItemIds.Contains(entry.ItemTemplateId)
                        || !config.IsKnownGroup(entry.ItemGroupName)
                        || !config.IsLegalPart(entry.ItemGroupName, requestedPart)
                        || !MatchesSpecificItemGroup(entry.ItemGroupName, sourceItemGroupName, requestedPart)
                        || !HasCompoundDropEligibility(
                            entry.Rarity,
                            entry.MinimumLevel,
                            entry.Legacy,
                            entry.CreationRate,
                            config.ExceptionWeights.ContainsKey(entry.ItemTemplateId))
                        || !IsTradableCompoundResult(entry.AttachType))
                        continue;

                    if (sourceLegacy && !entry.Legacy)
                        continue;
                    if (sourceRarity != LegacyRarity && entry.Legacy)
                        continue;

                    var weight = config.ExceptionWeights.TryGetValue(entry.ItemTemplateId, out var exceptionWeight)
                        ? exceptionWeight
                        : (entry.Legacy ? config.LegacyWeight * LegacyWeightScale : 1.0);
                    if (weight <= 0)
                        continue;

                    candidates.Add(new EquipmentRegenerationCandidate
                    {
                        ItemTemplateId = entry.ItemTemplateId,
                        TargetLevel = entry.MinimumLevel,
                        Weight = weight,
                        Legacy = entry.Legacy,
                    });
                }

                if (target.Weight > 0 && candidates.Count > 0)
                    levelPools.Add(new EquipmentRegenerationCandidatePool
                    {
                        TargetLevel = target.Level,
                        LevelWeight = target.Weight,
                        Candidates = candidates.AsReadOnly(),
                    });
            }
            return levelPools.AsReadOnly();
        }

        private static EquipmentRegenerationCandidate PickCandidate(
            IReadOnlyList<EquipmentRegenerationCandidate> candidates)
        {
            var total = candidates.Sum(candidate => Math.Max(0, candidate.Weight));
            if (total <= 0)
                return null;
            var roll = NextWeightedRoll(total);
            foreach (var candidate in candidates)
            {
                roll -= Math.Max(0, candidate.Weight);
                if (roll < 0)
                    return candidate;
            }
            return candidates[candidates.Count - 1];
        }

        private static double NextWeightedRoll(double exclusiveMaximum)
            => ServerRandom.Next(1_000_000) * exclusiveMaximum / 1_000_000d;

        private static bool IsLegacy(EquipmentFile equipment)
            => string.Equals(equipment?.ItemCategory?.Trim(), "legacy", StringComparison.OrdinalIgnoreCase);

        // Compound output must be transferable in its freshly-created state.
        // Sealing templates are created sealed by InventoryCreateService.
        private static bool IsTradableCompoundResult(string attachType)
            => string.Equals(
                NormalizeItemGroup(attachType),
                "sealing",
                StringComparison.OrdinalIgnoreCase);

        internal static bool MatchesSpecificItemGroup(
            string candidateGroupName,
            string sourceGroupName,
            ushort requestedPart)
            => requestedPart == 0
                || string.Equals(
                    NormalizeItemGroup(candidateGroupName),
                    NormalizeItemGroup(sourceGroupName),
                    StringComparison.OrdinalIgnoreCase);

        internal static bool HasCompoundCreationRate(
            bool legacy,
            int creationRate,
            bool hasExplicitExceptionWeight)
            => legacy || hasExplicitExceptionWeight || creationRate > 0;

        internal static bool HasCompoundDropEligibility(
            int rarity,
            int minimumLevel,
            bool legacy,
            int creationRate,
            bool hasExplicitExceptionWeight)
            => (minimumLevel >= 85 && rarity == 6)
                || HasCompoundCreationRate(legacy, creationRate, hasExplicitExceptionWeight);

        private static string NormalizeItemGroup(string value)
            => (value ?? string.Empty)
                .Replace("`", string.Empty)
                .Replace("[", string.Empty)
                .Replace("]", string.Empty)
                .Trim();
    }
}
