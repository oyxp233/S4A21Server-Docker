using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Currency;
using DfoServer.Game.Mailbox;

namespace DfoServer.Game.Inventory
{
    internal sealed record InventoryExchangeOffer(short SourceSlot, int Count, ItemCore Snapshot);

    internal static class InventoryExchangeCommitService
    {
        // Main instances and crystal wallet projections only. Detail-bearing,
        // account-bound items must not pass through the ordinary-item path.
        internal static bool CanOffer(ItemCore core, int count, int senderAccountId, int receiverAccountId)
        {
            if (core == null || core.ItemId <= 0 || count <= 0
                || core.EquipmentLockId != 0 || core.TradeRestriction != 0
                || core.ItemKind == ItemCore.KindAvatar || core.ItemKind == ItemCore.KindCreature
                || core.ItemKind == ItemCore.KindEpicPiece)
                return false;
            if (InventoryStackRuleService.IsStackable(core) ? count > core.Count : count != 1)
                return false;
            // Direct trade preserves an active absolute expiry; unlike mail it
            // does not reject an item merely for having a deadline. This check
            // runs again at final settlement against the current instance.
            if (InventoryItemExpirationService.IsExpired(core, null, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                return false;
            return MailboxSendPolicy.ValidateTransferability(new MailboxSendRequest
            {
                SenderAccountId = senderAccountId, ReceiverAccountId = receiverAccountId
            }, core) == MailboxSendError.None;
        }

        internal static bool TryCommit(
            InventoryLease left, IReadOnlyList<InventoryExchangeOffer> leftItems, int leftGold,
            InventoryLease right, IReadOnlyList<InventoryExchangeOffer> rightItems, int rightGold)
        {
            if (left == null || right == null || left.CharacterId == right.CharacterId
                || left.AccountId == right.AccountId || leftItems == null || rightItems == null
                || leftGold < 0 || rightGold < 0 || leftItems.Count > 9 || rightItems.Count > 9
                || left.Inventory.Database == null || right.Inventory.Database == null
                || left.Inventory.Database.ConnectionString != right.Inventory.Database.ConnectionString)
                return false;
            var first = left.CharacterId < right.CharacterId ? left : right;
            var second = ReferenceEquals(first, left) ? right : left;
            lock (first.SyncRoot)
            lock (second.SyncRoot)
            {
                if (!Current(left) || !Current(right)
                    || !Validate(left, right.AccountId, leftItems, leftGold) || !Validate(right, left.AccountId, rightItems, rightGold))
                    return false;

                // Preserve earlier dirty gameplay changes before establishing the
                // exchange rollback baseline. Neither offer has been debited yet.
                if (!InventoryPersistenceService.SaveDirty(left)
                    || !InventoryPersistenceService.SaveDirty(right))
                    return false;
                InventoryService leftBaseline = null, rightBaseline = null;
                bool mutated = false;
                try
                {
                    using var connection = left.Inventory.Database.OpenConnection();
                    leftBaseline = InventoryService.LoadFromDb(connection, left.CharacterId, left.AccountId, left.Inventory.Database);
                    rightBaseline = InventoryService.LoadFromDb(connection, right.CharacterId, right.AccountId, right.Inventory.Database);
                    using var transaction = connection.BeginTransaction();
                    using var allocation = InventoryUidAllocationContext.Enter(connection, transaction);
                    long leftBalance = (long)(left.Inventory.GetMainVirtualCount(0)?.Count ?? 0) - leftGold + rightGold;
                    long rightBalance = (long)(right.Inventory.GetMainVirtualCount(0)?.Count ?? 0) - rightGold + leftGold;
                    if (leftBalance > CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(connection, transaction, left.CharacterId)
                        || rightBalance > CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(connection, transaction, right.CharacterId))
                        return false;
                    mutated = true;
                    if (!Remove(left.Inventory, leftItems) || !Remove(right.Inventory, rightItems)
                        || !Insert(left.Inventory, rightItems) || !Insert(right.Inventory, leftItems)
                        || !left.Inventory.SetMainVirtualCount(0, checked((int)leftBalance))
                        || !right.Inventory.SetMainVirtualCount(0, checked((int)rightBalance))
                        || !InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, left)
                        || !InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, right))
                        throw new InvalidOperationException("exchange validation or persistence failed");
                    transaction.Commit();
                    left.Inventory.ClearDirtyState();
                    right.Inventory.ClearDirtyState();
                    return true;
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[InventoryExchange] rollback cid={left.CharacterId}/{right.CharacterId}: {ex.Message}");
                    // Recovery cannot depend on opening the failed database again.
                    // Both pre-mutation snapshots were loaded before any debit.
                    if (mutated)
                    {
                        InventoryContext.TryReplaceCurrentLease(left, leftBaseline, out _);
                        InventoryContext.TryReplaceCurrentLease(right, rightBaseline, out _);
                    }
                    return false;
                }
            }
        }

        internal static bool Current(InventoryLease lease) => lease != null
            && InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId);

        internal static bool IsCrystalSlot(short slot) => slot >= InventoryService.MainVirtualCubeSlotStart
            && slot <= InventoryService.MainVirtualCubeSlotEnd;

        internal static ItemCore ReadSource(InventoryService inventory, short slot)
        {
            if (slot >= 3 && slot < 352) return inventory.GetItem(InventoryListType.Main, slot);
            if (!IsCrystalSlot(slot)) return null;
            var balance = inventory.GetMainVirtualCount(slot);
            if (balance == null || balance.Count <= 0) return null;
            // Quote-only snapshot. Never attach this core to an inventory container.
            return new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = balance.ItemId, Count = balance.Count };
        }

        private static bool Validate(InventoryLease lease, int receiverAccountId, IReadOnlyList<InventoryExchangeOffer> offers, int gold)
        {
            if ((lease.Inventory.GetMainVirtualCount(0)?.Count ?? 0) < gold
                || offers.Select(x => x.SourceSlot).Distinct().Count() != offers.Count)
                return false;
            foreach (var offer in offers)
            {
                var actual = ReadSource(lease.Inventory, offer.SourceSlot);
                if (!CanOffer(actual, offer.Count, lease.AccountId, receiverAccountId)
                    || offer.Snapshot == null || !actual.ToBytes().SequenceEqual(offer.Snapshot.ToBytes()))
                    return false;
            }
            return true;
        }

        private static bool Remove(InventoryService inventory, IReadOnlyList<InventoryExchangeOffer> offers)
        {
            foreach (var offer in offers)
            {
                if (IsCrystalSlot(offer.SourceSlot))
                {
                    if (!inventory.TryConsumeMainItem(offer.Snapshot.ItemId, offer.Count, out _)) return false;
                    continue;
                }
                if (!InventoryDeleteService.TryConsumeFromSlot(inventory, InventoryListType.Main,
                        offer.SourceSlot, offer.Snapshot.ItemId, offer.Count, out _))
                    return false;
            }
            return true;
        }

        private static bool Insert(InventoryService inventory, IReadOnlyList<InventoryExchangeOffer> offers)
        {
            foreach (var offer in offers)
            {
                var core = offer.Snapshot.Copy();
                if (IsCrystalSlot(offer.SourceSlot))
                {
                    // The grant service saturates virtual rewards; exchange must conserve every unit.
                    if ((long)inventory.CountMainItem(core.ItemId) + offer.Count > int.MaxValue
                        || !InventoryRewardGrantService.TryInsertExisting(inventory, core, offer.Count, out var crystal)
                        || crystal.Kind != InventoryRewardGrantKind.MainVirtualCount
                        || crystal.SlotIndex != offer.SourceSlot || crystal.GrantedCount != offer.Count)
                        return false;
                    continue;
                }
                if (MailboxSendPolicy.IsTradeLimitItem(ItemMetadataResolver.Resolve(core.ItemId)))
                    core.StackTradeCount--;
                if (InventoryStackRuleService.IsStackable(core))
                {
                    // The general stack policy compares only part of the instance.
                    // A transfer must not merge away binding/count/expiration state.
                    var comparable = core.Copy(); comparable.Count = 1;
                    foreach (var existing in inventory.GetItems(InventoryListType.Main))
                    {
                        if (!InventoryStackRuleService.CanShareStack(core, existing.Value)) continue;
                        var other = existing.Value.Copy(); other.Count = 1;
                        if (!comparable.ToBytes().SequenceEqual(other.ToBytes())) return false;
                    }
                }
                if (!InventoryRewardGrantService.TryInsertExisting(inventory, core, offer.Count, out var result)
                    || result.Kind != InventoryRewardGrantKind.InventoryItem || result.GrantedCount != offer.Count
                    || result.ListType != InventoryListType.Main)
                    return false;
            }
            return true;
        }
    }
}
