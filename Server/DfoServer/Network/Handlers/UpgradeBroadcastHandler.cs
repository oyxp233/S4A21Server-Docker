using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class UpgradeBroadcastHandler : IDisposable
    {
        private readonly object _sync = new();
        private readonly Dictionary<ushort, UpgradeBroadcastRoom> _rooms = new();
        private readonly Dictionary<Guid, UpgradeBroadcastRoom> _membership = new();
        private readonly Dictionary<Guid, (int Character, byte Town, byte Area)> _discovery = new();
        private readonly ISessionDirectory _sessions;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly InventoryRefreshSender _refresh;
        private readonly Func<EnhancedClientSession, ItemUpgradeResult, Task> _notice;
        private ushort _next;
        private bool _disposed;

        internal UpgradeBroadcastHandler(ISessionDirectory sessions, CharacterTransitionCoordinator transitions,
            InventoryRefreshSender refresh, Func<EnhancedClientSession, ItemUpgradeResult, Task> notice)
        {
            _sessions = sessions; _transitions = transitions; _refresh = refresh; _notice = notice;
            _sessions.SessionEnding += Ending;
        }

        internal void Register(GameCommandRegistry.GameCommandRegistrationGroup group)
        {
            foreach (var cmd in new[] { CmdPacketTypeA21.CREATE_UPGRADE_ROOM, CmdPacketTypeA21.DESTROY_UPGRADE_ROOM,
                CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM, CmdPacketTypeA21.LEAVE_SPECTATOR_UPGRADE_ROOM,
                CmdPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO, CmdPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM,
                CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_START, CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_CANCEL,
                CmdPacketTypeA21.UPGRADE_ROOM_MASTER_MESSAGE }) group[(ushort)cmd] = Handle;
        }

        private bool Current(EnhancedClientSession s) => !_disposed && s?.Player.CharacterId > 0
            && s.Player.UserId != 0 && _transitions.IsCurrent(s)
            && InventoryContext.TryGetOwnedLease(s.SessionId, s.Player.CharacterId, out _);
        private bool Eligible(EnhancedClientSession s) => Current(s) && s.Player.UserState == 0
            && s.Player.TownPresenceReady && !s.Player.DungeonSelectionPending && s.Player.CurrentRun == null;
        private bool SameArea(UpgradeBroadcastRoom r, EnhancedClientSession s) => Eligible(s)
            && r.Listener == s.ListenerPort && r.Town == s.Player.CurTownId && r.Area == s.Player.CurAreaId;
        private bool Live(UpgradeBroadcastRoom r)
        { lock (_sync) return !r.Closed && _rooms.TryGetValue(r.Id, out var active) && ReferenceEquals(r, active)
            && SameArea(r, r.Host) && r.Host.Player.CharacterId == r.CharacterId && r.Host.Player.UserId == r.HostUid; }
        private UpgradeBroadcastRoom Find(EnhancedClientSession s)
        { lock (_sync) return _membership.TryGetValue(s.SessionId, out var room) && Live(room) ? room : null; }

        internal async Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!Eligible(session)) return;
            await Reconcile(session);
            if (header.type == (ushort)CmdPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO)
            { await Donate(session, header, body); return; }
            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!Eligible(session)) return;
                var cmd = (CmdPacketTypeA21)header.type;
                if (cmd == CmdPacketTypeA21.CREATE_UPGRADE_ROOM) { await Create(session, body); return; }
                if (cmd == CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM) { await Join(session, body); return; }
                var room = Find(session);
                if (room == null) { await Failure(session, "请先创建或进入强化直播间。"); return; }
                if (cmd == CmdPacketTypeA21.LEAVE_SPECTATOR_UPGRADE_ROOM && (body?.Length ?? 0) == 0)
                { await Accept(session, cmd); await Remove(session); return; }
                if (!ReferenceEquals(room.Host, session))
                { await Failure(session, "只有直播间主人可以操作强化装备。"); return; }
                if (cmd == CmdPacketTypeA21.DESTROY_UPGRADE_ROOM && (body?.Length ?? 0) == 0)
                { await Accept(session, cmd); await Remove(session); return; }
                if (cmd == CmdPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM) { await Put(room, body); return; }
                if (cmd == CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_START && (body?.Length ?? 0) == 0)
                { await Start(room, header); return; }
                if (cmd == CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_CANCEL && (body?.Length ?? 0) == 0)
                {
                    lock (_sync) { room.State = room.Selected == null ? (byte)0 : (byte)3; room.Revision++; }
                    await Accept(session, cmd);
                    await Members(room, UpgradeBroadcastPacketBuilder.Cancelled()); return;
                }
                if (cmd == CmdPacketTypeA21.UPGRADE_ROOM_MASTER_MESSAGE && TryTitle(body, out var title))
                {
                    lock (_sync) { room.Title = title; room.Revision++; }
                    await Accept(session, cmd);
                    await Members(room, UpgradeBroadcastPacketBuilder.Title(title)); return;
                }
            });
        }

        private async Task Create(EnhancedClientSession host, byte[] body)
        {
            if (body?.Length != 4) return;
            var previous = Find(host);
            if (previous != null)
            {
                if (ReferenceEquals(previous.Host, host)) { await Accept(host, CmdPacketTypeA21.CREATE_UPGRADE_ROOM); await SendInfo(previous, host); return; }
                await Remove(host);
            }
            UpgradeBroadcastRoom room;
            lock (_sync)
            {
                do { _next++; } while (_next == 0 || _rooms.ContainsKey(_next));
                room = new UpgradeBroadcastRoom { Id = _next, Host = host, CharacterId = host.Player.CharacterId,
                    HostUid = host.Player.UserId, Listener = host.ListenerPort, Town = host.Player.CurTownId, Area = host.Player.CurAreaId,
                    X = BitConverter.ToInt16(body, 0), Y = BitConverter.ToInt16(body, 2),
                    Title = ClientTextEncoding.GetBytes(ClientTextEncoding.GetString(host.Player.Name) + "的强化直播") };
                _rooms.Add(room.Id, room); _membership[host.SessionId] = room;
            }
            await Accept(host, CmdPacketTypeA21.CREATE_UPGRADE_ROOM);
            await Area(room, UpgradeBroadcastPacketBuilder.Created(room));
            await SendInfo(room, host);
            FileLogger.Log($"[UpgradeBroadcast] create room={room.Id} cid={room.CharacterId} listener={room.Listener}");
        }

        private async Task Join(EnhancedClientSession viewer, byte[] body)
        {
            if (body?.Length != 4) return;
            ushort id = BitConverter.ToUInt16(body, 0), uid = BitConverter.ToUInt16(body, 2);
            UpgradeBroadcastRoom room;
            lock (_sync) _rooms.TryGetValue(id, out room);
            if (room == null || !Live(room) || !SameArea(room, viewer) || room.HostUid != uid)
            { await Failure(viewer, "直播间已关闭，或不在当前频道和区域。"); return; }
            if (ReferenceEquals(room.Host, viewer)) { await Accept(viewer, CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM); await SendInfo(room, viewer); return; }
            if (!ReferenceEquals(Find(viewer), room)) await Remove(viewer);
            lock (_sync)
            {
                if (!Live(room) || room.Viewers.Count >= 100) return;
                room.Viewers.Add(viewer); _membership[viewer.SessionId] = room;
            }
            await Accept(viewer, CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM);
            await Members(room, UpgradeBroadcastPacketBuilder.Joined(viewer.Player.UserId), viewer);
            await SendInfo(room, viewer);
        }

        private async Task SendInfo(UpgradeBroadcastRoom room, EnhancedClientSession target)
        {
            byte[] info, put, preview, donations, result, started;
            lock (_sync) { info = UpgradeBroadcastPacketBuilder.Info(room); put = room.Selected == null ? null : UpgradeBroadcastPacketBuilder.Put(room);
                preview = room.Selected == null ? null : UpgradeBroadcastPacketBuilder.EquipmentPreview(room); donations = UpgradeBroadcastPacketBuilder.Donations(room);
                result = room.State == 2 && room.LastResult != null ? UpgradeBroadcastPacketBuilder.Result(room, room.LastResult) : null;
                started = room.State == 1 ? UpgradeBroadcastPacketBuilder.Started() : null; }
            await Send(target, info, () => Live(room) && ReferenceEquals(Find(target), room));
            if (put != null) await Send(target, put, () => Live(room) && ReferenceEquals(Find(target), room));
            if (preview != null) await Send(target, preview, () => Live(room) && ReferenceEquals(Find(target), room));
            await Send(target, donations, () => Live(room) && ReferenceEquals(Find(target), room));
            if (started != null) await Send(target, started, () => Live(room) && ReferenceEquals(Find(target), room));
            if (result != null) await Send(target, result, () => Live(room) && ReferenceEquals(Find(target), room));
        }

        private async Task Put(UpgradeBroadcastRoom room, byte[] body)
        {
            if (body?.Length != 6 || !InventoryContext.TryGetOwnedLease(room.Host.SessionId, room.CharacterId, out var lease)) return;
            int itemId = BitConverter.ToInt32(body, 0); short slot = BitConverter.ToInt16(body, 4);
            ItemCore selected;
            lock (lease.SyncRoot)
            {
                var item = lease.Inventory.GetItem(InventoryListType.Main, slot);
                selected = item?.ItemKind == ItemCore.KindEquipment && item.ItemId == itemId ? item.Copy() : null;
            }
            if (selected == null) { await Failure(room.Host, "请放入当前背包中的可强化装备。"); return; }
            lock (_sync) { room.Selected = selected; room.Slot = slot; room.State = 3; room.LastResult = null; room.Revision++; }
            await Accept(room.Host, CmdPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM);
            await Area(room, UpgradeBroadcastPacketBuilder.Put(room));
            await Members(room, UpgradeBroadcastPacketBuilder.EquipmentPreview(room));
            FileLogger.Log($"[UpgradeBroadcast] put room={room.Id} cid={room.CharacterId} slot={slot} item={selected.ItemId} level={selected.Upgrade}");
        }

        private async Task Start(UpgradeBroadcastRoom room, GamePacketHeader header)
        {
            ItemCore source; short slot;
            lock (_sync)
            {
                if (!Live(room) || room.State == 1 || room.Selected == null || room.Starts.Contains((header.seq, header.checksum))) return;
                source = room.Selected.Copy(); slot = room.Slot;
            }
            if (!InventoryContext.TryGetOwnedLease(room.Host.SessionId, room.CharacterId, out var lease)) return;
            bool valid;
            lock (lease.SyncRoot)
            {
                var current = lease.Inventory.GetItem(InventoryListType.Main, slot);
                valid = current != null && current.ToBytes().AsSpan().SequenceEqual(source.ToBytes())
                    && InventorySpaceCheckService.HasEnoughMaterialFreeSlots(lease.Inventory, out _, out _)
                    && BuildCommand(lease.Inventory, slot, source) != null;
            }
            if (!valid) { await Failure(room.Host, "当前装备不能进行直播强化。"); return; }
            lock (_sync)
            {
                room.State = 1; room.LastResult = null; room.Revision++;
                var key = (header.seq, header.checksum); room.Starts.Add(key); room.StartOrder.Enqueue(key);
                if (room.StartOrder.Count > 64) room.Starts.Remove(room.StartOrder.Dequeue());
            }
            await Accept(room.Host, CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_START);
            await Members(room, UpgradeBroadcastPacketBuilder.Started());
            // 01233920 starts the native timer. After it finishes, 012332B0
            // calls 016FD060 and sends UPGRADE_ITEM. That request commits once.
            FileLogger.Log($"[UpgradeBroadcast] animation room={room.Id} cid={room.CharacterId} slot={slot}");
        }

        private static ItemUpgradeCommand BuildCommand(InventoryService inventory, short slot, ItemCore source)
        {
            var metadata = ItemMetadataResolver.Resolve(source.ItemId);
            if (metadata == null || !ItemUpgradeTableProvider.TryGetRow(ItemUpgradeTableKind.Normal, source.Upgrade + 1, out var row)) return null;
            var cost = ItemUpgradeTableProvider.BuildCost(ItemUpgradeTableKind.Normal, row, new EquipmentUpgradeCostInput
            { EquipmentLevel = metadata.MinimumLevel, Rarity = metadata.Rarity, EquipmentType = EquipmentTypeInfo.ParseOrUnknown(metadata.EquipmentType), CurrentUpgradeLevel = source.Upgrade });
            short material = -1;
            if (InventoryService.TryResolveMainVirtualSlotByItemId(cost.MaterialItemId, out var virtualSlot, out _)) material = virtualSlot;
            else foreach (var item in inventory.GetItems(InventoryListType.Main))
                if (item.Value.ItemId == cost.MaterialItemId && item.Value.Count >= cost.MaterialCount) { material = item.Key; break; }
            return new ItemUpgradeCommand { Method = ItemUpgradeMethod.Reinforce, Mode = ItemUpgradeMode.Reinforce,
                TargetSlotIndex = slot, TargetItemTemplateId = source.ItemId, MaterialSlotIndex = material, OptionalTicketSlotIndex = -1, TargetItemName = string.Empty };
        }

        internal async Task<bool> TryHandleUpgradeCompletion(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var room = Find(session);
            if (room == null || !ReferenceEquals(room.Host, session)) return false;
            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!Live(room) || !ReferenceEquals(Find(session), room)) return;
                ItemCore source; short slot; var key = (header.seq, header.checksum);
                lock (_sync)
                {
                    if (room.Completions.Contains(key)) return;
                    source = room.State == 1 ? room.Selected?.Copy() : null; slot = room.Slot;
                }
                if (source == null)
                { await UpgradeRejected(session, ItemUpgradeResult.ErrorInvalidTarget); return; }
                if (!ItemUpgradeRequest.TryParse(body, out var request)
                    || request.Method != ItemUpgradeMethod.Reinforce || request.Mode != ItemUpgradeMode.Reinforce
                    || request.TargetSlotIndex != slot || request.TargetItemTemplateId != source.ItemId
                    || !InventoryContext.TryGetOwnedLease(session.SessionId, room.CharacterId, out var lease))
                { await CompletionFailed(room, ItemUpgradeResult.ErrorInvalidTarget); return; }
                bool space;
                lock (lease.SyncRoot) space = InventorySpaceCheckService.HasEnoughMaterialFreeSlots(lease.Inventory, out _, out _);
                if (!space) { await CompletionFailed(room, ItemUpgradeResult.ErrorInventoryFull); return; }
                if (!InventoryItemUpgradeCommitService.TryCommit(lease, request.ToCommand(), out var result, out var persistenceFailed, source))
                { await CompletionFailed(room, persistenceFailed ? ItemUpgradeResult.ErrorInvalidTarget : result?.ErrorCode ?? ItemUpgradeResult.ErrorInvalidTarget); return; }
                lock (_sync)
                {
                    room.LastResult = result; room.State = 2; room.Revision++;
                    room.Selected = result.TargetItemSnapshot?.Copy();
                    room.Completions.Add(key); room.CompletionOrder.Enqueue(key);
                    if (room.CompletionOrder.Count > 64) room.Completions.Remove(room.CompletionOrder.Dequeue());
                }
                await Area(room, UpgradeBroadcastPacketBuilder.Result(room, result));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.UPGRADE_ITEM, ItemUpgradeAckBuilder.BuildSuccess(result)));
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, result.MainRefreshSlots);
                if (result.GoldCost > 0) await _refresh.SendUpdateItemList(session, InventoryListType.Main, InventoryService.MainVirtualCurrencySlotStart);
                if (result.NoticeRequired && _notice != null) await _notice(session, result);
                FileLogger.Log($"[UpgradeBroadcast] result room={room.Id} cid={room.CharacterId} slot={slot} level={result.OldLevel}->{result.NewLevel} code={result.ResultCode}");
            });
            return true;
        }

        private async Task CompletionFailed(UpgradeBroadcastRoom room, byte error)
        {
            lock (_sync) { room.State = 3; room.Revision++; }
            await Members(room, UpgradeBroadcastPacketBuilder.Cancelled());
            await UpgradeRejected(room.Host, error);
        }

        private static Task UpgradeRejected(EnhancedClientSession session, byte error)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.UPGRADE_ITEM, ItemUpgradeAckBuilder.BuildError(error)));

        private async Task Ending(int characterId, EnhancedClientSession session) => await Remove(session);
        private async Task Remove(EnhancedClientSession session)
        {
            UpgradeBroadcastRoom room; bool host;
            lock (_sync)
            {
                _discovery.Remove(session.SessionId);
                if (!_membership.Remove(session.SessionId, out room)) return;
                host = ReferenceEquals(room.Host, session);
                if (host)
                {
                    room.Closed = true; _rooms.Remove(room.Id);
                    foreach (var viewer in room.Viewers) _membership.Remove(viewer.SessionId);
                }
                else room.Viewers.Remove(session);
            }
            if (host)
            {
                foreach (var viewer in room.Viewers.ToArray())
                    await Send(viewer, UpgradeBroadcastPacketBuilder.Left(viewer.Player.UserId), () => Current(viewer));
                foreach (var target in _sessions.GetAllGameSessions().Where(s => SameArea(room, s)))
                    await Send(target, UpgradeBroadcastPacketBuilder.Destroyed(room), () => SameArea(room, target));
            }
            else
            {
                await Members(room, UpgradeBroadcastPacketBuilder.Left(session.Player.UserId));
                await Send(session, UpgradeBroadcastPacketBuilder.Left(session.Player.UserId), () => Current(session));
            }
        }

        internal async Task Reconcile(EnhancedClientSession session)
        {
            if (_disposed) return;
            // Runs after every handled packet, so it only inspects this
            // session's membership and discovery key. Stale rooms are closed by
            // their own host's reconcile or by SessionEnding, never by a global scan.
            UpgradeBroadcastRoom member;
            bool joined;
            lock (_sync) joined = _membership.TryGetValue(session.SessionId, out member);
            if (joined && (!Live(member) || !SameArea(member, session))) await Remove(session);
            if (!Eligible(session))
            {
                // Discovery is rebuilt once the session is eligible again.
                lock (_sync) _discovery.Remove(session.SessionId);
                return;
            }
            var key = (session.Player.CharacterId, session.Player.CurTownId, session.Player.CurAreaId);
            byte[][] packets;
            lock (_sync)
            {
                if (_discovery.TryGetValue(session.SessionId, out var previous) && previous == key) return;
                _discovery[session.SessionId] = key;
                var visible = _rooms.Values.Where(r => Live(r) && SameArea(r, session)).ToArray();
                if (visible.Length == 0) return;
                packets = new[] { UpgradeBroadcastPacketBuilder.List(visible) }.Concat(visible
                    .Where(r => r.State == 2 && r.LastResult != null)
                    .Select(r => UpgradeBroadcastPacketBuilder.Result(r, r.LastResult))).ToArray();
            }
            foreach (var packet in packets)
                await Send(session, packet, () => Eligible(session) && key == (session.Player.CharacterId, session.Player.CurTownId, session.Player.CurAreaId));
        }

        private Task Area(UpgradeBroadcastRoom room, byte[] packet) => Task.WhenAll(_sessions.GetAllGameSessions()
            .Where(s => SameArea(room, s)).Select(s => Send(s, packet, () => Live(room) && SameArea(room, s))));
        private Task Members(UpgradeBroadcastRoom room, byte[] packet, EnhancedClientSession exclude = null)
        {
            EnhancedClientSession[] members;
            lock (_sync) members = room.Viewers.Append(room.Host).Where(s => s != exclude).ToArray();
            return Task.WhenAll(members.Select(s => Send(s, packet, () => Live(room) && SameArea(room, s) && ReferenceEquals(Find(s), room))));
        }
        private static Task Send(EnhancedClientSession target, byte[] packet, Func<bool> current)
            => SessionDirectory.TrySendBestEffortAsync(ct => target.TrySendPacketAsync(packet, ct, current), "upgrade broadcast");
        private static Task Failure(EnhancedClientSession session, string text) => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0,
            (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE, ServerNoticeMessageBuilder.Build(text)));
        private static Task Accept(EnhancedClientSession session, CmdPacketTypeA21 command)
            => session.SendPacketAsync(UpgradeBroadcastPacketBuilder.Accepted(command));
        private static bool TryTitle(byte[] body, out byte[] title)
        {
            title = null; if (body == null || body.Length < 5) return false;
            int n = BitConverter.ToInt32(body, 0); if (n < 1 || n > 80 || body.Length != n + 4) return false;
            var bytes = body.AsSpan(4).ToArray();
            if (!ClientTextEncoding.TryGetStringStrict(bytes, out var text) || text.Any(char.IsControl)) return false;
            title = bytes; return true;
        }
        public void Dispose()
        {
            _disposed = true; _sessions.SessionEnding -= Ending;
            lock (_sync) { foreach (var room in _rooms.Values) room.Closed = true; _rooms.Clear(); _membership.Clear(); _discovery.Clear(); }
        }
    }
}
