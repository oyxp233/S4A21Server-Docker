using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.Game.Dungeon
{
    internal enum DimensionDungeonEnterTicketStatus
    {
        NotApplicable = 0,
        Success,
        LocationRestricted,
        InsufficientMaterial,
        ConsumeFailed,
        LimitUpdateFailed,
        ExpiredRemoved,
    }

    internal sealed class DimensionDungeonEnterTicketDefinition
    {
        internal const string ActionType = "[add dimension dungeon enter count]";

        internal DimensionDungeonEnterTicketDefinition(
            int enterCount,
            int materialItemTemplateId,
            int materialCount,
            bool townOnly)
        {
            EnterCount = enterCount;
            MaterialItemTemplateId = materialItemTemplateId;
            MaterialCount = materialCount;
            TownOnly = townOnly;
        }

        internal int EnterCount { get; }

        internal int MaterialItemTemplateId { get; }

        internal int MaterialCount { get; }

        internal bool TownOnly { get; }

        internal bool RequiresMaterial
            => MaterialItemTemplateId > 0 && MaterialCount > 0;
    }

    internal static class DimensionDungeonEnterTicketRules
    {
        private static readonly Regex PlacePattern = new Regex(
            "`\\[([^\\]]+)\\]`",
            RegexOptions.Compiled);

        internal static bool TryResolve(
            StackableItemFile stackable,
            out DimensionDungeonEnterTicketDefinition definition)
        {
            definition = null;
            if (stackable == null)
                return false;

            var action = StackableItemProvider.NormalizeType(stackable.ActionTypeName);
            if (!string.Equals(
                    action,
                    DimensionDungeonEnterTicketDefinition.ActionType,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (stackable.ActionTypeParams == null
                || stackable.ActionTypeParams.Count == 0
                || stackable.ActionTypeParams[0] <= 0)
            {
                return false;
            }

            var materialItemId = 0;
            var materialCount = 0;
            var materials = ItemMetadataResolver.ParseNeedMaterials(stackable.NeedMaterial);
            if (materials.Count > 0)
            {
                materialItemId = materials[0].ItemId;
                materialCount = materials[0].Count;
            }

            definition = new DimensionDungeonEnterTicketDefinition(
                stackable.ActionTypeParams[0],
                materialItemId,
                materialCount,
                RequiresTown(stackable));
            return true;
        }

        private static bool RequiresTown(StackableItemFile stackable)
        {
            if (stackable.Root == null)
                return false;

            var nodes = stackable.Root.GetChildren("action usable place");
            if (nodes == null || nodes.Count == 0)
                return false;

            var sawPlace = false;
            foreach (var node in nodes)
            {
                if (node?.DataItems == null)
                    continue;

                foreach (var item in node.DataItems)
                {
                    var raw = item.GetContent(stackable.Content) ?? string.Empty;
                    foreach (Match match in PlacePattern.Matches(raw))
                    {
                        sawPlace = true;
                        var place = match.Groups[1].Value.Trim();
                        if (!place.Equals("village", StringComparison.OrdinalIgnoreCase)
                            && !place.Equals("seria room", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }
                }
            }

            return sawPlace;
        }
    }

    internal sealed class DimensionDungeonEnterTicketUseResult
    {
        internal DimensionDungeonEnterTicketStatus Status { get; set; }

        internal int ItemTemplateId { get; set; }

        internal int AddedEnterCount { get; set; }

        internal int RemainingCount { get; set; }

        internal int ExtraCount { get; set; }

        internal bool SourceExpiredDeleted { get; set; }

        internal InventoryMutationResult TicketMutation { get; set; }

        internal List<InventoryMaterialConsumptionEntry> Materials { get; }
            = new List<InventoryMaterialConsumptionEntry>();

        internal bool Handled
            => Status != DimensionDungeonEnterTicketStatus.NotApplicable;

        internal bool Success
            => Status == DimensionDungeonEnterTicketStatus.Success;
    }

    internal static class DimensionDungeonEnterTicketService
    {
        internal static DimensionDungeonEnterTicketUseResult TryUse(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int expectedItemId,
            bool inTown,
            StackableItemFile stackable,
            DimensionDungeonEnterTicketDefinition definition,
            int defaultCurrentCount,
            int defaultExtraCount,
            DungeonEntryLimitService entryLimits)
        {
            var result = new DimensionDungeonEnterTicketUseResult
            {
                ItemTemplateId = expectedItemId,
            };
            if (definition == null || stackable == null || entryLimits == null)
            {
                result.Status = DimensionDungeonEnterTicketStatus.NotApplicable;
                return result;
            }

            result.AddedEnterCount = definition.EnterCount;
            if (definition.TownOnly && !inTown)
            {
                result.Status = DimensionDungeonEnterTicketStatus.LocationRestricted;
                return result;
            }

            if (lease?.Inventory?.Database == null)
            {
                result.Status = DimensionDungeonEnterTicketStatus.LimitUpdateFailed;
                return result;
            }

            InventoryItemLifecycleUsePlan lifecyclePlan = null;
            InventoryMutationResult ticketMutation = null;
            DimensionGateEntryLimitSnapshot snapshot = null;
            var expiredDeleted = false;
            var failure = DimensionDungeonEnterTicketStatus.ConsumeFailed;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "dimension-dungeon-enter-ticket",
                (connection, transaction) =>
                {
                    lifecyclePlan = InventoryItemLifecycleService.PrepareUseWithDefinition(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        expectedItemId,
                        InventoryItemLifecycleService.UtcNowUnixSeconds(),
                        1,
                        stackable);
                    if (lifecyclePlan.SourceExpiredDeleted)
                    {
                        ticketMutation = lifecyclePlan.SourceMutation;
                        expiredDeleted = true;
                        return true;
                    }

                    if (!lifecyclePlan.Success)
                    {
                        failure = DimensionDungeonEnterTicketStatus.ConsumeFailed;
                        return false;
                    }

                    if (!UsableCountLimitService.TryRecordUseIfLimited(
                            connection,
                            transaction,
                            lease.CharacterId,
                            lifecyclePlan.ItemTemplateId,
                            1,
                            out var usableCountState))
                    {
                        failure = DimensionDungeonEnterTicketStatus.ConsumeFailed;
                        return false;
                    }

                    if (!InventoryDeleteService.TryUseStackableForClient(
                            lease.Inventory,
                            listType,
                            slotIndex,
                            lifecyclePlan.ItemTemplateId,
                            out ticketMutation))
                    {
                        failure = DimensionDungeonEnterTicketStatus.ConsumeFailed;
                        return false;
                    }

                    if (definition.RequiresMaterial)
                    {
                        var requirements = new List<InventoryMaterialRequirement>
                        {
                            new InventoryMaterialRequirement(
                                definition.MaterialItemTemplateId,
                                definition.MaterialCount),
                        };
                        if (!InventoryMaterialConsumptionService.HasEnough(
                                lease.Inventory,
                                requirements)
                            || !InventoryMaterialConsumptionService.TryConsume(
                                lease.Inventory,
                                requirements,
                                result.Materials))
                        {
                            failure = DimensionDungeonEnterTicketStatus.InsufficientMaterial;
                            return false;
                        }
                    }

                    if (!entryLimits.TryAddDimensionGateExtraCount(
                            connection,
                            transaction,
                            lease.CharacterId,
                            defaultCurrentCount,
                            defaultExtraCount,
                            definition.EnterCount,
                            out snapshot))
                    {
                        failure = DimensionDungeonEnterTicketStatus.LimitUpdateFailed;
                        return false;
                    }

                    InventoryItemLifecycleService.ApplyUseSuccess(
                        lease.Inventory,
                        lifecyclePlan);
                    if (ticketMutation != null)
                        ticketMutation.UsableCountState = usableCountState;
                    return true;
                });

            result.ItemTemplateId = lifecyclePlan?.ItemTemplateId > 0
                ? lifecyclePlan.ItemTemplateId
                : expectedItemId;
            result.TicketMutation = ticketMutation;
            result.SourceExpiredDeleted = expiredDeleted;
            if (!committed)
            {
                result.Materials.Clear();
                result.Status = failure;
                return result;
            }

            if (expiredDeleted)
            {
                result.Status = DimensionDungeonEnterTicketStatus.ExpiredRemoved;
                return result;
            }

            result.RemainingCount = snapshot?.CurrentCount ?? defaultCurrentCount;
            result.ExtraCount = snapshot?.ExtraCount ?? 0;
            result.Status = DimensionDungeonEnterTicketStatus.Success;
            return result;
        }
    }
}
