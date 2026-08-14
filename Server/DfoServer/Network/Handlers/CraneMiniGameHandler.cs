using DfoServer.Game.CraneMiniGame;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Network.Builders;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class CraneMiniGameHandler
    {
        private const string OverflowMailTitle = "智能娃娃机(1期)";
        private const string OverflowMailText = "智能娃娃机的奖励将以邮件形式发送给您。";
        private readonly CraneMiniGameStartService _startService;
        private readonly CraneMiniGameSessionCoordinator _sessions;
        private readonly InventoryRefreshSender _refresh;
        private readonly MailboxInventoryOverflowRewardSink _overflowRewardSink;

        internal CraneMiniGameHandler(
            InventoryRefreshSender refresh,
            MailboxInventoryOverflowRewardSink overflowRewardSink)
        {
            _startService = new CraneMiniGameStartService();
            _sessions = new CraneMiniGameSessionCoordinator();
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            _overflowRewardSink = overflowRewardSink
                ?? throw new ArgumentNullException(nameof(overflowRewardSink));
        }

        internal async Task HandleStartUse(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (body == null || body.Length != 2 || session?.Player == null)
            {
                await SendFailure(session);
                return;
            }

            var machineId = BitConverter.ToUInt16(body, 0);
            var characterId = session.Player.CharacterId;
            CraneMiniGameStartResult result = null;
            var ok = InventoryContext.TryGetLease(characterId, out var lease)
                && lease.IsOwnedBy(session.SessionId);
            if (ok)
            {
                ok = CraneMiniGameStartCommitService.TryStart(
                    lease,
                    machineId,
                    _startService,
                    out result);
            }

            if (!ok)
            {
                FileLogger.Log($"[GameProtocol] CRANE_START_USE rejected: cid={characterId} machine={machineId}");
                await SendFailure(session);
                return;
            }

            _sessions.Set(session.SessionId, result);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.CRANE_START_USE,
                CraneMiniGameStartAckBuilder.BuildSuccess(result)));
            FileLogger.Log(
                $"[GameProtocol] CRANE_START_USE ok: cid={characterId} machine={machineId} " +
                $"items={string.Join(",", result.DisplayItems.Select(item => $"{item.CatalogIndex}:{item.ItemId}@{item.PickChance:0.##}%"))} " +
                $"materialSlot={result.MaterialSlot} materialRemaining={result.MaterialRemainingCount}");
        }

        internal async Task HandlePickup(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (body == null || body.Length != 6 || session?.Player == null)
            {
                await SendPickupFailure(session);
                return;
            }

            var displaySlot = BitConverter.ToUInt16(body, 0);
            var requestedItemId = BitConverter.ToInt32(body, 2);
            if (!_sessions.TryReservePickup(
                    session.SessionId,
                    displaySlot,
                    requestedItemId,
                    out var reservation))
            {
                FileLogger.Log(
                    $"[GameProtocol] CRANE_PICKUP rejected: cid={session.Player.CharacterId} " +
                    $"displaySlot={displaySlot} item={requestedItemId}");
                await SendPickupFailure(session);
                return;
            }

            var item = reservation.Item;
            if (!reservation.Won)
            {
                await SendPickupFailure(session);
                FileLogger.Log(
                    $"[GameProtocol] CRANE_PICKUP miss: cid={session.Player.CharacterId} " +
                    $"displaySlot={displaySlot} item={item.ItemId} count=0 slot=-1");
                return;
            }

            var hasLease = InventoryContext.TryGetLease(
                    session.Player.CharacterId,
                    out var lease)
                && lease.IsOwnedBy(session.SessionId);
            if (!hasLease)
            {
                _sessions.ReleasePickup(session.SessionId, reservation);
                await SendPickupFailure(session);
                return;
            }

            if (!CraneMiniGamePickupCommitService.TryCommit(
                    lease,
                    reservation,
                    _overflowRewardSink,
                    OverflowMailTitle,
                    OverflowMailText,
                    out var commit))
            {
                _sessions.ReleasePickup(session.SessionId, reservation);
                FileLogger.Log(
                    $"[GameProtocol] CRANE_PICKUP grant failed: cid={session.Player.CharacterId} " +
                    $"displaySlot={displaySlot} item={item.ItemId} count={item.Count}");
                await SendPickupFailure(session);
                return;
            }

            _sessions.CompletePickup(session.SessionId, reservation);
            var grant = commit.Grant;
            var deliveredByMail = commit.DeliveredByMail;
            if (deliveredByMail)
            {
                FileLogger.Log(
                    $"[GameProtocol] CRANE_PICKUP mailed: cid={session.Player.CharacterId} " +
                    $"displaySlot={displaySlot} item={item.ItemId} count={item.Count}");
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.CRANE_PICKUP,
                CraneMiniGamePickupAckBuilder.BuildSuccess(item)));
            if (deliveredByMail)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.MAILBOX_ALARM,
                    MailboxHandler.BuildMailboxAlarmNotification(1)));
            }
            if (!deliveredByMail
                && grant != null
                && grant.Kind == InventoryRewardGrantKind.InventoryItem
                && grant.SlotIndex >= 0)
                await _refresh.SendUpdateItemList(session, grant.ListType, grant.SlotIndex);

            FileLogger.Log(
                $"[GameProtocol] CRANE_PICKUP won: cid={session.Player.CharacterId} " +
                $"displaySlot={displaySlot} item={item.ItemId} count={item.Count} " +
                $"slot={(grant != null ? grant.SlotIndex : -1)}");
        }

        internal void ClearSession(Guid sessionId) => _sessions.Clear(sessionId);

        private static Task SendFailure(EnhancedClientSession session)
        {
            if (session == null)
                return Task.CompletedTask;
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.CRANE_START_USE,
                CraneMiniGameStartAckBuilder.BuildFailure()));
        }

        private static Task SendPickupFailure(EnhancedClientSession session)
        {
            if (session == null)
                return Task.CompletedTask;
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.CRANE_PICKUP,
                CraneMiniGamePickupAckBuilder.BuildFailure()));
        }
    }
}
