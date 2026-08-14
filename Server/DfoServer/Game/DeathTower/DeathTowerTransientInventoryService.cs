using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.DeathTower
{
    // Owns the run-scoped tower overlay. The online inventory lease provides
    // ownership and serialization only; persistent items never enter the plan.
    internal sealed class DeathTowerTransientInventoryService
    {
        internal bool TryPickup(
            DeathTowerSession tower,
            InventoryLease lease,
            ushort sceneSlot,
            out TowerPickupResult result)
        {
            result = null;
            if (tower == null || lease == null)
                return false;

            lock (lease.SyncRoot)
            {
                return tower.TryPickupGroundItem(
                    sceneSlot,
                    lease.Inventory.GetListParam16(InventoryListType.Main),
                    endpoint => IsPersistentMainSlotOccupied(
                        lease.Inventory,
                        endpoint),
                    out result);
            }
        }

        internal bool TryUseStackable(
            DeathTowerSession tower,
            InventoryLease lease,
            InventoryListType listType,
            short slot,
            int expectedItemId,
            out bool handled,
            out TowerInventoryMutation result)
        {
            handled = false;
            result = null;
            if (tower == null
                || lease == null
                || !TryCreateEndpoint(listType, slot, false, out _))
            {
                return false;
            }

            handled = true;
            lock (lease.SyncRoot)
            {
                if (!TryCreateEndpoint(listType, slot, false, out var endpoint)
                    || !tower.TryGetInventoryItem(endpoint, out var item))
                    return false;

                var authoritativeItemId = expectedItemId > 0
                    ? expectedItemId
                    : item.ItemId;
                return tower.TryUseItem(endpoint, authoritativeItemId, out result);
            }
        }

        internal bool TryMove(
            DeathTowerSession tower,
            InventoryLease lease,
            InventoryMoveRequest request,
            byte characterJob,
            int characterGrowType,
            out bool handled,
            out TowerInventoryMoveResult result)
        {
            handled = false;
            result = new TowerInventoryMoveResult();
            if (tower == null
                || lease == null
                || request == null
                || !TryCreateEndpoint(
                    request.SourceListType,
                    request.SourceSlotIndex,
                    false,
                    out var sourceEndpoint)
                || !TryCreateEndpoint(
                    request.DestinationListType,
                    request.DestinationSlotIndex,
                    false,
                    out var destinationEndpoint))
            {
                return false;
            }

            lock (lease.SyncRoot)
            {
                var sourceItemId = request.SourceInstanceValue > 0
                    ? request.SourceInstanceValue
                    : 0;
                var destinationItemId = request.DestinationInstanceValue > 0
                    ? request.DestinationInstanceValue
                    : 0;
                var sourceExists = tower.TryGetInventoryItem(
                    sourceEndpoint,
                    out var sourceItem);
                var destinationExists = tower.TryGetInventoryItem(
                    destinationEndpoint,
                    out var destinationItem);
                var sourceIdentityEndpoints = tower.FindInventoryEndpointsByItemId(
                    sourceItemId);
                var destinationIdentityEndpoints = tower.FindInventoryEndpointsByItemId(
                    destinationItemId);

                var sourceIdentityResolved = TryResolveIdentityEndpoint(
                    sourceItemId,
                    sourceEndpoint,
                    sourceItem,
                    sourceIdentityEndpoints,
                    out var resolvedSourceEndpoint,
                    out var sourceIdentityAmbiguous);
                var destinationIdentityResolved = TryResolveIdentityEndpoint(
                    destinationItemId,
                    destinationEndpoint,
                    destinationItem,
                    destinationIdentityEndpoints,
                    out var resolvedDestinationEndpoint,
                    out var destinationIdentityAmbiguous);

                var hasTowerIdentity = sourceIdentityEndpoints.Count > 0
                    || destinationIdentityEndpoints.Count > 0;
                if (!sourceExists
                    && !destinationExists
                    && !hasTowerIdentity)
                    return false;

                handled = true;
                result.IdentityResolved = sourceIdentityResolved
                    || destinationIdentityResolved;

                // Two non-zero instance values describe an exchange. Resolve
                // both identities before mutating so a stale client endpoint
                // cannot turn an exchange into a duplicate stack.
                if (sourceItemId > 0 && destinationItemId > 0)
                {
                    if (sourceIdentityAmbiguous || destinationIdentityAmbiguous)
                    {
                        result.ErrorCode = 0x02;
                        return false;
                    }

                    if (sourceIdentityResolved
                        && destinationIdentityResolved
                        && !resolvedSourceEndpoint.Equals(resolvedDestinationEndpoint)
                        && sourceItemId != destinationItemId)
                    {
                        if (!TryApplyIdentitySwap(
                                tower,
                                lease,
                                sourceEndpoint,
                                destinationEndpoint,
                                resolvedSourceEndpoint,
                                resolvedDestinationEndpoint,
                                request.MoveCount,
                                out var swapResult))
                        {
                            result.ErrorCode = 0x02;
                            return false;
                        }

                        CopyMoveResult(result, swapResult);
                        result.ReversedRequest = false;
                        result.IdentityResolved = true;
                        return true;
                    }

                    // When both identities are present but already sit at the
                    // requested endpoints, use the normal typed move rules.
                    if (!sourceExists
                        || !destinationExists
                        || sourceItem.ItemId != sourceItemId
                        || destinationItem.ItemId != destinationItemId)
                    {
                        result.ErrorCode = 0x02;
                        return false;
                    }
                }

                var reversedRequest = false;
                DeathTowerInventoryEndpoint actualSource;
                DeathTowerInventoryEndpoint actualDestination;
                if (sourceItemId > 0 && sourceIdentityResolved)
                {
                    // A stale source endpoint is recoverable only when it is
                    // empty. If it contains another tower item, the packet is
                    // an ambiguous exchange and must fail closed.
                    if (sourceExists
                        && !resolvedSourceEndpoint.Equals(sourceEndpoint))
                    {
                        result.ErrorCode = 0x02;
                        return false;
                    }

                    actualSource = resolvedSourceEndpoint;
                    actualDestination = destinationEndpoint;
                }
                else if (destinationItemId > 0 && destinationIdentityResolved)
                {
                    // The A14 client uses the empty target as the packet source
                    // for reverse drags. The destination identity is the item
                    // that must move into the requested source endpoint.
                    if (sourceExists
                        && !sourceEndpoint.Equals(resolvedDestinationEndpoint))
                    {
                        if (sourceItem.ItemId == destinationItemId)
                        {
                            // A same-template stack at the drop endpoint is a
                            // merge, not an exchange. Keep the A14 reverse
                            // orientation and let the aggregate apply limits.
                            actualSource = resolvedDestinationEndpoint;
                            actualDestination = sourceEndpoint;
                            reversedRequest = true;
                        }
                        else
                        {
                            // When the drop endpoint already owns another tower
                            // item the client still sends only the dragged item's
                            // identity. Relocate both items in one planned swap.
                            if (!TryApplyIdentitySwap(
                                    tower,
                                    lease,
                                    sourceEndpoint,
                                    destinationEndpoint,
                                    sourceEndpoint,
                                    resolvedDestinationEndpoint,
                                    request.MoveCount,
                                    out var swapResult))
                            {
                                result.ErrorCode = 0x02;
                                return false;
                            }

                            CopyMoveResult(result, swapResult);
                            result.ReversedRequest = true;
                            result.IdentityResolved = true;
                            return true;
                        }
                    }
                    else if (sourceEndpoint.Equals(resolvedDestinationEndpoint))
                    {
                        actualSource = sourceEndpoint;
                        actualDestination = destinationEndpoint;
                    }
                    else
                    {
                        actualSource = resolvedDestinationEndpoint;
                        actualDestination = sourceEndpoint;
                        reversedRequest = true;
                    }
                }
                else
                {
                    // Preserve the ordinary endpoint-first behavior when the
                    // packet carries no usable tower identity.
                    actualSource = sourceExists
                        ? sourceEndpoint
                        : destinationEndpoint;
                    actualDestination = sourceExists
                        ? destinationEndpoint
                        : sourceEndpoint;
                    reversedRequest = !sourceExists;
                }

                if (!tower.TryMoveItem(
                        actualSource,
                        actualDestination,
                        request.MoveCount,
                        endpoint => IsPersistentMainSlotOccupied(
                            lease.Inventory,
                            endpoint),
                        out var transientMove))
                {
                    result.ErrorCode = 0x02;
                    return false;
                }

                result.MoveValue32 = transientMove.MoveValue32;
                result.ChangedSlots = transientMove.ChangedSlots;
                result.ChangedEndpoints = transientMove.ChangedEndpoints;
                result.ReversedRequest = reversedRequest;
                return true;
            }
        }

        private static bool TryApplyIdentitySwap(
            DeathTowerSession tower,
            InventoryLease lease,
            DeathTowerInventoryEndpoint requestedSource,
            DeathTowerInventoryEndpoint requestedDestination,
            DeathTowerInventoryEndpoint actualSource,
            DeathTowerInventoryEndpoint actualDestination,
            int requestedCount,
            out TowerInventoryMoveResult result)
        {
            result = new TowerInventoryMoveResult();
            if (requestedSource.Equals(requestedDestination)
                || actualSource.Equals(actualDestination))
            {
                return false;
            }

            var planned = tower.CopyInventoryItems();
            if (!planned.TryGetValue(actualSource, out var sourceItem)
                || !planned.TryGetValue(actualDestination, out var destinationItem)
                || sourceItem == null
                || destinationItem == null
                || sourceItem.ItemId == destinationItem.ItemId)
            {
                return false;
            }

            var sourceMoveCount = requestedCount <= 0
                ? sourceItem.Count
                : requestedCount;
            if (sourceMoveCount != sourceItem.Count
                || requestedCount > 0 && requestedCount != destinationItem.Count)
            {
                return false;
            }

            if (!DeathTowerItemSlotPolicy.IsSlotAllowed(
                    Inventory.ItemMetadataResolver.Resolve(sourceItem.ItemId),
                    requestedDestination)
                || !DeathTowerItemSlotPolicy.IsSlotAllowed(
                    Inventory.ItemMetadataResolver.Resolve(destinationItem.ItemId),
                    requestedSource)
                || IsPersistentMainSlotOccupied(
                    lease.Inventory,
                    requestedSource)
                || IsPersistentMainSlotOccupied(
                    lease.Inventory,
                    requestedDestination))
            {
                return false;
            }

            if (planned.TryGetValue(requestedSource, out var sourceTarget)
                && !requestedSource.Equals(actualSource)
                && !requestedSource.Equals(actualDestination)
                && sourceTarget != null)
            {
                return false;
            }
            if (planned.TryGetValue(requestedDestination, out var destinationTarget)
                && !requestedDestination.Equals(actualSource)
                && !requestedDestination.Equals(actualDestination)
                && destinationTarget != null)
            {
                return false;
            }

            planned.Remove(actualSource);
            planned.Remove(actualDestination);
            planned[requestedSource] = destinationItem.Copy();
            planned[requestedDestination] = sourceItem.Copy();
            tower.ReplaceInventoryItems(planned);

            result.MoveValue32 = requestedCount;
            result.ChangedSlots = new[]
            {
                actualSource.SlotIndex,
                actualDestination.SlotIndex,
                requestedSource.SlotIndex,
                requestedDestination.SlotIndex,
            }.Distinct().ToArray();
            result.ChangedEndpoints = new[]
            {
                actualSource,
                actualDestination,
                requestedSource,
                requestedDestination,
            }.Distinct().ToArray();
            return true;
        }

        private static void CopyMoveResult(
            TowerInventoryMoveResult target,
            TowerInventoryMoveResult source)
        {
            target.MoveValue32 = source.MoveValue32;
            target.ChangedSlots = source.ChangedSlots;
            target.ChangedEndpoints = source.ChangedEndpoints;
        }

        private static bool TryResolveIdentityEndpoint(
            int itemId,
            DeathTowerInventoryEndpoint requestedEndpoint,
            TowerInventoryItem requestedItem,
            IReadOnlyList<DeathTowerInventoryEndpoint> candidates,
            out DeathTowerInventoryEndpoint endpoint,
            out bool ambiguous)
        {
            endpoint = default;
            ambiguous = false;
            if (itemId <= 0)
                return false;

            if (requestedItem != null && requestedItem.ItemId == itemId)
            {
                endpoint = requestedEndpoint;
                return true;
            }

            if (candidates == null || candidates.Count == 0)
                return false;
            if (candidates.Count != 1)
            {
                ambiguous = true;
                return false;
            }

            endpoint = candidates[0];
            return true;
        }

        internal bool TrySort(
            DeathTowerSession tower,
            InventoryLease lease,
            InventoryListType listType,
            byte category,
            out bool handled,
            out DeathTowerInventorySortResult result)
            => TrySort(
                tower,
                lease,
                listType,
                category,
                true,
                out handled,
                out result);

        internal bool TrySort(
            DeathTowerSession tower,
            InventoryLease lease,
            InventoryListType listType,
            byte category,
            bool hasCategory,
            out bool handled,
            out DeathTowerInventorySortResult result)
        {
            handled = false;
            result = new DeathTowerInventorySortResult();
            if (tower == null
                || lease == null
                || (listType != InventoryListType.Main
                    && listType != InventoryListType.QuickSlot))
            {
                return false;
            }

            handled = true;
            lock (lease.SyncRoot)
            {
                var groups = BuildSortGroups(
                    lease.Inventory,
                    listType,
                    category,
                    hasCategory);
                if (groups.Count == 0)
                {
                    result.Success = true;
                    return true;
                }

                var original = tower.CopyInventoryItems();
                var planned = tower.CopyInventoryItems();
                foreach (var group in groups)
                {
                    if (!TryPlanSortGroup(
                            original,
                            planned,
                            lease.Inventory,
                            group))
                    {
                        return false;
                    }
                }

                var changed = new List<DeathTowerInventoryEndpoint>();
                var allEndpoints = new HashSet<DeathTowerInventoryEndpoint>(
                    original.Keys);
                allEndpoints.UnionWith(planned.Keys);
                foreach (var endpoint in allEndpoints)
                {
                    original.TryGetValue(endpoint, out var before);
                    planned.TryGetValue(endpoint, out var after);
                    if (!TowerItemsEqual(before, after))
                        changed.Add(endpoint);
                }

                changed.Sort(CompareEndpoints);
                if (changed.Count > 0)
                    tower.ReplaceInventoryItems(planned);

                result.Success = true;
                result.Mutated = changed.Count > 0;
                result.ChangedSlots = changed
                    .Select(endpoint => endpoint.SlotIndex)
                    .ToArray();
                result.ChangedEndpoints = changed;
                return true;
            }
        }

        private static List<DeathTowerSortGroup> BuildSortGroups(
            InventoryService inventory,
            InventoryListType listType,
            byte category,
            bool hasCategory)
        {
            var result = new List<DeathTowerSortGroup>();
            if (listType == InventoryListType.QuickSlot)
            {
                if (!hasCategory || IsKnownItemKind(category))
                {
                    result.Add(new DeathTowerSortGroup(
                        InventoryListType.QuickSlot,
                        (short)ItemSlotBoundService.MainQuickSlotStart,
                        (short)ItemSlotBoundService.MainQuickSlotEnd,
                        hasCategory,
                        category));
                }
                return result;
            }

            if (hasCategory)
            {
                AddMainSortGroup(inventory, category, result);
                return result;
            }

            foreach (var itemKind in SortableMainItemKinds)
                AddMainSortGroup(inventory, itemKind, result);
            return result;
        }

        private static void AddMainSortGroup(
            InventoryService inventory,
            byte category,
            ICollection<DeathTowerSortGroup> target)
        {
            if (inventory == null
                || !InventorySortService.TryGetSortRange(
                    inventory,
                    InventoryListType.Main,
                    category,
                    out var range))
            {
                return;
            }

            target.Add(new DeathTowerSortGroup(
                InventoryListType.Main,
                range.Start,
                range.End,
                false,
                category));
        }

        private static bool TryPlanSortGroup(
            IReadOnlyDictionary<DeathTowerInventoryEndpoint, TowerInventoryItem> original,
            IDictionary<DeathTowerInventoryEndpoint, TowerInventoryItem> planned,
            InventoryService inventory,
            DeathTowerSortGroup group)
        {
            var groupEndpoints = Enumerable.Range(
                    group.Start,
                    group.End - group.Start + 1)
                .Select(slot => new DeathTowerInventoryEndpoint(
                    group.ListType,
                    (short)slot))
                .ToArray();
            var movable = original
                .Where(pair => group.Contains(pair.Key)
                    && group.Matches(pair.Value))
                .ToArray();
            var movableEndpoints = new HashSet<DeathTowerInventoryEndpoint>(
                movable.Select(pair => pair.Key));

            foreach (var pair in movable)
            {
                if (IsPersistentMainSlotOccupied(inventory, pair.Key))
                    return false;
            }

            var availableSlots = groupEndpoints
                .Where(endpoint => !IsPersistentMainSlotOccupied(
                        inventory,
                        endpoint)
                    && (!original.ContainsKey(endpoint)
                        || movableEndpoints.Contains(endpoint)))
                .ToArray();
            if (availableSlots.Length < movable.Length)
                return false;

            var sorted = movable
                .OrderBy(pair => DeathTowerItemSlotPolicy.CreateCore(pair.Value),
                    TowerItemCoreComparer.Instance)
                .ThenBy(pair => pair.Key.ListType)
                .ThenBy(pair => pair.Key.SlotIndex)
                .Select(pair => pair.Value.Copy())
                .ToArray();

            foreach (var endpoint in movableEndpoints)
                planned.Remove(endpoint);
            for (var index = 0; index < sorted.Length; index++)
                planned[availableSlots[index]] = sorted[index];
            return true;
        }

        private static bool IsKnownItemKind(byte itemKind)
        {
            return SortableMainItemKinds.Contains(itemKind);
        }

        private static int CompareEndpoints(
            DeathTowerInventoryEndpoint left,
            DeathTowerInventoryEndpoint right)
        {
            var listResult = ((byte)left.ListType).CompareTo((byte)right.ListType);
            return listResult != 0
                ? listResult
                : left.SlotIndex.CompareTo(right.SlotIndex);
        }

        private static readonly byte[] SortableMainItemKinds =
        {
            ItemCore.KindEquipment,
            ItemCore.KindConsumable,
            ItemCore.KindMaterial,
            ItemCore.KindQuest,
            ItemCore.KindExpertJobMaterial,
            ItemCore.KindAvatarEmblem,
        };

        private readonly struct DeathTowerSortGroup
        {
            internal DeathTowerSortGroup(
                InventoryListType listType,
                short start,
                short end,
                bool filterByItemKind,
                byte itemKind)
            {
                ListType = listType;
                Start = start;
                End = end;
                FilterByItemKind = filterByItemKind;
                ItemKind = itemKind;
            }

            internal InventoryListType ListType { get; }
            internal short Start { get; }
            internal short End { get; }
            internal bool FilterByItemKind { get; }
            internal byte ItemKind { get; }

            internal bool Contains(DeathTowerInventoryEndpoint endpoint)
            {
                return endpoint.ListType == ListType
                    && endpoint.SlotIndex >= Start
                    && endpoint.SlotIndex <= End;
            }

            internal bool Matches(TowerInventoryItem item)
            {
                return item != null
                    && (!FilterByItemKind
                        || ResolveItemKind(item) == ItemKind);
            }

            private static byte ResolveItemKind(TowerInventoryItem item)
            {
                if (ItemMetadataResolver.TryResolveItemKind(
                        item.ItemId,
                        out var itemKind))
                {
                    return itemKind;
                }

                return DeathTowerItemSlotPolicy.CreateCore(item).ItemKind;
            }
        }

        internal bool TryDeleteSkillMaterials(
            DeathTowerSession tower,
            InventoryLease lease,
            DeathTowerDeleteItemCommand command,
            out bool handled,
            out DeathTowerDeleteItemResult result)
        {
            handled = false;
            result = new DeathTowerDeleteItemResult();
            if (tower == null
                || lease == null
                || command == null
                || command.Entries.Count == 0
                || !tower.Config.UsesFpCubePiece
                || !tower.Config.LimitsStackableItems)
            {
                return false;
            }

            var endpoints = new List<DeathTowerInventoryEndpoint>(
                command.Entries.Count);
            foreach (var entry in command.Entries)
            {
                if (!entry.IsSkillMaterialOperation
                    || entry.ItemId <= 0
                    || entry.DeleteCount <= 0
                    || entry.DeleteCount > short.MaxValue
                    || !TryCreateEndpoint(
                        command.ListType,
                        entry.SlotIndex,
                        true,
                        out var endpoint))
                {
                    return false;
                }
                endpoints.Add(endpoint);
            }

            handled = true;
            lock (lease.SyncRoot)
            {
                var planned = tower.CopyInventoryItems();
                var mutations = new List<InventoryMutationResult>(
                    command.Entries.Count);
                var itemIds = new HashSet<int>();
                for (var index = 0; index < command.Entries.Count; index++)
                {
                    var entry = command.Entries[index];
                    var endpoint = endpoints[index];
                    if (!planned.TryGetValue(endpoint, out var item)
                        || item.ItemId != entry.ItemId
                        || item.Count < entry.DeleteCount)
                    {
                        return false;
                    }

                    item.Count -= entry.DeleteCount;
                    var remaining = item.Count;
                    if (remaining == 0)
                        planned.Remove(endpoint);
                    mutations.Add(CreateDeleteMutation(
                        command.ListType,
                        entry,
                        remaining));
                    itemIds.Add(entry.ItemId);
                }

                tower.ReplaceInventoryItems(planned);
                result.Success = true;
                result.Mutations = mutations;
                result.TransientItemIds = itemIds.ToArray();
                result.ChangedEndpoints = endpoints
                    .Distinct()
                    .ToArray();
                return true;
            }
        }

        private static bool TryCreateEndpoint(
            InventoryListType listType,
            short slot,
            bool allowVirtual,
            out DeathTowerInventoryEndpoint endpoint)
        {
            endpoint = default;
            var valid = listType == InventoryListType.Main
                ? (slot >= InventoryService.MainSlotStart
                    && slot <= InventoryService.MainSlotEnd)
                    || (allowVirtual && InventoryService.IsVirtualMainSlot(slot))
                : listType == InventoryListType.QuickSlot
                    && ItemSlotBoundService.IsMainQuickSlot(slot);
            if (!valid)
                return false;

            endpoint = new DeathTowerInventoryEndpoint(listType, slot);
            return true;
        }

        private static InventoryMutationResult CreateDeleteMutation(
            InventoryListType listType,
            DeathTowerDeleteItemEntry entry,
            int remaining)
        {
            return new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = entry.SlotIndex,
                ItemTemplateId = entry.ItemId,
                RemainingStackCount = remaining,
                InstanceValue = remaining,
                RequestedCount = (short)entry.DeleteCount,
                AppliedCount = (short)entry.DeleteCount,
            };
        }

        private static bool TowerItemsEqual(
            TowerInventoryItem left,
            TowerInventoryItem right)
        {
            if (left == null || right == null)
                return left == null && right == null;
            return left.ItemId == right.ItemId
                && left.Count == right.Count
                && left.StackLimit == right.StackLimit
                && left.IsQuickSlotConsumable == right.IsQuickSlotConsumable
                && left.IsWaste == right.IsWaste;
        }

        private static bool IsPersistentMainSlotOccupied(
            InventoryService inventory,
            DeathTowerInventoryEndpoint endpoint)
        {
            if (inventory == null
                || endpoint.ListType != InventoryListType.Main
                || endpoint.SlotIndex < InventoryService.MainSlotStart
                || endpoint.SlotIndex > InventoryService.MainSlotEnd)
            {
                return false;
            }

            return inventory.GetItem(
                InventoryListType.Main,
                endpoint.SlotIndex) != null;
        }

        private sealed class TowerItemCoreComparer : IComparer<ItemCore>
        {
            internal static readonly TowerItemCoreComparer Instance =
                new TowerItemCoreComparer();

            public int Compare(ItemCore left, ItemCore right)
            {
                if (ReferenceEquals(left, right))
                    return 0;
                if (left == null)
                    return -1;
                if (right == null)
                    return 1;

                var kind = left.ItemKind.CompareTo(right.ItemKind);
                return kind != 0 ? kind : left.ItemId.CompareTo(right.ItemId);
            }
        }
    }
}
