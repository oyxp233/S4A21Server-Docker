using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Network;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonItemAcquisitionSource
    {
        QuestAutomaticDrop = 0,
        TutorialReward = 1,
        SpecialMechanismReward = 2,
    }

    internal sealed class DungeonItemGrantRequest
    {
        internal int QuestId { get; set; }
        internal QuestActivationId QuestActivationId { get; set; }
        internal int ItemTemplateId { get; set; }
        internal int Count { get; set; }
        internal DungeonItemAcquisitionSource Source { get; set; }
    }

    internal sealed class DungeonItemGrantBatchPlan
    {
        internal IReadOnlyList<DungeonItemGrantRequest> Requests { get; set; }
            = Array.Empty<DungeonItemGrantRequest>();
        internal InventoryRewardGrantBatchPlan InventoryPlan { get; set; }
        internal InventoryRewardGrantError Error { get; set; }
        internal bool Success { get; set; }
    }

    internal sealed class DungeonItemGrantMutationSnapshot
    {
        private readonly Dictionary<(InventoryListType, short), ItemCore> _items =
            new Dictionary<(InventoryListType, short), ItemCore>();
        private readonly Dictionary<short, int> _virtualCounts =
            new Dictionary<short, int>();

        internal static bool TryCapture(
            InventoryService inventory,
            DungeonItemGrantBatchPlan plan,
            out DungeonItemGrantMutationSnapshot snapshot)
        {
            snapshot = null;
            if (inventory == null
                || plan == null
                || !plan.Success
                || plan.InventoryPlan == null)
            {
                return false;
            }

            var captured = new DungeonItemGrantMutationSnapshot();
            foreach (var entry in plan.InventoryPlan.Entries)
            {
                if (entry.Kind == InventoryRewardGrantKind.InventoryItem)
                {
                    var key = (entry.ListType, entry.SlotIndex);
                    if (!captured._items.ContainsKey(key))
                    {
                        captured._items[key] = inventory.TryGetItem(
                            entry.ListType,
                            entry.SlotIndex,
                            out var item)
                            ? item.Copy()
                            : null;
                    }
                    continue;
                }
                if (entry.Kind == InventoryRewardGrantKind.MainVirtualCount)
                {
                    if (!captured._virtualCounts.ContainsKey(entry.SlotIndex))
                    {
                        captured._virtualCounts[entry.SlotIndex] =
                            inventory.GetMainVirtualCount(entry.SlotIndex)?.Count ?? 0;
                    }
                    continue;
                }

                return false;
            }

            snapshot = captured;
            return true;
        }

        internal void Restore(
            InventoryService inventory,
            DungeonItemGrantBatchPlan plan)
        {
            if (inventory == null)
                return;

            if (plan?.InventoryPlan != null)
            {
                foreach (var entry in plan.InventoryPlan.Entries)
                {
                    if (entry.Kind == InventoryRewardGrantKind.InventoryItem
                        && entry.CreateResult != null)
                    {
                        InventoryCreateService.DetachCreatedDetails(
                            inventory,
                            entry.CreateResult);
                    }
                }
            }

            foreach (var pair in _items)
            {
                if (pair.Value == null)
                    inventory.RemoveItem(pair.Key.Item1, pair.Key.Item2);
                else
                {
                    inventory.SetItem(
                        pair.Key.Item1,
                        pair.Key.Item2,
                        pair.Value.Copy());
                }
            }
            foreach (var pair in _virtualCounts)
                inventory.SetMainVirtualCount(pair.Key, pair.Value);
        }
    }

    internal sealed class DungeonItemGrantEntry
    {
        internal DungeonItemGrantRequest Request { get; set; }
        internal InventoryRewardGrantResult Grant { get; set; }
    }

    internal sealed class DungeonItemGrantBatchResult
    {
        private readonly List<DungeonItemGrantEntry> _entries =
            new List<DungeonItemGrantEntry>();

        internal bool Success { get; set; }
        internal InventoryRewardGrantError Error { get; set; }
        internal IReadOnlyList<DungeonItemGrantEntry> Entries => _entries;
        internal InventoryMutationSet Changes { get; } = new InventoryMutationSet();

        internal void Add(
            DungeonItemGrantRequest request,
            InventoryRewardGrantResult grant)
        {
            _entries.Add(new DungeonItemGrantEntry
            {
                Request = request,
                Grant = grant,
            });
            if (grant != null)
                Changes.AddRange(grant.Changes);
        }
    }

    internal sealed class DungeonItemAcquisitionService
    {
        private readonly DropService _drops;

        internal DungeonItemAcquisitionService(DropService drops)
        {
            _drops = drops ?? throw new ArgumentNullException(nameof(drops));
        }

        internal PickupResult AcquireGroundDrop(
            DungeonRun run,
            ushort sceneSlot,
            EnhancedClientSession session)
        {
            return _drops.TryPickup(run, sceneSlot, session);
        }

        internal bool TryRegisterGroundDrop(
            DungeonRun run,
            int itemTemplateId,
            int count,
            out DropInfo drop)
        {
            return _drops.TryRegisterTemplateDrop(
                run,
                itemTemplateId,
                count,
                out drop);
        }

        internal bool TryGrantItems(
            InventoryLease lease,
            IReadOnlyList<DungeonItemGrantRequest> requests,
            out DungeonItemGrantBatchResult result)
        {
            if (lease == null)
            {
                result = Failed(InventoryRewardGrantError.InvalidInventory);
                return false;
            }

            lock (lease.SyncRoot)
                return TryGrantItems(lease.Inventory, requests, out result);
        }

        internal bool TryGrantItems(
            InventoryService inventory,
            IReadOnlyList<DungeonItemGrantRequest> requests,
            out DungeonItemGrantBatchResult result)
        {
            if (!TryPlanItems(inventory, requests, out var plan))
            {
                result = Failed(plan?.Error
                    ?? InventoryRewardGrantError.InvalidRequest);
                return false;
            }

            return TryApplyPlannedItems(inventory, plan, out result);
        }

        internal bool TryPlanItems(
            InventoryService inventory,
            IReadOnlyList<DungeonItemGrantRequest> requests,
            out DungeonItemGrantBatchPlan plan)
        {
            plan = new DungeonItemGrantBatchPlan
            {
                Requests = requests ?? Array.Empty<DungeonItemGrantRequest>(),
            };
            if (inventory == null)
            {
                plan.Error = InventoryRewardGrantError.InvalidInventory;
                return false;
            }
            if (requests == null)
            {
                plan.Error = InventoryRewardGrantError.InvalidRequest;
                return false;
            }

            var grants = new List<InventoryRewardGrantRequest>(requests.Count);
            foreach (var request in requests)
            {
                if (request == null
                    || request.ItemTemplateId <= 0
                    || request.Count <= 0
                    || (request.QuestId > 0
                        && !request.QuestActivationId.IsValid))
                {
                    plan.Error = InventoryRewardGrantError.InvalidRequest;
                    return false;
                }

                grants.Add(InventoryRewardGrantRequest.Create(
                    request.ItemTemplateId,
                    request.Count,
                    ItemCreateReason.QuestReward));
            }

            if (!InventoryRewardGrantService.TryPlanBatch(
                    inventory,
                    grants,
                    out var inventoryPlan)
                || inventoryPlan == null
                || !inventoryPlan.Success)
            {
                plan.Error = inventoryPlan?.Error
                    ?? InventoryRewardGrantError.InsertPlanFailed;
                return false;
            }

            plan.InventoryPlan = inventoryPlan;
            plan.Success = true;
            plan.Error = InventoryRewardGrantError.None;
            return true;
        }

        internal bool TryApplyPlannedItems(
            InventoryService inventory,
            DungeonItemGrantBatchPlan plan,
            out DungeonItemGrantBatchResult result)
        {
            result = new DungeonItemGrantBatchResult();
            if (inventory == null
                || plan == null
                || !plan.Success
                || plan.InventoryPlan == null
                || plan.Requests == null)
            {
                result.Error = plan?.Error
                    ?? InventoryRewardGrantError.InvalidRequest;
                return false;
            }

            if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                    inventory,
                    plan.InventoryPlan,
                    out var granted))
            {
                result.Error = granted?.Error
                    ?? InventoryRewardGrantError.InsertApplyFailed;
                return false;
            }

            if (granted.Results.Count != plan.Requests.Count)
            {
                result.Error = InventoryRewardGrantError.InsertApplyFailed;
                return false;
            }

            for (var index = 0; index < plan.Requests.Count; index++)
            {
                var grant = granted.Results[index];
                if (grant == null || !grant.Success)
                {
                    result.Error = grant?.Error
                        ?? InventoryRewardGrantError.InsertApplyFailed;
                    return false;
                }
                result.Add(plan.Requests[index], grant);
            }

            result.Success = true;
            result.Error = InventoryRewardGrantError.None;
            return true;
        }

        private static DungeonItemGrantBatchResult Failed(
            InventoryRewardGrantError error)
            => new DungeonItemGrantBatchResult
            {
                Success = false,
                Error = error,
            };
    }
}
