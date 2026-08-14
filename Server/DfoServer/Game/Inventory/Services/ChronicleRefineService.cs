using System;
using System.Collections.Generic;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.ItemUpgrade;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    internal static class ChronicleRefineService
    {
        internal static bool TryRefine(
            InventoryService inventory,
            ChronicleRefineCommand command,
            out ChronicleRefineResult result)
        {
            return TryRefine(
                inventory,
                command,
                () => Infrastructure.ServerRandom.Next(101),
                out result);
        }

        internal static bool TryRefine(
            InventoryService inventory,
            ChronicleRefineCommand command,
            Func<int> rollProvider,
            out ChronicleRefineResult result)
        {
            result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorInvalidMaterial);
            if (inventory == null || command == null
                || command.MaterialSlotIndex == command.TargetSlotIndex)
                return false;

            var material = inventory.GetItem(InventoryListType.Main, command.MaterialSlotIndex);
            if (material == null
                || material.ItemId != command.MaterialItemTemplateId
                || material.Count <= 0
                || !InventoryStackRuleService.IsStackable(material))
                return false;

            if (!ChronicleRefineMaterialResolver.TryResolveMaterial(material.ItemId, out var materialDefinition))
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorUnsupported);
                return false;
            }

            var target = inventory.GetItem(InventoryListType.Main, command.TargetSlotIndex);
            if (target == null || target.ItemKind != ItemCore.KindEquipment)
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorInvalidTarget);
                return false;
            }
            if (target.ItemId != command.TargetItemTemplateId)
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorTemplateMismatch);
                return false;
            }
            if (IsItemLocked(inventory, target))
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorLocked);
                return false;
            }

            var metadata = ItemMetadataResolver.Resolve(target.ItemId);
            var equipmentType = EquipmentTypeInfo.ParseOrUnknown(metadata.EquipmentType);
            if (metadata.Rarity != 5 || !EquipmentTypeInfo.IsUpgradeTargetType(equipmentType))
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorUnsupported);
                return false;
            }

            if (!ItemMetadataResolver.TryLoadEquipmentFile(target.ItemId, out var equipment)
                || !ChronicleRefineJobMatcher.Matches(equipment.UsableJob, command.CharacterJob))
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorUnsupported);
                return false;
            }

            var selectedCheck = FindChronicleCheck(
                materialDefinition.ThreeChronicleEnchant,
                command.CharacterJob,
                command.FirstGrowType,
                metadata.EquipmentType,
                command.OptionNo);
            var selectedSkill = selectedCheck?.Skills.Find(skill =>
                skill.OptionNo == command.OptionNo
                && ChronicleRefineJobMatcher.Matches(skill.Job, command.CharacterJob));
            if (selectedCheck == null
                || selectedSkill == null
                || selectedSkill.SkillId < 0
                || !ChronicleRefineMaterialResolver.TryGetPacketAuraItemId(
                    materialDefinition.Type,
                    out var packetAuraItemId))
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorUnsupported);
                return false;
            }

            if ((target.AmplifyType & 0x80) != 0)
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorUnidentified);
                return false;
            }
            if (target.Durability != metadata.Durability)
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorDurability);
                return false;
            }
            if (command.OptionNo > 0x1F)
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorUnsupported);
                return false;
            }

            var current = NormalizeOptions(target.ChronicleOptions, equipmentType);
            if (current.Count >= 2)
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorOptionFull);
                return false;
            }

            foreach (var option in current)
            {
                if (option.OptionNo == command.OptionNo
                    && ChronicleRefineMaterialResolver.TryGetAuraType(option.OptionId, out var currentAuraType)
                    && currentAuraType == materialDefinition.Type)
                {
                    result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorUnsupported);
                    return false;
                }
            }

            var probability = current.Count < materialDefinition.ThreeChronicleEnchant.Probabilities.Count
                ? materialDefinition.ThreeChronicleEnchant.Probabilities[current.Count]
                : 0;
            var roll = Math.Max(0, Math.Min(100, rollProvider != null ? rollProvider() : 100));
            var refineSucceeded = ChronicleRefineProbability.IsSuccess(probability, roll);
            var failureRewards = new List<DisjointMaterialResult>();
            List<InventoryRewardGrantRequest> rewardRequests = null;

            if (!refineSucceeded)
            {
                if (!ChronicleRefineMaterialResolver.TryGetFragmentItemId(
                        materialDefinition,
                        out var fragmentItemTemplateId))
                {
                    result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorUnsupported);
                    return false;
                }

                failureRewards = BuildFailureRewards(metadata, target.Upgrade, fragmentItemTemplateId);
                if (!BuildGrantRequests(failureRewards, out rewardRequests)
                    || !InventoryRewardGrantService.TryPlanBatch(inventory, rewardRequests, out var plan)
                    || !plan.Success)
                {
                    result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorInventoryFull);
                    return false;
                }
            }

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    command.MaterialSlotIndex,
                    1,
                    out var materialDelete))
            {
                result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorDeleteFailed);
                return false;
            }

            if (refineSucceeded)
            {
                var updated = target.Copy();
                current.Add(new ChronicleOption
                {
                    OptionId = packetAuraItemId,
                    CharacJob = command.CharacterJob,
                    FirstGrowType = command.FirstGrowType,
                    EquipmentType = (byte)equipmentType,
                    OptionNo = command.OptionNo,
                });
                updated.SetChronicleOptions(current);
                if (!inventory.SetItem(InventoryListType.Main, command.TargetSlotIndex, updated))
                {
                    result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorInvalidTarget);
                    return false;
                }
            }
            else
            {
                if (!InventoryDeleteService.TryRemoveSlot(
                        inventory,
                        InventoryListType.Main,
                        command.TargetSlotIndex,
                        out _)
                    || !InventoryRewardGrantService.TryGrantBatch(
                        inventory,
                        rewardRequests,
                        out var grantResult)
                    || !grantResult.Success
                    || grantResult.Results.Count != failureRewards.Count)
                {
                    result = ChronicleRefineResult.Error(command, ChronicleRefineResult.ErrorInventoryFull);
                    return false;
                }

                for (var index = 0; index < failureRewards.Count; index++)
                    failureRewards[index].SlotIndex = grantResult.Results[index].SlotIndex;
            }

            result = new ChronicleRefineResult
            {
                Success = true,
                RefineSucceeded = refineSucceeded,
                TargetDestroyed = !refineSucceeded,
                Command = command,
                MaterialRemainingStackCount = materialDelete.RemainingCount,
                EquipmentType = (byte)materialDefinition.Type,
                OptionCount = (byte)(refineSucceeded ? current.Count : target.ChronicleOptionCount),
                SuccessProbability = probability,
                ProbabilityRoll = roll,
            };
            result.FailureRewards.AddRange(failureRewards);
            return true;
        }

        private static ThreeChronicleEnchantCheck FindChronicleCheck(
            ThreeChronicleEnchantInfo enchant,
            byte characterJob,
            byte firstGrowType,
            string targetEquipmentType,
            byte optionNo)
        {
            if (enchant?.Checks == null)
                return null;

            var targetType = NormalizeEquipmentType(targetEquipmentType);
            foreach (var check in enchant.Checks)
            {
                if (check == null || check.Values.Count < 2
                    || check.Values[0] != characterJob
                    || check.Values[1] != firstGrowType
                    || !string.Equals(
                        NormalizeEquipmentType(check.EquipmentType),
                        targetType,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                if (check.Skills.Exists(skill =>
                    skill.OptionNo == optionNo
                    && ChronicleRefineJobMatcher.Matches(skill.Job, characterJob)))
                    return check;
            }

            return null;
        }

        internal static List<ChronicleOption> NormalizeOptions(
            IReadOnlyList<ChronicleOption> source,
            EquipmentType targetType)
        {
            var result = new List<ChronicleOption>(2);
            if (source == null)
                return result;

            foreach (var raw in source)
            {
                if (raw == null || raw.IsEmpty)
                    continue;

                var option = raw.Copy();
                if (!ChronicleRefineMaterialResolver.TryGetAuraType(option.OptionId, out var auraType))
                {
                    if (option.EquipmentType > 2)
                    {
                        result.Add(option);
                        continue;
                    }

                    auraType = option.EquipmentType;
                }

                if (ChronicleRefineMaterialResolver.TryGetPacketAuraItemId(auraType, out var auraItemId))
                    option.OptionId = auraItemId;
                if (EquipmentTypeInfo.IsUpgradeTargetType(targetType))
                    option.EquipmentType = (byte)targetType;
                result.Add(option);
            }

            return result;
        }

        internal static List<DisjointMaterialResult> BuildFailureRewards(
            ItemMetadata metadata,
            int reinforcementLevel,
            int fragmentItemTemplateId)
        {
            var rewards = new List<DisjointMaterialResult>();
            AddOrMergeReward(rewards, fragmentItemTemplateId, Math.Max(1, reinforcementLevel + 1));
            foreach (var reward in DisjointResultCalculator.Calculate(metadata))
                AddOrMergeReward(rewards, reward.ItemTemplateId, reward.Count);
            return rewards;
        }

        private static void AddOrMergeReward(
            List<DisjointMaterialResult> rewards,
            int itemTemplateId,
            int count)
        {
            if (itemTemplateId <= 0 || count <= 0)
                return;

            foreach (var reward in rewards)
            {
                if (reward.ItemTemplateId != itemTemplateId)
                    continue;
                reward.Count += count;
                return;
            }

            rewards.Add(new DisjointMaterialResult
            {
                SlotIndex = -1,
                ItemTemplateId = itemTemplateId,
                Count = count,
            });
        }

        private static bool BuildGrantRequests(
            IReadOnlyList<DisjointMaterialResult> rewards,
            out List<InventoryRewardGrantRequest> requests)
        {
            requests = new List<InventoryRewardGrantRequest>();
            if (rewards == null)
                return false;

            foreach (var reward in rewards)
            {
                if (reward == null || reward.ItemTemplateId <= 0 || reward.Count <= 0)
                    return false;
                requests.Add(InventoryRewardGrantRequest.Create(
                    reward.ItemTemplateId,
                    reward.Count,
                    ItemCreateReason.Unknown));
            }

            return requests.Count > 0;
        }

        private static bool IsItemLocked(InventoryService inventory, ItemCore core)
        {
            return core.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        private static string NormalizeEquipmentType(string value)
        {
            return (value ?? string.Empty)
                .Trim()
                .Trim((char)96)
                .Trim('[', ']')
                .Trim()
                .ToLowerInvariant();
        }
    }
}
