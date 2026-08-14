using System;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryStackRejectReason
    {
        None = 0,
        InvalidItem = 1,
        NotStackable = 2,
        DifferentItem = 3,
        InvalidCount = 4,
        StackLimitExceeded = 5,
        MetadataNotFound = 6,
        DifferentState = 7,
    }

    internal readonly struct InventoryStackPlan
    {
        public InventoryStackPlan(int acceptedCount, int remainingCount)
        {
            AcceptedCount = acceptedCount;
            RemainingCount = remainingCount;
        }

        public int AcceptedCount { get; }

        public int RemainingCount { get; }
    }

    internal static class InventoryStackRuleService
    {
        internal static bool IsStackable(ItemCore item)
        {
            return item != null
                && !item.IsEmpty
                && !item.IsEquipmentItem();
        }

        internal static bool CanShareStack(ItemCore source, ItemCore destination)
        {
            return source != null
                && destination != null
                && source.ItemId == destination.ItemId
                && IsStackable(source)
                && IsStackable(destination)
                && source.EnchantUpgradeCount == destination.EnchantUpgradeCount;
        }

        internal static int NormalizeInsertCount(ItemCore item, int requestedCount)
        {
            if (!IsStackable(item))
                return 1;

            return requestedCount > 0 ? requestedCount : item.Count;
        }

        internal static bool TryGetStackLimit(ItemCore item, out int stackLimit)
        {
            stackLimit = 1;
            if (item == null || item.IsEmpty || item.ItemId <= 0)
                return false;

            if (!IsStackable(item))
                return true;

            ItemMetadata metadata;
            try
            {
                metadata = ItemMetadataResolver.Resolve(item.ItemId);
            }
            catch
            {
                return false;
            }

            if (metadata == null || !metadata.IsStackable)
                return false;

            stackLimit = metadata.StackLimit > 0 ? metadata.StackLimit : int.MaxValue;
            return true;
        }

        internal static bool TryPlanMerge(
            ItemCore source,
            ItemCore destination,
            int requestedCount,
            out InventoryStackPlan plan,
            out InventoryStackRejectReason rejectReason)
        {
            plan = default;
            rejectReason = InventoryStackRejectReason.None;

            if (source == null || destination == null || source.IsEmpty || destination.IsEmpty)
            {
                rejectReason = InventoryStackRejectReason.InvalidItem;
                return false;
            }

            if (source.ItemId != destination.ItemId)
            {
                rejectReason = InventoryStackRejectReason.DifferentItem;
                return false;
            }

            if (!IsStackable(source) || !IsStackable(destination))
            {
                rejectReason = InventoryStackRejectReason.NotStackable;
                return false;
            }

            if (!CanShareStack(source, destination))
            {
                rejectReason = InventoryStackRejectReason.DifferentState;
                return false;
            }

            var sourceCount = NormalizeInsertCount(source, requestedCount);
            if (sourceCount <= 0)
            {
                rejectReason = InventoryStackRejectReason.InvalidCount;
                return false;
            }

            if (!TryGetStackLimit(source, out var stackLimit))
            {
                rejectReason = InventoryStackRejectReason.MetadataNotFound;
                return false;
            }

            var freeCount = stackLimit == int.MaxValue
                ? int.MaxValue
                : Math.Max(0, stackLimit - destination.Count);
            if (freeCount <= 0 || sourceCount > freeCount)
            {
                rejectReason = InventoryStackRejectReason.StackLimitExceeded;
                return false;
            }

            plan = new InventoryStackPlan(sourceCount, 0);
            return true;
        }
    }
}
