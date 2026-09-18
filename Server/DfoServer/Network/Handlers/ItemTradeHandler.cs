using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;

namespace DfoServer.Network.Handlers
{
    internal sealed class ItemTradeHandler : IDisposable
    {
        private readonly ISessionDirectory _sessions;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly BlacklistRepository _blacklist;
        private readonly object _sync = new();
        private readonly Dictionary<Guid, Binding> _bindings = new();

        internal ItemTradeHandler(ISessionDirectory sessions, CharacterTransitionCoordinator transitions, IGameDatabase database)
        {
            _sessions = sessions; _transitions = transitions;
            _blacklist = new BlacklistRepository(database);
            _sessions.SessionEnding += Ending;
        }

        private sealed class Binding
        {
            internal EnhancedClientSession[] Sessions;
            internal ushort[] Uids;
            internal ItemTradeSession Trade;
        }

        private bool Current(Binding b, int i)
        {
            var s = b.Sessions[i]; var l = b.Trade.Leases[i];
            return s.Player.CharacterId == l.CharacterId && s.Player.UserId == b.Uids[i]
                && _sessions.TryGet(l.CharacterId, out var current) && ReferenceEquals(current, s)
                && InventoryContext.TryGetLease(l.CharacterId, out var lease)
                && ReferenceEquals(lease, l) && lease.IsOwnedBy(s.SessionId);
        }

        // Trading is channel-scoped, not town/area-presence scoped. Both peers
        // must still be current, town-ready and outside dungeon transitions.
        private bool Eligible(EnhancedClientSession a, EnhancedClientSession b)
            => a != null && b != null && a != b && a.ListenerPort == b.ListenerPort
            && a.Player.CharacterId != b.Player.CharacterId && a.Player.UserId != 0 && b.Player.UserId != 0
            && a.Player.UserState == 0 && b.Player.UserState == 0
            && a.Player.TownPresenceReady && b.Player.TownPresenceReady
            && !a.Player.DungeonSelectionPending && !b.Player.DungeonSelectionPending
            && a.Player.CurrentRun == null && b.Player.CurrentRun == null
            && !_blacklist.IsBlocked(a.Player.CharacterId, b.Player.CharacterId)
            && !_blacklist.IsBlocked(b.Player.CharacterId, a.Player.CharacterId);

        private Binding Find(EnhancedClientSession s)
        {
            lock (_sync) return _bindings.TryGetValue(s.SessionId, out var b) ? b : null;
        }

        private bool Registered(Binding b)
        {
            lock (_sync) return _bindings.TryGetValue(b.Sessions[0].SessionId, out var x) && ReferenceEquals(x, b)
                && _bindings.TryGetValue(b.Sessions[1].SessionId, out x) && ReferenceEquals(x, b);
        }

        internal async Task Request(EnhancedClientSession s, GamePacketHeader h, byte[] body)
        {
            if (!ItemTradeRequest.TryPeer(body, false, out var uid, out var peer, out _)) return;
            var target = _sessions.GetAllGameSessions().FirstOrDefault(x => x.Player.UserId == uid && x.ListenerPort == s.ListenerPort);
            if (target == null) { await s.SendPacketAsync(ItemTradePacketBuilder.PeerAck(CmdPacketTypeA21.REQUEST_PEER, 3)); return; }
            await _transitions.RunIfBothCurrentAsync(s, target, async () =>
            {
                await Expire(s); await Expire(target);
                if (!Eligible(s, target) || !TryLease(s, out var a) || !TryLease(target, out var b)
                    || a.AccountId == b.AccountId)
                { await s.SendPacketAsync(ItemTradePacketBuilder.PeerAck(CmdPacketTypeA21.REQUEST_PEER, 19)); return; }
                Binding binding = null;
                lock (_sync)
                {
                    if (!_bindings.ContainsKey(s.SessionId) && !_bindings.ContainsKey(target.SessionId))
                    {
                        binding = new Binding { Sessions = new[] { s, target }, Uids = new[] { s.Player.UserId, uid }, Trade = new(a, b, peer) };
                        _bindings.Add(s.SessionId, binding); _bindings.Add(target.SessionId, binding);
                    }
                }
                if (binding == null)
                { await s.SendPacketAsync(ItemTradePacketBuilder.PeerAck(CmdPacketTypeA21.REQUEST_PEER, 19)); return; }
                if (!await Send(binding, 0, ItemTradePacketBuilder.PeerAck(CmdPacketTypeA21.REQUEST_PEER))
                    || !await Send(binding, 1, ItemTradePacketBuilder.Invite(binding.Uids[0], peer)))
                    await Close(binding, false);
            });
        }

        internal async Task Respond(EnhancedClientSession s, GamePacketHeader h, byte[] body)
        {
            if (!ItemTradeRequest.TryPeer(body, true, out var uid, out var peer, out var refused)) return;
            var b = Find(s);
            if (b == null || b.Sessions[1] != s || b.Uids[0] != uid) return;
            await _transitions.RunIfBothCurrentAsync(b.Sessions[0], b.Sessions[1], async () =>
            {
                if (!Registered(b) || !Current(b, 0) || !Current(b, 1) || b.Trade.Active) return;
                if (refused)
                {
                    await Send(b, 0, ItemTradePacketBuilder.PeerAck(CmdPacketTypeA21.RESPONSE_PEER, 0x85));
                    Remove(b); return;
                }
                if (!Eligible(b.Sessions[0], s) || !b.Trade.Accept(b.Trade.Leases[1]))
                { await Close(b, false); return; }
                if (!await Send(b, 1, ItemTradePacketBuilder.Accepted(b.Uids[0], 0, true))
                    || !await Send(b, 0, ItemTradePacketBuilder.Accepted(b.Uids[1], peer, false)))
                    await Close(b, false);
            });
        }

        internal async Task<bool> TryMove(EnhancedClientSession s, GamePacketHeader h, byte[] body)
        {
            if (!ItemTradeRequest.IsTradeMove(body)) return false;
            var b = Find(s);
            async Task Error(string reason)
            {
                FileLogger.Log($"[ItemTrade] MOVE rejected cid={s.Player.CharacterId} reason={reason} bodyLen={body.Length} src={body[0]} dst={body[11]}");
                await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1,
                    (ushort)CmdPacketTypeA21.MOVE_ITEMSPACE,
                    MoveItemSpaceAckBuilder.BuildError(MoveItemSpaceAckBuilder.InvalidOperationErrorCode, body[0], body[11])));
            }
            if (b == null) { await Error("no-negotiation"); return true; }
            await _transitions.RunIfBothCurrentAsync(b.Sessions[0], b.Sessions[1], async () =>
            {
                int side = b.Sessions[0] == s ? 0 : 1;
                if (!Registered(b) || !Current(b, 0) || !Current(b, 1)
                    || !Eligible(b.Sessions[0], b.Sessions[1]))
                { await Close(b, false); return; }
                if (!ItemTradeRequest.TryMove(body, out var request))
                { await Error("invalid-request"); return; }
                short sourceSlot = request.SourceSlotIndex, destinationSlot;
                int itemId = request.SourceInstanceValue, count = request.MoveCount;
                ItemCore projection;
                bool withdrawal = body[0] == 4;
                bool changed = withdrawal
                    ? b.Trade.Withdraw(side, sourceSlot, itemId, count, out destinationSlot, out projection)
                    : b.Trade.Offer(side, sourceSlot, itemId, count, out destinationSlot, out projection);
                if (!changed) { await Error("offer-rejected"); return; }
                var ack = MoveItemSpaceAckBuilder.Build(new InventoryMoveResult
                {
                    SourceListType = (InventoryListType)body[0], SourceSlotIndex = sourceSlot,
                    MoveValue32 = count, DestinationListType = (InventoryListType)body[11],
                    DestinationSlotIndex = destinationSlot, Mutated = true
                });
                if (!await Send(b, side, GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.MOVE_ITEMSPACE, ack))
                    || !await Send(b, 1 - side, ItemTradePacketBuilder.Item(withdrawal ? sourceSlot : destinationSlot, projection, b.Trade.Gold(side))))
                    await Close(b, false);
            });
            return true;
        }

        internal async Task State(EnhancedClientSession s, GamePacketHeader h, byte[] body)
        {
            if (!ItemTradeRequest.TryState(body, out var state)) return;
            var b = Find(s); if (b == null) return;
            await _transitions.RunIfBothCurrentAsync(b.Sessions[0], b.Sessions[1], async () =>
            {
                if (!Registered(b)) return;
                int side = b.Sessions[0] == s ? 0 : 1;
                if (!b.Trade.IsCurrent || !Current(b, 0) || !Current(b, 1) || !Eligible(b.Sessions[0], b.Sessions[1])
                    || (state != 5 && state != 1 && state != 3))
                { await Close(b, false); return; }
                var result = b.Trade.Advance(side, state);
                if (result == ItemTradeAdvance.Rejected) return;
                if (result == ItemTradeAdvance.Committed || result == ItemTradeAdvance.Canceled)
                { await Close(b, result == ItemTradeAdvance.Committed); return; }
                if (state != 3)
                {
                    var packet = ItemTradePacketBuilder.State(b.Uids[side], state);
                    if (!await Send(b, 0, packet) || !await Send(b, 1, packet))
                    { await Close(b, false); return; }
                }
                if (result == ItemTradeAdvance.RegistrationFinished)
                {
                    if (!await Send(b, 0, ItemTradePacketBuilder.RegistrationFinished())
                        || !await Send(b, 1, ItemTradePacketBuilder.RegistrationFinished()))
                        await Close(b, false);
                }
            });
        }

        private async Task Expire(EnhancedClientSession s)
        {
            var b = Find(s);
            if (b != null && (!b.Trade.IsCurrent || (!b.Trade.Active && DateTimeOffset.UtcNow > b.Trade.InvitationDeadline)))
                await Close(b, false);
        }

        internal async Task CancelBeforeTransition(EnhancedClientSession s, ushort command)
        {
            if (command != (ushort)CmdPacketTypeA21.SELECT_CHARACTER
                && command != (ushort)CmdPacketTypeA21.RETURN_SELECT_CHARACTER
                && command != (ushort)CmdPacketTypeA21.ENTER_SELECT_DUNGEON
                && command != (ushort)CmdPacketTypeA21.SET_USER_AREA
                && command != (ushort)CmdPacketTypeA21.TELEPORT
                && command != (ushort)CmdPacketTypeA21.SOLO_TELEPOART
                && command != (ushort)CmdPacketTypeA21.PARTY_TELEPORT
                && command != (ushort)CmdPacketTypeA21.PARTY_TELEPORT_CONFIRM
                && command != (ushort)CmdPacketTypeA21.ENTER_PVP_ROOM) return;
            var b = Find(s); if (b == null) return;
            if (!await _transitions.RunIfBothCurrentAsync(b.Sessions[0], b.Sessions[1],
                    () => Registered(b) ? Close(b, false) : Task.CompletedTask))
                await Close(b, false);
        }

        private bool Remove(Binding b)
        {
            b.Trade.Cancel();
            lock (_sync)
            {
                bool removed = false;
                foreach (var s in b.Sessions)
                    if (_bindings.TryGetValue(s.SessionId, out var current) && ReferenceEquals(current, b))
                        removed |= _bindings.Remove(s.SessionId);
                return removed;
            }
        }

        private async Task Close(Binding b, bool committed, EnhancedClientSession ending = null)
        {
            if (!Remove(b)) return;
            // SessionEnding can race the final transaction. Publish its actual
            // result once; disconnect must not turn a committed exchange into cancel.
            committed = b.Trade.Committed;
            for (int i = 0; i < 2; i++)
            {
                if (b.Sessions[i] == ending || !Current(b, i)) continue;
                int side = i;
                if (committed && b.Sessions[i].GameSession?.QuestManager != null)
                {
                    try
                    {
                        await b.Sessions[i].GameSession.QuestManager.SyncItemSeekingQuestProgressAfterInventoryMutationsAsync(
                            b.Trade.Leases[i], b.Trade.MutationHints());
                    }
                    catch (Exception ex) { FileLogger.Log($"[ItemTrade] quest projection failed cid={b.Trade.Leases[i].CharacterId}: {ex.Message}"); }
                }
                await SessionDirectory.TrySendBestEffortAsync(async cancellation =>
                {
                    bool CanSend() => Current(b, side) && Find(b.Sessions[side]) == null;
                    if (!await b.Sessions[side].TrySendPacketAsync(ItemTradePacketBuilder.Closed(committed), cancellation, CanSend)) return;
                    if (!TryLease(b.Sessions[side], out var lease)) return;
                    byte[] refresh;
                    lock (lease.SyncRoot)
                        refresh = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.ITEM_LIST,
                            ItemListPacketBuilder.BuildItemSpaceListBody(lease.Inventory, InventoryListType.Main));
                    await b.Sessions[side].TrySendPacketAsync(refresh, cancellation,
                        () => CanSend() && InventoryExchangeCommitService.Current(lease));
                }, "item-trade-close");
            }
        }

        private async Task<bool> Send(Binding b, int side, byte[] packet)
        {
            bool delivered = false;
            var sent = await SessionDirectory.TrySendBestEffortAsync(async cancellation =>
                delivered = await b.Sessions[side].TrySendPacketAsync(packet, cancellation,
                    () => Registered(b) && Current(b, 0) && Current(b, 1) && b.Trade.IsCurrent), "item-trade");
            return sent && delivered;
        }

        private Task Ending(int cid, EnhancedClientSession s)
        {
            var b = Find(s);
            return b == null ? Task.CompletedTask : Close(b, false, s);
        }

        private static bool TryLease(EnhancedClientSession s, out InventoryLease lease)
            => InventoryContext.TryGetLease(s.Player.CharacterId, out lease) && lease.IsOwnedBy(s.SessionId);

        public void Dispose()
        {
            _sessions.SessionEnding -= Ending;
            Binding[] bindings;
            lock (_sync) { bindings = _bindings.Values.Distinct().ToArray(); _bindings.Clear(); }
            foreach (var b in bindings) b.Trade.Cancel();
        }
    }
}
