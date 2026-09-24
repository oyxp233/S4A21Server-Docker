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
            public readonly Dictionary<ushort, QuestSetTriggerResult>
                TriggerChanges =
                    new Dictionary<ushort, QuestSetTriggerResult>();
            public int Version;
            public ClockService.ClockTimerHandle Timer;
        }

        private sealed class RefreshSnapshot
        {
            public EnhancedClientSession Session;
            public int CharacterId;
            public short[] Slots;
            public QuestSetTriggerResult[] TriggerChanges;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<Guid, PendingRefresh> _pending =
            new Dictionary<Guid, PendingRefresh>();
        private readonly Func<EnhancedClientSession, IReadOnlyCollection<short>, Task>
            _sendInventoryRefresh;
        private readonly Func<EnhancedClientSession,
            IReadOnlyCollection<QuestSetTriggerResult>, Task>
            _sendTriggerChanges;

        internal QuestDropNotificationBatcher(InventoryRefreshSender inventoryRefresh)
            : this(
                (session, slots) => inventoryRefresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    slots),
                (session, changes) => session?.GameSession?.QuestManager
                    ?.SendTriggerChangesAsync(changes)
                    ?? Task.CompletedTask)
        {
            if (inventoryRefresh == null)
                throw new ArgumentNullException(nameof(inventoryRefresh));
        }

        internal QuestDropNotificationBatcher(
            Func<EnhancedClientSession, IReadOnlyCollection<short>, Task>
                sendInventoryRefresh,
            Func<EnhancedClientSession,
                IReadOnlyCollection<QuestSetTriggerResult>, Task>
                sendTriggerChanges = null)
        {
            _sendInventoryRefresh = sendInventoryRefresh
                ?? throw new ArgumentNullException(nameof(sendInventoryRefresh));
            _sendTriggerChanges = sendTriggerChanges
                ?? ((_, __) => Task.CompletedTask);
        }

        internal void Queue(
            EnhancedClientSession session,
            IEnumerable<short> slots,
            IEnumerable<QuestSetTriggerResult> triggerChanges = null)
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

                if (triggerChanges != null)
                {
                    foreach (var change in triggerChanges)
                    {
                        if (change != null
                            && change.Success
                            && change.QuestId > 0
                            && change.PreviousTriggerValue
                                != change.TriggerValue)
                        {
                            if (!pending.TriggerChanges.TryGetValue(
                                    change.QuestId,
                                    out var existing)
                                || IsLaterTriggerChange(existing, change))
                            {
                                pending.TriggerChanges[change.QuestId] = change;
                            }
                        }
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


            if (session?.Player == null
                || session.Player.CharacterId != snapshot.CharacterId)
            {
                FileLogger.Log(
                    "[GameProtocol] QUEST_DROP trigger refresh skipped because character changed");
                return;
            }

            if (snapshot.TriggerChanges.Length > 0)
            {
                try
                {
                    await _sendTriggerChanges(
                        session,
                        snapshot.TriggerChanges);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[GameProtocol] QUEST_DROP trigger refresh failed: {ex.Message}");
                }
            }
        }

        private static RefreshSnapshot CreateSnapshot(PendingRefresh pending)
            => new RefreshSnapshot
            {
                Session = pending.Session,
                CharacterId = pending.CharacterId,
                Slots = new List<short>(pending.Slots).ToArray(),
                TriggerChanges = new List<QuestSetTriggerResult>(
                    pending.TriggerChanges.Values).ToArray(),
            };

        private static bool IsLaterTriggerChange(
            QuestSetTriggerResult existing,
            QuestSetTriggerResult candidate)
        {
            if (candidate.PreviousTriggerValue
                == existing.TriggerValue)
            {
                return true;
            }

            if (existing.PreviousTriggerValue
                == candidate.TriggerValue)
            {
                return false;
            }

            // Successful CAS updates for one quest form a chain. If the
            // chain is incomplete, keep the first observed state instead of
            // allowing an unrelated late notification to regress it.
            return false;
        }

        private static int NextVersion(int version)
        {
            version = unchecked(version + 1);
            return version == 0 ? 1 : version;
        }

        private static string BuildTimerName(EnhancedClientSession session)
            => "quest-drop:" + session.SessionId.ToString("N") + ":refresh";
    }
}
