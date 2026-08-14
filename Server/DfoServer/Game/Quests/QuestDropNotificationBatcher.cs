using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestDropNotificationBatcher
    {
        private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(100);

        private sealed class PendingRefresh
        {
            public EnhancedClientSession Session;
            public int CharacterId;
            public readonly HashSet<short> Slots = new HashSet<short>();
            public int Version;
            public ClockService.ClockTimerHandle Timer;
        }

        private sealed class RefreshSnapshot
        {
            public EnhancedClientSession Session;
            public int CharacterId;
            public short[] Slots;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<Guid, PendingRefresh> _pending =
            new Dictionary<Guid, PendingRefresh>();
        private readonly Func<EnhancedClientSession, IReadOnlyCollection<short>, Task>
            _sendInventoryRefresh;

        internal QuestDropNotificationBatcher(InventoryRefreshSender inventoryRefresh)
            : this(
                (session, slots) => inventoryRefresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    slots))
        {
            if (inventoryRefresh == null)
                throw new ArgumentNullException(nameof(inventoryRefresh));
        }

        internal QuestDropNotificationBatcher(
            Func<EnhancedClientSession, IReadOnlyCollection<short>, Task>
                sendInventoryRefresh)
        {
            _sendInventoryRefresh = sendInventoryRefresh
                ?? throw new ArgumentNullException(nameof(sendInventoryRefresh));
        }

        internal void Queue(
            EnhancedClientSession session,
            IEnumerable<short> slots)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0)
                return;

            lock (_sync)
            {
                if (!_pending.TryGetValue(session.SessionId, out var pending)
                    || pending.CharacterId != session.Player.CharacterId)
                {
                    pending?.Timer?.Cancel();
                    pending = new PendingRefresh
                    {
                        Session = session,
                        CharacterId = session.Player.CharacterId,
                    };
                    _pending[session.SessionId] = pending;
                }

                if (slots != null)
                {
                    foreach (var slot in slots)
                    {
                        if (slot >= 0)
                            pending.Slots.Add(slot);
                    }
                }

                pending.Version = NextVersion(pending.Version);
                var version = pending.Version;
                pending.Timer = ClockService.Instance.ScheduleOneShotAfterAsync(
                    BuildTimerName(session),
                    FlushDelay,
                    _ => FlushScheduledAsync(session.SessionId, version));
            }
        }

        internal async Task<bool> FlushPendingAsync(EnhancedClientSession session)
        {
            if (session == null)
                return false;

            RefreshSnapshot snapshot;
            ClockService.ClockTimerHandle timer;
            lock (_sync)
            {
                if (!_pending.TryGetValue(session.SessionId, out var pending))
                    return false;

                _pending.Remove(session.SessionId);
                timer = pending.Timer;
                snapshot = CreateSnapshot(pending);
            }

            timer?.Cancel();
            await SendSnapshotAsync(snapshot);
            return true;
        }

        private async Task FlushScheduledAsync(Guid sessionId, int version)
        {
            RefreshSnapshot snapshot;
            lock (_sync)
            {
                if (!_pending.TryGetValue(sessionId, out var pending)
                    || pending.Version != version)
                {
                    return;
                }

                _pending.Remove(sessionId);
                snapshot = CreateSnapshot(pending);
            }

            await SendSnapshotAsync(snapshot);
        }

        private async Task SendSnapshotAsync(RefreshSnapshot snapshot)
        {
            var session = snapshot?.Session;
            if (session?.Player == null
                || session.Player.CharacterId != snapshot.CharacterId)
            {
                FileLogger.Log(
                    "[GameProtocol] QUEST_DROP refresh skipped because character changed");
                return;
            }

            if (snapshot.Slots.Length > 0)
            {
                try
                {
                    await _sendInventoryRefresh(session, snapshot.Slots);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[GameProtocol] QUEST_DROP inventory refresh failed: {ex.Message}");
                }
            }
        }

        private static RefreshSnapshot CreateSnapshot(PendingRefresh pending)
            => new RefreshSnapshot
            {
                Session = pending.Session,
                CharacterId = pending.CharacterId,
                Slots = new List<short>(pending.Slots).ToArray(),
            };

        private static int NextVersion(int version)
        {
            version = unchecked(version + 1);
            return version == 0 ? 1 : version;
        }

        private static string BuildTimerName(EnhancedClientSession session)
            => "quest-drop:" + session.SessionId.ToString("N") + ":refresh";
    }
}
