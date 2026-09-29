using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using PvfLib;
using System;

namespace DfoServer.Game.NpcFavor
{
    internal sealed class NpcFavorGiftCommandResult
    {
        internal NpcFavorGiftPlan Plan { get; set; }

        internal NpcFavorGiftResult Gift { get; set; }

        internal bool Success => Plan?.Success == true && Gift?.Success == true;
    }

    /// <summary>
    /// Bridges the captured A21 slot-only gift request to the PVF planner and
    /// the transactional inventory/favor mutation. The client does not send a
    /// quantity; it is derived from the active PVF rule.
    /// </summary>
    internal sealed class NpcFavorGiftCommandService
    {
        private readonly NpcFavorDefinitionCatalog _catalog;
        private readonly NpcFavorRepository _repository;
        private readonly NpcFavorGiftService _giftService;
        private readonly Func<int, int, int> _randomInclusive;

        internal NpcFavorGiftCommandService(
            NpcFavorDefinitionCatalog catalog,
            NpcFavorRepository repository,
            Func<int, int, int> randomInclusive = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _giftService = new NpcFavorGiftService(repository);
            _randomInclusive = randomInclusive ?? NextInclusive;
        }

        internal NpcFavorGiftCommandResult TryGift(
            InventoryLease lease,
            int characterLevel,
            int npcId,
            short slotIndex,
            int gameDayId)
        {
            if (lease?.Inventory == null || slotIndex < 0)
                return Failure(NpcFavorGiftPlanError.InvalidRequest);

            int itemId;
            int availableCount;
            lock (lease.SyncRoot)
            {
                if (InventoryService.IsVirtualMainSlot(slotIndex))
                {
                    var item = lease.Inventory.GetMainVirtualCount(slotIndex);
                    if (item == null
                        || !InventoryService.TryResolveMainVirtualItemId(
                            slotIndex,
                            out itemId))
                    {
                        return Failure(NpcFavorGiftPlanError.ItemNotSupported);
                    }
                    availableCount = item.Count;
                }
                else
                {
                    var item = lease.Inventory.GetItem(
                        InventoryListType.Main,
                        slotIndex);
                    if (item == null || !InventoryStackRuleService.IsStackable(item))
                        return Failure(NpcFavorGiftPlanError.ItemNotSupported);
                    itemId = item.ItemId;
                    availableCount = item.Count;
                }
            }

            var state = _repository.GetState(
                lease.CharacterId,
                npcId,
                defaultFavorPoint: ResolveDefaultFavor(npcId),
                gameDayId);
            var requiredCount = ResolveRequiredCount(
                npcId,
                state.FavorPoint,
                itemId);
            if (requiredCount <= 0 || availableCount < requiredCount)
                return Failure(NpcFavorGiftPlanError.InvalidItemCount);

            var plan = NpcFavorGiftPlanner.Plan(
                _catalog,
                characterLevel,
                npcId,
                state.FavorPoint,
                slotIndex,
                itemId,
                requiredCount,
                moodIndex: 1,
                gameDayId,
                _randomInclusive);
            if (!plan.Success)
                return new NpcFavorGiftCommandResult { Plan = plan };

            return new NpcFavorGiftCommandResult
            {
                Plan = plan,
                Gift = _giftService.TryGift(lease, plan.Request),
            };
        }

        private int ResolveRequiredCount(
            int npcId,
            int currentFavorPoint,
            int itemId)
        {
            if (_catalog.TryGetSpecialGiftRule(itemId, out _))
                return _catalog.System.SpecialGiftItemCount;
            if (!_catalog.TryGetNpc(npcId, out var npc))
                return 0;

            _catalog.TryResolveItemGroup(itemId, out var itemGroupName);
            var match = NpcFavorGiftRuleMatcher.Match(
                npc,
                NpcFavorProgressionPolicy.ResolveFavorLevel(currentFavorPoint),
                itemId,
                itemGroupName,
                int.MaxValue,
                moodIndex: 1);
            return match.Rule?.MinimumCount > 0
                ? match.Rule.MinimumCount
                : 1;
        }

        private int ResolveDefaultFavor(int npcId)
            => _catalog.TryGetNpc(npcId, out NpcFile npc)
                ? Math.Max(0, npc.DefaultFavor)
                : 0;

        private static int NextInclusive(int minimum, int maximum)
            => maximum <= minimum
                ? minimum
                : minimum + ServerRandom.Next(maximum - minimum + 1);

        private static NpcFavorGiftCommandResult Failure(
            NpcFavorGiftPlanError error)
            => new NpcFavorGiftCommandResult
            {
                Plan = new NpcFavorGiftPlan
                {
                    Success = false,
                    Error = error,
                },
            };
    }
}
