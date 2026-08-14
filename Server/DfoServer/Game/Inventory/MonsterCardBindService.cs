using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class MonsterCardBindResult
    {
        public int ResultItemId { get; set; }
        public int FirstRarity { get; set; }
        public int SecondRarity { get; set; }
        public int ResultRarity { get; set; }
        public int BindType { get; set; }
        public int SuccessWeight { get; set; }
        public bool SuccessRoll { get; set; }
        public InventoryRewardGrantResult Grant { get; set; }
    }

    internal sealed class MonsterCardBindService
    {
        private readonly MonsterCardBindConfig _config;
        private readonly Func<int, int> _next;

        internal MonsterCardBindService()
            : this(MonsterCardBindConfigProvider.Current, ServerRandom.Next)
        {
        }

        internal MonsterCardBindService(MonsterCardBindConfig config, Func<int, int> next)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        internal bool TryBind(InventoryService inventory, short binderSlot, short firstSlot, short secondSlot,
            out MonsterCardBindResult result, out string rejection)
        {
            result = null;
            rejection = null;
            if (inventory == null || binderSlot == firstSlot || binderSlot == secondSlot)
                return Reject("invalid or duplicate slots", out rejection);
            if (!inventory.TryGetItem(InventoryListType.Main, binderSlot, out var binder)
                || !inventory.TryGetItem(InventoryListType.Main, firstSlot, out var first)
                || !inventory.TryGetItem(InventoryListType.Main, secondSlot, out var second))
                return Reject("requested slot is empty", out rejection);
            if (firstSlot == secondSlot && first.Count < 2)
                return Reject($"same card slot has count={first.Count}, need=2", out rejection);
            if (!ItemMetadataResolver.TryLoadStackableFile(binder.ItemId, out var binderFile)
                || binderFile.MonsterCardBind < 0)
                return Reject($"invalid binder item={binder.ItemId}", out rejection);
            if (!TryResolveCard(first.ItemId, out var firstRarity)
                || !TryResolveCard(second.ItemId, out var secondRarity))
                return Reject("input is not a supported monster card", out rejection);
            if (!_config.TryCalculateSuccessWeight(firstRarity, secondRarity, binderFile.MonsterCardBind, out var chance))
                return Reject("probability configuration unavailable", out rejection);

            var high = Math.Max(firstRarity, secondRarity);
            var success = chance > 0 && _next(MonsterCardBindConfig.ProbabilityDenominator) < chance;
            var resultRarity = firstRarity == secondRarity
                ? Math.Min(3, firstRarity + (success ? 1 : 0))
                : success ? high : Math.Max(0, high - 1);
            if (!_config.TrySelectResult(resultRarity, _next, out var selected))
                return Reject($"result pool unavailable rarity={resultRarity}", out rejection);

            var planning = InventoryCompoundPlanning.CloneInventory(inventory);
            if (!Consume(planning, binderSlot, binder.ItemId)
                || !Consume(planning, firstSlot, first.ItemId)
                || !Consume(planning, secondSlot, second.ItemId))
                return Reject("inventory consume planning failed", out rejection);
            var requests = new List<InventoryRewardGrantRequest>
            {
                InventoryRewardGrantRequest.Create(selected.ItemId, 1, ItemCreateReason.Unknown),
            };
            if (!InventoryRewardGrantService.TryPlanBatch(planning, requests, out var plan)
                || !plan.Success)
                return Reject("inventory full", out rejection);
            if (!Consume(inventory, binderSlot, binder.ItemId)
                || !Consume(inventory, firstSlot, first.ItemId)
                || !Consume(inventory, secondSlot, second.ItemId)
                || !InventoryRewardGrantService.TryApplyPreparedBatch(inventory, plan, out var batch)
                || !batch.Success || batch.Results.Count != 1)
                return Reject("inventory transaction failed", out rejection);

            result = new MonsterCardBindResult
            {
                ResultItemId = selected.ItemId,
                FirstRarity = firstRarity,
                SecondRarity = secondRarity,
                ResultRarity = resultRarity,
                BindType = binderFile.MonsterCardBind,
                SuccessWeight = chance,
                SuccessRoll = success,
                Grant = batch.Results[0],
            };
            return true;
        }

        private static bool Consume(InventoryService inventory, short slot, int itemId)
            => InventoryDeleteService.TryConsumeFromSlot(inventory, InventoryListType.Main, slot, itemId, 1, out var deleted)
                && deleted.Success && deleted.DeletedCount == 1;

        private static bool TryResolveCard(int itemId, out int rarity)
        {
            rarity = -1;
            if (!ItemMetadataResolver.TryLoadStackableFile(itemId, out var card)
                || !ItemMetadataResolver.IsMonsterCard(card)
                || card.Rarity < 0 || card.Rarity > 3)
                return false;
            rarity = card.Rarity;
            return true;
        }

        private static bool Reject(string reason, out string rejection)
        {
            rejection = reason;
            return false;
        }
    }
}
