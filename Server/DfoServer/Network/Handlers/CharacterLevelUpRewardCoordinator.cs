using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class CharacterLevelUpRewardCoordinator : IDisposable
    {
        private readonly CharacterLevelUpRewardService _service;
        private readonly ISessionDirectory _sessions;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly object _sync = new();
        private readonly string _clockName = "level-up-rewards:" + Guid.NewGuid().ToString("N");
        private ClockService _clock;
        private ClockService.ClockTimerHandle _recoveryTimer;
        private ClockService.ClockTimerHandle _notificationTimer;
        private volatile bool _disposed;

        internal CharacterLevelUpRewardCoordinator(CharacterLevelUpRewardService service,
            ISessionDirectory sessions, CharacterTransitionCoordinator transitions)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
        }

        internal void StartRecovery(ClockService clock)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(CharacterLevelUpRewardCoordinator));
                if (_clock != null) return;
                _clock = clock ?? throw new ArgumentNullException(nameof(clock));
                ScheduleNotifications(Drain());
                ScheduleRecovery();
            }
        }

        // 公共命令完成、角色 gate 释放后调用；覆盖当前请求升级的所有队员。
        internal Task DrainAndNotifyAsync() => NotifyAsync(Drain());

        private IReadOnlyList<CharacterLevelUpRewardDelivery> Drain()
        {
            lock (_sync)
            {
                if (_disposed) return Array.Empty<CharacterLevelUpRewardDelivery>();
                try { return _service.ProcessPending(); }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Progression] level-up reward recovery deferred: {ex}");
                    return Array.Empty<CharacterLevelUpRewardDelivery>();
                }
            }
        }

        private void ScheduleRecovery()
        {
            if (!_disposed)
                _recoveryTimer = _clock.ScheduleOneShotAfter(_clockName, TimeSpan.FromMinutes(1), _ =>
                {
                    // SQLite 工作留在 Clock 的顺序回调内；异步发包另行调度。
                    lock (_sync)
                    {
                        if (_disposed) return;
                        ScheduleNotifications(Drain());
                        ScheduleRecovery();
                    }
                });
        }

        private void ScheduleNotifications(IReadOnlyList<CharacterLevelUpRewardDelivery> delivered)
        {
            if (delivered.Count > 0 && !_disposed)
                _notificationTimer = _clock.ScheduleOneShotAfterAsync(_clockName + ":notify",
                    TimeSpan.Zero, _ => NotifyAsync(delivered));
        }

        internal async Task NotifyAsync(IReadOnlyList<CharacterLevelUpRewardDelivery> delivered)
        {
            foreach (var group in delivered.GroupBy(d => d.CharacterId).OrderBy(g => g.Key))
            {
                if (_disposed) return;
                if (!_sessions.TryGet(group.Key, out var session)) continue;
                byte[] broadcast = null;
                await _transitions.RunIfCurrentAsync(session, async () =>
                {
                    if (_disposed || !InventoryContext.TryGetOwnedLease(session.SessionId, group.Key, out _)) return;
                    // 只根据本次邮件事务实际提交的奖励判断，不能重新读取可能变化的PVF配置。
                    if (group.Any(d => d.Level == LevelUpRewardBroadcastBuilder.NativeLevel
                        && d.ItemId == LevelUpRewardBroadcastBuilder.NativeItemId
                        && d.ItemCount == LevelUpRewardBroadcastBuilder.NativeItemCount)
                        && LevelUpRewardBroadcastBuilder.TryBuild(session.Player.Name, out var body))
                        broadcast = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_BROADCAST, body);
                    var alarm = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.MAILBOX_ALARM,
                        MailboxHandler.BuildMailboxAlarmNotification((ushort)Math.Min(ushort.MaxValue, group.Count())));
                    var message = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_BROADCAST_TIME_MESSAGE,
                        ServerBroadcastTimeMessageBuilder.Build($"恭喜你达到{group.Max(d => d.Level)}级，获得升级奖励"));
                    await SessionDirectory.TrySendBestEffortAsync(async cancellationToken =>
                    {
                        await session.SendPacketAsync(alarm, cancellationToken);
                        await session.SendPacketAsync(message, cancellationToken);
                    }, $"level-up reward cid={group.Key}");
                });
                // 先释放发奖者gate，再逐个获取接收者gate，避免跨角色广播形成嵌套锁。
                if (broadcast != null)
                    await BroadcastNativeRewardAsync(broadcast);
            }
        }

        private async Task BroadcastNativeRewardAsync(byte[] packet)
        {
            foreach (var recipient in _sessions.GetAllGameSessions())
            {
                if (_disposed) return;
                if (recipient?.Player?.CharacterId <= 0) continue;
                await _transitions.RunIfCurrentAsync(recipient, async () =>
                {
                    if (_disposed) return;
                    await SessionDirectory.TrySendBestEffortAsync(
                        cancellationToken => recipient.SendPacketAsync(packet, cancellationToken),
                        $"升级奖励播报，接收角色={recipient.Player.CharacterId}");
                });
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _recoveryTimer?.Cancel();
                _notificationTimer?.Cancel();
            }
        }
    }
}
