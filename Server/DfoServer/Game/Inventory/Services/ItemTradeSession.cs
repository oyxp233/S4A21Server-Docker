using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    internal enum ItemTradeAdvance { Rejected, Changed, RegistrationFinished, Committed, Canceled }

    // Owns only the negotiation. InventoryLease remains the asset owner.
    internal sealed class ItemTradeSession
    {
        internal ItemTradeSession(InventoryLease inviter, InventoryLease invitee, int peer)
        {
            Leases = new[] { inviter, invitee };
            Peer = peer;
        }
        internal object SyncRoot { get; } = new object();
        internal InventoryLease[] Leases { get; }
        internal int Peer { get; }
        internal DateTimeOffset InvitationDeadline { get; } = DateTimeOffset.UtcNow.AddMinutes(1);
        internal bool Active { get; private set; }
        internal bool Closed { get; private set; }
        internal bool Committed { get; private set; }
        private readonly byte[] _states = new byte[2];
        private readonly int[] _gold = new int[2];
        private readonly SortedDictionary<short, InventoryExchangeOffer>[] _offers =
            { new(), new() };

        internal bool IsCurrent => !Closed && Leases.All(InventoryExchangeCommitService.Current);
        internal bool Accept(InventoryLease invitee)
        {
            lock (SyncRoot)
            {
                if (!IsCurrent || Active || !ReferenceEquals(invitee, Leases[1])
                    || DateTimeOffset.UtcNow > InvitationDeadline)
                    return false;
                Active = true;
                return true;
            }
        }

        internal bool Offer(int side, short sourceSlot, int itemId, int count, out short tradeSlot, out ItemCore projection)
        {
            tradeSlot = -1;
            projection = null;
            lock (SyncRoot)
            {
                if (side < 0 || side > 1 || !IsCurrent || !Active || _states[side] != 0 || _states[1 - side] == 1)
                    return false;
                var lease = Leases[side];
                lock (lease.SyncRoot)
                {
                    if (!IsCurrent || count <= 0) return false;
                    if (sourceSlot == 0 && itemId == 0)
                    {
                        if (count > (lease.Inventory.GetMainVirtualCount(0)?.Count ?? 0) - _gold[side]) return false;
                        _gold[side] += count;
                        tradeSlot = 0;
                        return true;
                    }
                    var core = InventoryExchangeCommitService.ReadSource(lease.Inventory, sourceSlot);
                    if (core?.ItemId != itemId
                        || !InventoryExchangeCommitService.CanOffer(core, count, lease.AccountId, Leases[1 - side].AccountId)
                        || _offers[side].Values.Any(x => x.SourceSlot == sourceSlot))
                        return false;
                    for (short slot = 3; slot < 12; slot++)
                    {
                        if (_offers[side].ContainsKey(slot)) continue;
                        _offers[side].Add(slot, new(sourceSlot, count, core.Copy()));
                        tradeSlot = slot;
                        projection = core.Copy();
                        if (InventoryStackRuleService.IsStackable(projection)) projection.Count = count;
                        return true;
                    }
                    return false;
                }
            }
        }

        internal int Gold(int side) { lock (SyncRoot) return _gold[side]; }

        internal bool Withdraw(int side, short tradeSlot, int itemId, int count, out short sourceSlot, out ItemCore projection)
        {
            sourceSlot = -1; projection = null;
            lock (SyncRoot)
            {
                if (side < 0 || side > 1 || !IsCurrent || !Active || _states[side] != 0
                    || _states[1 - side] == 1 || count <= 0) return false;
                if (tradeSlot == 0 && itemId == 0 && count <= _gold[side])
                { _gold[side] -= count; sourceSlot = 0; return true; }
                if (!_offers[side].TryGetValue(tradeSlot, out var offer) || offer.Snapshot.ItemId != itemId || count > offer.Count)
                    return false;
                var lease = Leases[side];
                lock (lease.SyncRoot)
                {
                    var current = InventoryExchangeCommitService.ReadSource(lease.Inventory, offer.SourceSlot);
                    if (current == null || !current.ToBytes().SequenceEqual(offer.Snapshot.ToBytes())) return false;
                    sourceSlot = offer.SourceSlot;
                    if (count == offer.Count) _offers[side].Remove(tradeSlot);
                    else
                    {
                        _offers[side][tradeSlot] = offer with { Count = offer.Count - count };
                        projection = offer.Snapshot.Copy(); projection.Count = offer.Count - count;
                    }
                    return true;
                }
            }
        }

        internal InventoryMutationResult[] MutationHints()
        {
            lock (SyncRoot)
                return _offers.SelectMany(x => x.Values).Select(x => x.Snapshot.ItemId).Distinct()
                    .Select(id => new InventoryMutationResult { ItemTemplateId = id })
                    .Concat(_gold.Any(x => x != 0) ? new[] { new InventoryMutationResult { GoldSpent = true } } : Array.Empty<InventoryMutationResult>())
                    .ToArray();
        }

        internal ItemTradeAdvance Advance(int side, byte state)
        {
            lock (SyncRoot)
            {
                if (side < 0 || side > 1 || !IsCurrent || !Active) return ItemTradeAdvance.Rejected;
                if (state == 5)
                {
                    if (_states[side] != 0) return ItemTradeAdvance.Rejected;
                    _states[side] = 5;
                    return _states[1 - side] == 5 ? ItemTradeAdvance.RegistrationFinished : ItemTradeAdvance.Changed;
                }
                if (state == 1)
                {
                    if (_states[side] != 5 || (_states[1 - side] != 5 && _states[1 - side] != 1))
                        return ItemTradeAdvance.Rejected;
                    _states[side] = 1;
                    return ItemTradeAdvance.Changed;
                }
                if (state == 3)
                {
                    if (_states[side] != 1 || (_states[1 - side] != 1 && _states[1 - side] != 3))
                        return ItemTradeAdvance.Rejected;
                    _states[side] = 3;
                    if (_states[1 - side] != 3) return ItemTradeAdvance.Changed;
                    Closed = true; // Consume the negotiation before attempting settlement.
                    Committed = InventoryExchangeCommitService.TryCommit(
                        Leases[0], _offers[0].Values.ToArray(), _gold[0],
                        Leases[1], _offers[1].Values.ToArray(), _gold[1]);
                    return Committed ? ItemTradeAdvance.Committed : ItemTradeAdvance.Canceled;
                }
                return ItemTradeAdvance.Rejected;
            }
        }

        internal void Cancel() { lock (SyncRoot) Closed = true; }
    }
}
