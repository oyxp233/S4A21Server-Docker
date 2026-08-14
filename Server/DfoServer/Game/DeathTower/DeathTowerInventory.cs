using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.DeathTower
{
    public readonly struct DeathTowerInventoryEndpoint : IEquatable<DeathTowerInventoryEndpoint>
    {
        public DeathTowerInventoryEndpoint(
            InventoryListType listType,
            short slotIndex)
        {
            ListType = listType;
            SlotIndex = slotIndex;
        }

        public InventoryListType ListType { get; }
        public short SlotIndex { get; }

        public bool Equals(DeathTowerInventoryEndpoint other)
            => ListType == other.ListType && SlotIndex == other.SlotIndex;

        public override bool Equals(object obj)
            => obj is DeathTowerInventoryEndpoint other && Equals(other);

        public override int GetHashCode()
            => ((int)ListType * 397) ^ SlotIndex;

        public override string ToString()
            => $"{ListType}:{SlotIndex}";
    }

    public sealed class TowerInventoryItem
    {
        public int ItemId { get; internal set; }
        public int Count { get; internal set; }
        public int StackLimit { get; internal set; }
        // Direct-use eligibility is independent from where an item may be moved.
        // Keep the legacy IsWaste bit for compatibility with older snapshots.
        public bool IsQuickSlotConsumable { get; internal set; }
        public bool IsWaste { get; internal set; }

        internal TowerInventoryItem Copy()
        {
            return new TowerInventoryItem
            {
                ItemId = ItemId,
                Count = Count,
                StackLimit = StackLimit,
                IsQuickSlotConsumable = IsQuickSlotConsumable,
                IsWaste = IsWaste,
            };
        }
    }

    public sealed class TowerPickupResult
    {
        public short DestinationSlot { get; internal set; }
        internal DeathTowerInventoryEndpoint DestinationEndpoint { get; set; }
        public int ItemId { get; internal set; }
        public IReadOnlyList<short> ChangedSlots { get; internal set; } = Array.Empty<short>();
        internal IReadOnlyList<DeathTowerInventoryEndpoint> ChangedEndpoints { get; set; }
            = Array.Empty<DeathTowerInventoryEndpoint>();
    }

    public sealed class TowerInventoryMutation
    {
        public int ItemId { get; internal set; }
        public int RemainingCount { get; internal set; }
        public IReadOnlyList<short> ChangedSlots { get; internal set; } = Array.Empty<short>();
        internal DeathTowerInventoryEndpoint Endpoint { get; set; }
    }

    public sealed class TowerInventoryMoveResult
    {
        public int MoveValue32 { get; internal set; }
        internal byte ErrorCode { get; set; } = 0x04;
        public IReadOnlyList<short> ChangedSlots { get; internal set; } = Array.Empty<short>();
        internal IReadOnlyList<DeathTowerInventoryEndpoint> ChangedEndpoints { get; set; }
            = Array.Empty<DeathTowerInventoryEndpoint>();
        internal bool ReversedRequest { get; set; }
        internal bool IdentityResolved { get; set; }
    }

    internal sealed class DeathTowerInventorySortResult
    {
        internal bool Success { get; set; }
        internal bool Mutated { get; set; }
        internal IReadOnlyList<short> ChangedSlots { get; set; } = Array.Empty<short>();
        internal IReadOnlyList<DeathTowerInventoryEndpoint> ChangedEndpoints { get; set; }
            = Array.Empty<DeathTowerInventoryEndpoint>();
    }

    internal readonly struct DeathTowerDeleteItemEntry
    {
        internal DeathTowerDeleteItemEntry(
            ushort operationType,
            short slotIndex,
            int itemId,
            int deleteCount)
        {
            OperationType = operationType;
            SlotIndex = slotIndex;
            ItemId = itemId;
            DeleteCount = deleteCount;
        }

        internal ushort OperationType { get; }
        internal short SlotIndex { get; }
        internal int ItemId { get; }
        internal int DeleteCount { get; }
        internal bool IsSkillMaterialOperation => OperationType > 1;
    }

    internal sealed class DeathTowerDeleteItemCommand
    {
        internal DeathTowerDeleteItemCommand(
            InventoryListType listType,
            IReadOnlyList<DeathTowerDeleteItemEntry> entries)
        {
            ListType = listType;
            Entries = entries ?? Array.Empty<DeathTowerDeleteItemEntry>();
        }

        internal InventoryListType ListType { get; }
        internal IReadOnlyList<DeathTowerDeleteItemEntry> Entries { get; }
    }

    internal sealed class DeathTowerDeleteItemResult
    {
        internal bool Success { get; set; }
        internal IReadOnlyList<InventoryMutationResult> Mutations { get; set; }
            = Array.Empty<InventoryMutationResult>();
        internal IReadOnlyList<int> TransientItemIds { get; set; }
            = Array.Empty<int>();
        internal IReadOnlyList<DeathTowerInventoryEndpoint> ChangedEndpoints { get; set; }
            = Array.Empty<DeathTowerInventoryEndpoint>();
    }

    internal static class DeathTowerItemSlotPolicy
    {
        internal static bool IsWaste(ItemMetadata metadata)
            => metadata != null && metadata.IsPrimaryStackableFamily("waste");

        internal static bool IsQuickSlotConsumable(ItemMetadata metadata)
            => IsWaste(metadata);

        internal static bool PrefersQuickSlotAllocation(ItemMetadata metadata)
            => IsWaste(metadata);

        internal static bool CanMoveToQuickSlot(ItemMetadata metadata)
            => IsWaste(metadata) || HasPrimaryTag(metadata, "material instant item");

        internal static int ResolveStackLimit(ItemMetadata metadata)
        {
            if (metadata == null || !metadata.IsStackable)
                return 1;
            return metadata.StackLimit > 0 ? metadata.StackLimit : int.MaxValue;
        }

        internal static IReadOnlyList<DeathTowerInventoryEndpoint> GetAllocationOrder(
            int itemId,
            ItemMetadata metadata,
            int mainExpandStageKey = ItemSlotBoundService.MainExpandStageFull)
        {
            var result = new List<DeathTowerInventoryEndpoint>();
            if (PrefersQuickSlotAllocation(metadata))
            {
                AppendRange(
                    result,
                    InventoryListType.QuickSlot,
                    ItemSlotBoundService.MainQuickSlotStart,
                    ItemSlotBoundService.MainQuickSlotEnd);
                GetOpenSlotRange(
                    itemId,
                    metadata,
                    mainExpandStageKey,
                    out var overflowStart,
                    out var overflowEnd);
                AppendRange(
                    result,
                    InventoryListType.Main,
                    overflowStart,
                    overflowEnd);
                return result;
            }

            GetOpenSlotRange(
                itemId,
                metadata,
                mainExpandStageKey,
                out var start,
                out var end);
            AppendRange(result, InventoryListType.Main, start, end);
            return result;
        }

        internal static bool IsSlotAllowed(
            ItemMetadata metadata,
            DeathTowerInventoryEndpoint endpoint)
        {
            if (CanMoveToQuickSlot(metadata))
            {
                GetSlotRange(metadata, out var overflowStart, out var overflowEnd);
                if (endpoint.ListType == InventoryListType.QuickSlot)
                {
                    return endpoint.SlotIndex >= ItemSlotBoundService.MainQuickSlotStart
                        && endpoint.SlotIndex <= ItemSlotBoundService.MainQuickSlotEnd;
                }

                return endpoint.ListType == InventoryListType.Main
                    && endpoint.SlotIndex >= overflowStart
                    && endpoint.SlotIndex <= overflowEnd;
            }

            GetSlotRange(metadata, out var start, out var end);
            return endpoint.ListType == InventoryListType.Main
                && endpoint.SlotIndex >= start
                && endpoint.SlotIndex <= end;
        }

        internal static ItemCore CreateCore(TowerInventoryItem item)
        {
            var itemKind = ItemCore.KindConsumable;
            if (item != null
                && ItemMetadataResolver.TryResolveItemKind(
                    item.ItemId,
                    out var resolvedKind))
            {
                itemKind = resolvedKind;
            }

            var core = ItemCore.Create(itemKind, item?.ItemId ?? 0);
            core.Count = item?.Count ?? 0;
            return core;
        }

        private static void GetSlotRange(ItemMetadata metadata, out short start, out short end)
        {
            (metadata ?? ItemMetadata.CreateDefaultStackable()).GetSlotRange(
                out var resolvedStart,
                out var resolvedEnd);
            start = (short)resolvedStart;
            end = (short)resolvedEnd;
        }

        private static bool HasPrimaryTag(ItemMetadata metadata, string expectedTag)
        {
            if (metadata == null
                || !metadata.IsStackable
                || string.IsNullOrWhiteSpace(expectedTag))
            {
                return false;
            }

            var normalized = (metadata.StackableType ?? string.Empty)
                .Replace("`", string.Empty)
                .Trim();
            if (normalized.Length < 3 || normalized[0] != '[')
                return false;

            var end = normalized.IndexOf(']', 1);
            return end > 1
                && string.Equals(
                    normalized.Substring(1, end - 1).Trim(),
                    expectedTag.Trim(),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void GetOpenSlotRange(
            int itemId,
            ItemMetadata metadata,
            int mainExpandStageKey,
            out short start,
            out short end)
        {
            if (metadata != null
                && ItemMetadataResolver.TryResolveItemKind(
                    itemId,
                    metadata,
                    out var itemKind)
                && ItemSlotBoundService.TryGetSlotRange(
                    itemKind,
                    mainExpandStageKey,
                    out var listType,
                    out var range)
                && listType == InventoryListType.Main)
            {
                start = range.Start;
                end = range.End;
                return;
            }

            GetSlotRange(metadata, out start, out end);
        }

        private static void AppendRange(
            ICollection<DeathTowerInventoryEndpoint> result,
            InventoryListType listType,
            int start,
            int end)
        {
            for (var slot = start; slot <= end; slot++)
            {
                result.Add(new DeathTowerInventoryEndpoint(listType, (short)slot));
            }
        }
    }
}
