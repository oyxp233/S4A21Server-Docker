using System;
using System.Collections.Generic;
using DfoServer.Game.Currency;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryItemUpgradeService
    {
        private const int WeightScale = 100000;
        private static readonly ItemSlotRange QuickSlotRange = new ItemSlotRange(3, 8);

        internal static bool TryUpgradeItem(
            InventoryService inventory,
            ItemUpgradeCommand command,
            out ItemUpgradeResult result)
        {
            if (command == null)
            {
                result = ItemUpgradeResult.Error(null, ItemUpgradeResult.ErrorInvalidTarget);
                return false;
            }

            result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidTarget);
            if (inventory == null)
                return false;

            if (!TryResolveTableKind(command.Method, command.Mode, out var tableKind))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorWrongUpgradeMode);
                return false;
            }

            var target = inventory.GetItem(InventoryListType.Main, command.TargetSlotIndex);
            if (target == null
                || target.ItemKind != ItemCore.KindEquipment
                || target.ItemId != command.TargetItemTemplateId)
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidTarget);
                return false;
            }

            var targetMetadata = ItemMetadataResolver.Resolve(target.ItemId);
            if (targetMetadata == null
                || !string.Equals(targetMetadata.ItemKind, "equipment", StringComparison.Ordinal)
                || IsTitle(targetMetadata))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidTarget);
                return false;
            }

            var currentLevel = target.Upgrade;
            if (currentLevel > 30)
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorMaxLevel);
                return false;
            }

            if (IsItemLocked(inventory, target))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorLocked);
                return false;
            }

            if (!HasExpectedDurability(target, targetMetadata))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorDurability);
                return false;
            }

            if (IsImpossible(command.Mode, targetMetadata))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorRestriction);
                return false;
            }

            var amplify = ResolveAmplifyState(target);
            if (command.Mode == ItemUpgradeMode.Reinforce
                && (amplify.HasUnidentifiedOutworldVigor || amplify.HasAmplifyAttribute))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorWrongUpgradeMode);
                return false;
            }

            if (command.Mode == ItemUpgradeMode.Amplify)
            {
                if (amplify.HasUnidentifiedOutworldVigor)
                {
                    result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorAmplifyNotIdentified);
                    return false;
                }

                if (!amplify.HasAmplifyAttribute)
                {
                    result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorWrongUpgradeMode);
                    return false;
                }

                if (!amplify.IsIdentified)
                {
                    result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorAmplifyNotIdentified);
                    return false;
                }
            }

            var equipmentType = EquipmentTypeInfo.ParseOrUnknown(targetMetadata.EquipmentType);
            if (!EquipmentTypeInfo.IsUpgradeTargetType(equipmentType))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidTarget);
                return false;
            }

            var material = inventory.GetItem(InventoryListType.Main, command.MaterialSlotIndex);
            var materialConfig = tableKind == ItemUpgradeTableKind.Advanced
                ? null
                : ResolveMaterialConfig(material);

            if (!TryBuildContext(
                    command,
                    target,
                    targetMetadata,
                    equipmentType,
                    tableKind,
                    materialConfig,
                    out var context,
                    out var row,
                    out var errorCode))
            {
                result = ItemUpgradeResult.Error(command, errorCode);
                return false;
            }

            if (!ValidateRestrictions(context, target.SealFlag, out errorCode))
            {
                result = ItemUpgradeResult.Error(command, errorCode);
                return false;
            }

            context.EquippedUpgradeProbabilityIncrease =
                ResolveEquippedUpgradeProbabilityIncrease(inventory);

            if (!ValidateMaterial(inventory, command.MaterialSlotIndex, material, context.Cost, out errorCode))
            {
                result = ItemUpgradeResult.Error(command, errorCode);
                return false;
            }

            var oldGold = GetGold(inventory);
            if (oldGold < context.Cost.Gold)
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInsufficientGold);
                return false;
            }

            var chance = SelectChanceEntry(context);
            if (chance == null || chance.TargetLevel < 0)
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorUnsupported);
                return false;
            }

            var penaltyType = ResolvePenaltyType(context, row, tableKind);
            var finalWeight = CalculateFinalSuccessWeight(context, chance);
            var roll = ServerRandom.Next(WeightScale);
            var success = roll < finalWeight;
            var oldLevel = (byte)currentLevel;
            ProtectTicketSelection protectTicket = null;
            var protectedByTicket = false;
            var destroyed = false;
            var effectivePenaltyType = penaltyType;
            byte newLevel;

            if (success)
            {
                newLevel = (byte)Clamp(chance.TargetLevel, 0, 31);
            }
            else if (penaltyType == 3)
            {
                protectTicket = FindFirstProtectTicket(inventory, command.Mode);
                if (protectTicket != null)
                {
                    protectedByTicket = true;
                    effectivePenaltyType = 2;
                    newLevel = (byte)Clamp(protectTicket.Config.FailureRetainLevel, 0, oldLevel);
                }
                else
                {
                    destroyed = true;
                    newLevel = 0;
                }
            }
            else
            {
                newLevel = ApplyPenalty(oldLevel, row, penaltyType, context);
            }

            var resultCode = success ? (byte)0 : (byte)Math.Max(1, effectivePenaltyType);

            var destroyRewardRequests = new List<InventoryRewardGrantRequest>();
            InventoryRewardGrantBatchPlan destroyRewardPlan = null;
            if (destroyed)
            {
                foreach (var bonus in ItemUpgradeTableProvider.CalculateDestroyBonuses(
                    tableKind,
                    chance.TargetLevel,
                    targetMetadata.Grade,
                    targetMetadata.Rarity))
                {
                    if (bonus.HasValue)
                    {
                        destroyRewardRequests.Add(InventoryRewardGrantRequest.Create(
                            bonus.ItemId,
                            bonus.Count,
                            ItemCreateReason.Unknown));
                    }
                }

                if (destroyRewardRequests.Count > 0
                    && (!InventoryRewardGrantService.TryPlanBatch(
                            inventory,
                            destroyRewardRequests,
                            out destroyRewardPlan)
                        || !destroyRewardPlan.Success))
                {
                    result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInventoryFull);
                    return false;
                }
            }

            if (!ConsumeMaterial(inventory, command.MaterialSlotIndex, material, context.Cost, out var materialUpdate))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidMaterial);
                return false;
            }

            ItemUpgradeSlotCount protectTicketUpdate = null;
            if (protectedByTicket
                && !ConsumeMaterial(
                    inventory,
                    protectTicket.SlotIndex,
                    protectTicket.Item,
                    protectTicket.Config.Cost,
                    out protectTicketUpdate))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidMaterial);
                return false;
            }

            if (!TrySpendGold(inventory, context.Cost.Gold, out var updatedGold))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInsufficientGold);
                return false;
            }

            ItemCore targetItemSnapshot;
            if (destroyed)
            {
                targetItemSnapshot = target.Copy();
                if (!inventory.RemoveItem(InventoryListType.Main, command.TargetSlotIndex))
                {
                    result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidTarget);
                    return false;
                }
            }
            else
            {
                var updatedTarget = target.Copy();
                updatedTarget.Upgrade = newLevel;
                if (!inventory.SetItem(InventoryListType.Main, command.TargetSlotIndex, updatedTarget))
                {
                    result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidTarget);
                    return false;
                }
                targetItemSnapshot = updatedTarget;
            }

            var destroyRewardItems = new List<ItemUpgradeRewardItem>();
            if (destroyed)
            {
                InventoryRewardGrantBatchResult rewardBatch = null;
                if (destroyRewardRequests.Count > 0
                    && (!InventoryRewardGrantService.TryApplyPreparedBatch(
                            inventory,
                            destroyRewardPlan,
                            out rewardBatch)
                        || !rewardBatch.Success
                        || rewardBatch.Results.Count != destroyRewardRequests.Count))
                {
                    result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInventoryFull);
                    return false;
                }

                for (var index = 0; index < destroyRewardRequests.Count; index++)
                {
                    var rewardGrant = rewardBatch.Results[index];
                    destroyRewardItems.Add(new ItemUpgradeRewardItem
                    {
                        SlotIndex = rewardGrant.SlotIndex,
                        ItemTemplateId = rewardGrant.ItemTemplateId,
                        Count = rewardGrant.GrantedCount,
                    });
                }
            }

            var upgradeResult = new ItemUpgradeResult
            {
                Command = command,
                Success = true,
                Method = command.Method,
                Mode = command.Mode,
                Scene = context.Scene,
                TargetSlotIndex = command.TargetSlotIndex,
                TargetItemTemplateId = command.TargetItemTemplateId,
                MaterialSlotIndex = command.MaterialSlotIndex,
                MaterialItemTemplateId = context.Cost != null ? context.Cost.MaterialItemId : 0,
                OptionalTicketSlotIndex = -1,
                OldLevel = oldLevel,
                NewLevel = newLevel,
                ResultCode = resultCode,
                UpgradeSucceeded = success,
                FinalSuccessWeight = finalWeight,
                MaterialRemainingStackCount = materialUpdate != null ? materialUpdate.Count : 0,
                GoldCost = context.Cost != null ? context.Cost.Gold : 0,
                UpdatedGold = updatedGold,
                NoticeRequired = success
                    ? ItemUpgradeTableProvider.IsNoticeLevel(tableKind, newLevel)
                    : ItemUpgradeTableProvider.IsNoticeLevel(tableKind, oldLevel),
                TargetItemSnapshot = targetItemSnapshot,
            };

            AddRefreshSlot(upgradeResult.MainRefreshSlots, command.TargetSlotIndex);
            if (materialUpdate != null)
                AddRefreshSlot(upgradeResult.MainRefreshSlots, materialUpdate.SlotIndex);
            if (protectTicketUpdate != null)
                AddRefreshSlot(upgradeResult.MainRefreshSlots, protectTicketUpdate.SlotIndex);
            foreach (var reward in destroyRewardItems)
            {
                AddRefreshSlot(upgradeResult.MainRefreshSlots, reward.SlotIndex);
                upgradeResult.DestroyRewardItems.Add(reward);
            }

            result = upgradeResult;
            return true;
        }

        private static bool TryResolveTableKind(
            ItemUpgradeMethod method,
            ItemUpgradeMode mode,
            out ItemUpgradeTableKind tableKind)
        {
            switch (method)
            {
                case ItemUpgradeMethod.Reinforce:
                    tableKind = ItemUpgradeTableKind.Normal;
                    return mode == ItemUpgradeMode.Reinforce;
                case ItemUpgradeMethod.Amplify:
                    tableKind = ItemUpgradeTableKind.Amplify;
                    return mode == ItemUpgradeMode.Amplify;
                case ItemUpgradeMethod.AdvancedReinforce:
                    tableKind = ItemUpgradeTableKind.Advanced;
                    return mode == ItemUpgradeMode.Reinforce;
                default:
                    tableKind = ItemUpgradeTableKind.Normal;
                    return false;
            }
        }

        private static bool TryBuildContext(
            ItemUpgradeCommand command,
            ItemCore target,
            ItemMetadata targetMetadata,
            EquipmentType equipmentType,
            ItemUpgradeTableKind tableKind,
            ItemUpgradeConsumableConfig materialConfig,
            out ItemUpgradeContext context,
            out UpgradeTableRow row,
            out byte errorCode)
        {
            context = null;
            row = null;
            errorCode = ItemUpgradeResult.ErrorUnsupported;

            var currentLevel = target.Upgrade;
            var targetLevel = currentLevel + 1;
            var input = new EquipmentUpgradeCostInput
            {
                EquipmentLevel = targetMetadata.MinimumLevel,
                Rarity = targetMetadata.Rarity,
                EquipmentType = equipmentType,
                CurrentUpgradeLevel = currentLevel,
            };

            context = new ItemUpgradeContext
            {
                Mode = command.Mode,
                TargetSlotIndex = command.TargetSlotIndex,
                TargetItemTemplateId = command.TargetItemTemplateId,
                CurrentUpgradeLevel = currentLevel,
                EquipmentType = equipmentType,
                EquipmentLevel = targetMetadata.MinimumLevel,
                EquipmentGrade = targetMetadata.Grade,
                EquipmentRarity = targetMetadata.Rarity,
            };

            if (materialConfig == null)
            {
                if (!ItemUpgradeTableProvider.TryGetRow(tableKind, targetLevel, out row))
                {
                    errorCode = ItemUpgradeResult.ErrorMaxLevel;
                    return false;
                }

                context.Scene = ItemUpgradeScene.Npc;
                context.ConsumableKind = ItemUpgradeConsumableKind.None;
                context.Cost = ItemUpgradeTableProvider.BuildCost(tableKind, row, input);
                context.ChanceEntries.Add(new ItemUpgradeChanceEntry
                {
                    TargetLevel = targetLevel,
                    BaseSuccessWeight = row.DerivedSuccessWeight,
                    BaseFailureWeight = row.FailureWeight,
                });
                return true;
            }

            if (materialConfig.Mode != command.Mode)
            {
                errorCode = ItemUpgradeResult.ErrorWrongUpgradeMode;
                return false;
            }

            if (!AllowsConsumableCurrentLevel(materialConfig, currentLevel))
            {
                errorCode = ItemUpgradeResult.ErrorRestriction;
                return false;
            }

            context.Scene = materialConfig.Scene;
            context.ConsumableKind = materialConfig.Kind;
            context.Restriction = materialConfig.Restriction ?? new ItemUpgradeRestriction();
            context.Cost = materialConfig.Cost ?? new ItemUpgradeCost();
            context.SuccessRateAddWeight = materialConfig.SuccessRateAddWeight;
            context.SuccessRateBonusWeight = materialConfig.SuccessRateBonusWeight;
            context.FailureRetainLevel = materialConfig.FailureRetainLevel;

            if (materialConfig.Scene == ItemUpgradeScene.Ticket)
            {
                foreach (var chance in materialConfig.ChanceEntries)
                    context.ChanceEntries.Add(chance);

                if (context.ChanceEntries.Count == 0)
                {
                    errorCode = ItemUpgradeResult.ErrorUnsupported;
                    return false;
                }

                return true;
            }

            if (!ItemUpgradeTableProvider.TryGetRow(tableKind, targetLevel, out row))
            {
                errorCode = ItemUpgradeResult.ErrorMaxLevel;
                return false;
            }

            context.ChanceEntries.Add(new ItemUpgradeChanceEntry
            {
                TargetLevel = targetLevel,
                BaseSuccessWeight = row.DerivedSuccessWeight,
                BaseFailureWeight = row.FailureWeight,
            });
            return true;
        }

        private static bool ValidateRestrictions(ItemUpgradeContext context, byte targetSealFlag, out byte errorCode)
        {
            errorCode = ItemUpgradeResult.ErrorRestriction;
            var restriction = context.Restriction ?? new ItemUpgradeRestriction();

            if (!restriction.AllowsEquipmentType(context.EquipmentType))
                return false;

            if (!restriction.AllowsRarity(context.EquipmentRarity))
                return false;

            if (!restriction.AllowsItemLevel(context.EquipmentLevel))
                return false;

            if (restriction.SealRestriction == 1 && targetSealFlag == 0)
                return false;

            return true;
        }

        private static bool ValidateMaterial(
            InventoryService inventory,
            short materialSlotIndex,
            ItemCore material,
            ItemUpgradeCost cost,
            out byte errorCode)
        {
            errorCode = ItemUpgradeResult.ErrorInvalidMaterial;
            if (cost == null || cost.MaterialItemId <= 0 || cost.MaterialCount <= 0)
                return true;

            if (CurrencyService.IsCubeFragment(cost.MaterialItemId))
            {
                var cubeSlot = (short)CurrencyService.GetCubeFragmentSlot(cost.MaterialItemId);
                var cubeCount = inventory.GetMainVirtualCount(cubeSlot)?.Count ?? 0;
                if (cubeCount >= cost.MaterialCount)
                    return true;
            }

            return IsStackableMaterial(material, cost.MaterialItemId)
                && material.Count >= cost.MaterialCount
                && materialSlotIndex >= 0;
        }

        private static bool ConsumeMaterial(
            InventoryService inventory,
            short materialSlotIndex,
            ItemCore material,
            ItemUpgradeCost cost,
            out ItemUpgradeSlotCount materialUpdate)
        {
            materialUpdate = null;
            if (cost == null || cost.MaterialItemId <= 0 || cost.MaterialCount <= 0)
                return true;

            if (CurrencyService.IsCubeFragment(cost.MaterialItemId))
            {
                var cubeSlot = (short)CurrencyService.GetCubeFragmentSlot(cost.MaterialItemId);
                var cubeCount = inventory.GetMainVirtualCount(cubeSlot)?.Count ?? 0;
                if (cubeCount >= cost.MaterialCount)
                {
                    var remainingCubeCount = cubeCount - cost.MaterialCount;
                    if (!inventory.SetMainVirtualCount(cubeSlot, remainingCubeCount))
                        return false;

                    materialUpdate = CreateSlotCount(cubeSlot, cost.MaterialItemId, remainingCubeCount);
                    return true;
                }
            }

            if (!IsStackableMaterial(material, cost.MaterialItemId) || material.Count < cost.MaterialCount)
                return false;

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    materialSlotIndex,
                    cost.MaterialCount,
                    out var delete))
                return false;

            materialUpdate = CreateSlotCount(materialSlotIndex, cost.MaterialItemId, delete.RemainingCount);
            return true;
        }

        private static ProtectTicketSelection FindFirstProtectTicket(InventoryService inventory, ItemUpgradeMode mode)
        {
            var ticket = FindFirstProtectTicketInRange(inventory, mode, QuickSlotRange);
            if (ticket != null)
                return ticket;

            if (!ItemSlotBoundService.TryGetSlotRange(
                    ItemCore.KindMaterial,
                    inventory.GetListParam16(InventoryListType.Main),
                    out var listType,
                    out var materialRange)
                || listType != InventoryListType.Main)
                return null;

            return FindFirstProtectTicketInRange(inventory, mode, materialRange);
        }

        private static ProtectTicketSelection FindFirstProtectTicketInRange(
            InventoryService inventory,
            ItemUpgradeMode mode,
            ItemSlotRange range)
        {
            for (var slot = range.Start; slot <= range.End; slot++)
            {
                var item = inventory.GetItem(InventoryListType.Main, slot);
                if (item == null || item.Count <= 0)
                    continue;

                var config = ResolveMaterialConfig(item);
                if (IsProtectTicketForMode(config, mode))
                    return new ProtectTicketSelection { SlotIndex = slot, Item = item, Config = config };
            }

            return null;
        }

        private static bool IsProtectTicketForMode(ItemUpgradeConsumableConfig config, ItemUpgradeMode mode)
        {
            if (config == null || config.Mode != mode)
                return false;

            return mode == ItemUpgradeMode.Amplify
                ? config.Kind == ItemUpgradeConsumableKind.ProtectAmplify
                : config.Kind == ItemUpgradeConsumableKind.ProtectReinforcement;
        }

        private static ItemUpgradeChanceEntry SelectChanceEntry(ItemUpgradeContext context)
        {
            if (context.ChanceEntries == null || context.ChanceEntries.Count == 0)
                return null;

            if (context.ChanceEntries.Count == 1)
                return context.ChanceEntries[0];

            var totalWeight = 0;
            foreach (var entry in context.ChanceEntries)
                totalWeight += Math.Max(0, entry.BaseSuccessWeight);

            if (totalWeight <= 0)
                return null;

            var roll = ServerRandom.Next(totalWeight);
            var cursor = 0;
            foreach (var entry in context.ChanceEntries)
            {
                cursor += Math.Max(0, entry.BaseSuccessWeight);
                if (roll < cursor)
                {
                    return new ItemUpgradeChanceEntry
                    {
                        TargetLevel = entry.TargetLevel,
                        BaseSuccessWeight = WeightScale,
                        BaseFailureWeight = 0,
                    };
                }
            }

            return context.ChanceEntries[context.ChanceEntries.Count - 1];
        }

        private static int CalculateFinalSuccessWeight(ItemUpgradeContext context, ItemUpgradeChanceEntry chance)
        {
            var baseWeight = Clamp(chance.BaseSuccessWeight, 0, WeightScale);
            if (context.Scene == ItemUpgradeScene.Ticket)
                return baseWeight;

            var additiveWeight = (long)baseWeight
                + context.SuccessRateAddWeight
                + context.EquippedUpgradeProbabilityIncrease;
            additiveWeight = Math.Max(0L, Math.Min(WeightScale, additiveWeight));

            var multiplierWeight = Math.Max(0L, WeightScale + (long)context.SuccessRateBonusWeight);
            var finalWeight = additiveWeight * multiplierWeight / WeightScale;
            return (int)Math.Max(0L, Math.Min(WeightScale, finalWeight));
        }

        private static int ResolveEquippedUpgradeProbabilityIncrease(InventoryService inventory)
        {
            var title = inventory?.GetItem(
                InventoryListType.Equipment,
                (short)EquipmentType.TitleName);
            if (title == null
                || title.ItemKind != ItemCore.KindEquipment
                || !ItemMetadataResolver.TryLoadEquipmentFile(title.ItemId, out var equipment)
                || EquipmentTypeInfo.ParseOrUnknown(equipment.EquipmentType) != EquipmentType.TitleName)
            {
                return 0;
            }

            return Math.Max(0, equipment.UpgradeProbabilityIncrease);
        }

        private static int ResolvePenaltyType(ItemUpgradeContext context, UpgradeTableRow row, ItemUpgradeTableKind tableKind)
        {
            if (context.Scene == ItemUpgradeScene.Ticket)
                return 1;

            return ItemUpgradeTableProvider.GetPenaltyType(
                tableKind,
                row,
                context.CurrentUpgradeLevel,
                context.EquipmentRarity);
        }

        private static byte ApplyPenalty(byte oldLevel, UpgradeTableRow row, int penaltyType, ItemUpgradeContext context)
        {
            if (context.FailureRetainLevel >= 0 && oldLevel >= context.ProtectTriggerLevel)
                return (byte)Clamp(context.FailureRetainLevel, 0, oldLevel);

            if (penaltyType == 2 && row != null)
                return (byte)Math.Max(0, oldLevel - Math.Max(0, row.PenaltyValue));

            return oldLevel;
        }

        private static ItemUpgradeConsumableConfig ResolveMaterialConfig(ItemCore material)
        {
            if (material == null)
                return null;

            return ItemMetadataResolver.TryLoadStackableFile(material.ItemId, out var stackable)
                && ItemUpgradeConsumableResolver.TryResolve(material.ItemId, stackable, out var config)
                    ? config
                    : null;
        }

        private static bool IsStackableMaterial(ItemCore material, int expectedItemTemplateId)
        {
            return material != null
                && material.ItemId == expectedItemTemplateId
                && material.Count > 0
                && ItemMetadataResolver.GetStackableEntry(material.ItemId) != null;
        }

        private static bool TrySpendGold(InventoryService inventory, int goldCost, out int updatedGold)
        {
            updatedGold = GetGold(inventory);
            if (goldCost <= 0)
                return true;

            if (updatedGold < goldCost)
                return false;

            updatedGold -= goldCost;
            return inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, updatedGold);
        }

        private static int GetGold(InventoryService inventory)
        {
            return inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
        }

        private static bool IsItemLocked(InventoryService inventory, ItemCore core)
        {
            return inventory != null
                && core != null
                && core.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        private static AmplifyState ResolveAmplifyState(ItemCore item)
        {
            if (item == null)
                return new AmplifyState();

            var rawType = item.AmplifyType;
            var type = (byte)(rawType & 0x7F);
            var hasUnidentifiedOutworldVigor = (rawType & 0x80) != 0;
            var hasAttribute = type >= (byte)AmplifyAttributeType.Vitality
                && type <= (byte)AmplifyAttributeType.Intelligence;
            return new AmplifyState
            {
                HasUnidentifiedOutworldVigor = hasUnidentifiedOutworldVigor,
                HasAmplifyAttribute = hasAttribute,
                IsIdentified = hasAttribute && !hasUnidentifiedOutworldVigor && item.AmplifyValue > 0,
            };
        }

        private static bool HasExpectedDurability(ItemCore target, ItemMetadata targetMetadata)
        {
            if (targetMetadata.Durability > 0)
                return target.Durability == targetMetadata.Durability;

            return target.Durability == 0;
        }

        private static bool AllowsConsumableCurrentLevel(ItemUpgradeConsumableConfig config, int currentLevel)
        {
            if (config == null || config.Scene != ItemUpgradeScene.Portable)
                return true;

            if (config.ActionTypeParams == null || config.ActionTypeParams.Count < 2)
                return true;

            return currentLevel >= config.ActionTypeParams[0] && currentLevel <= config.ActionTypeParams[1];
        }

        private static bool IsImpossible(ItemUpgradeMode mode, ItemMetadata metadata)
        {
            if (metadata.ImpossibleContents == null)
                return false;

            var token = mode == ItemUpgradeMode.Amplify ? "amplify upgrade" : "upgrade";
            foreach (var item in metadata.ImpossibleContents)
            {
                if (string.Equals(item, token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsTitle(ItemMetadata metadata)
        {
            return string.Equals(metadata.EquipmentType, "[title name]", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddRefreshSlot(ICollection<short> slots, short slotIndex)
        {
            if (slots == null || slotIndex < 0 || slots.Contains(slotIndex))
                return;

            slots.Add(slotIndex);
        }

        private static ItemUpgradeSlotCount CreateSlotCount(short slotIndex, int itemTemplateId, int count)
        {
            return new ItemUpgradeSlotCount
            {
                SlotIndex = slotIndex,
                ItemTemplateId = itemTemplateId,
                Count = Math.Max(0, count),
            };
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private sealed class ProtectTicketSelection
        {
            public short SlotIndex { get; set; }

            public ItemCore Item { get; set; }

            public ItemUpgradeConsumableConfig Config { get; set; }
        }

        private struct AmplifyState
        {
            public bool HasUnidentifiedOutworldVigor { get; set; }

            public bool HasAmplifyAttribute { get; set; }

            public bool IsIdentified { get; set; }
        }
    }
}
