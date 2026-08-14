using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal enum TeleportConsumableKind
    {
        TownSelection = 1,
        FixedMap = 2,
        Partner = 3,
    }

    internal sealed class TeleportConsumableDefinition
    {
        internal const string TownSelectionTypeTag = "[teleport potion]";
        internal const string FixedMapActionTag = "[move map]";
        internal const string PartnerActionTag = "[teleport to partner]";

        private TeleportConsumableDefinition(
            int itemTemplateId,
            TeleportConsumableKind kind,
            bool isValid,
            byte? targetTownId,
            byte? targetAreaId)
        {
            ItemTemplateId = itemTemplateId;
            Kind = kind;
            IsValid = isValid;
            TargetTownId = targetTownId;
            TargetAreaId = targetAreaId;
        }

        internal int ItemTemplateId { get; }
        internal TeleportConsumableKind Kind { get; }
        internal bool IsValid { get; }
        internal byte? TargetTownId { get; }
        internal byte? TargetAreaId { get; }

        internal static bool TryCreate(
            int itemTemplateId,
            string stackableType,
            string actionType,
            IReadOnlyList<int> actionParameters,
            out TeleportConsumableDefinition definition)
        {
            definition = null;
            if (itemTemplateId <= 0)
                return false;

            var normalizedType = StackableItemProvider.NormalizeType(
                stackableType);
            if (string.Equals(
                    normalizedType,
                    TownSelectionTypeTag,
                    StringComparison.OrdinalIgnoreCase))
            {
                definition = new TeleportConsumableDefinition(
                    itemTemplateId,
                    TeleportConsumableKind.TownSelection,
                    isValid: true,
                    targetTownId: null,
                    targetAreaId: null);
                return true;
            }

            var normalizedAction = StackableItemProvider.NormalizeType(
                actionType);
            if (string.Equals(
                    normalizedAction,
                    FixedMapActionTag,
                    StringComparison.OrdinalIgnoreCase))
            {
                var valid = actionParameters != null
                    && actionParameters.Count >= 5
                    && actionParameters[0] > 0
                    && actionParameters[0] <= byte.MaxValue
                    && actionParameters[1] >= 0
                    && actionParameters[1] <= byte.MaxValue
                    && actionParameters[2] >= short.MinValue
                    && actionParameters[2] <= short.MaxValue
                    && actionParameters[3] >= short.MinValue
                    && actionParameters[3] <= short.MaxValue
                    && actionParameters[4] >= byte.MinValue
                    && actionParameters[4] <= byte.MaxValue;
                definition = new TeleportConsumableDefinition(
                    itemTemplateId,
                    TeleportConsumableKind.FixedMap,
                    valid,
                    valid ? (byte?)actionParameters[0] : null,
                    valid ? (byte?)actionParameters[1] : null);
                return true;
            }

            if (string.Equals(
                    normalizedAction,
                    PartnerActionTag,
                    StringComparison.OrdinalIgnoreCase))
            {
                definition = new TeleportConsumableDefinition(
                    itemTemplateId,
                    TeleportConsumableKind.Partner,
                    actionParameters != null
                        && actionParameters.Count >= 1,
                    targetTownId: null,
                    targetAreaId: null);
                return true;
            }

            return false;
        }
    }

    internal static class TeleportConsumableDefinitionProvider
    {
        private static readonly ConcurrentDictionary<
            int,
            Lazy<TeleportConsumableDefinition>> Cache =
                new ConcurrentDictionary<
                    int,
                    Lazy<TeleportConsumableDefinition>>();

        internal static bool TryResolve(
            int itemTemplateId,
            out TeleportConsumableDefinition definition)
        {
            definition = null;
            if (itemTemplateId <= 0)
                return false;

            definition = Cache.GetOrAdd(
                    itemTemplateId,
                    id => new Lazy<TeleportConsumableDefinition>(
                        () => ResolveCore(id)))
                .Value;
            return definition != null;
        }

        private static TeleportConsumableDefinition ResolveCore(
            int itemTemplateId)
        {
            var stackable = StackableItemProvider.Load(itemTemplateId);
            if (stackable == null)
                return null;

            return TeleportConsumableDefinition.TryCreate(
                itemTemplateId,
                stackable.StackableType,
                stackable.ActionTypeName,
                stackable.ActionTypeParams,
                out var definition)
                    ? definition
                    : null;
        }
    }
}
