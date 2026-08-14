using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    public static class DungeonRunTimerKeys
    {
        public static readonly RunTimerKey SettlementCardAutoFlow =
            new RunTimerKey("settlement", "card-auto-flow");

        public static readonly RunTimerKey CombatDeathRespawn =
            new RunTimerKey("combat", "death-respawn");

        public static readonly RunTimerKey GentInfiltrateTimeout =
            new RunTimerKey("special-dungeon", "gent-infiltrate-timeout");

        public static readonly RunTimerKey TournamentRewardAutoSelect =
            new RunTimerKey("tournament", "reward-auto-select");

        public static readonly RunTimerKey DeathTowerRankingToReward =
            new RunTimerKey("death-tower", "ranking-to-reward");

        public static readonly RunTimerKey DeathTowerRewardToEplp =
            new RunTimerKey("death-tower", "reward-to-eplp");

        public static readonly RunTimerKey DeathTowerReturnToTown =
            new RunTimerKey("death-tower", "return-to-town");

        public static readonly RunTimerKey BloodAltarWaveSchedule =
            new RunTimerKey("blood-altar", "wave-schedule");

        public static readonly RunTimerKey BloodAltarDifficultySelection =
            new RunTimerKey("blood-altar", "difficulty-selection");

        public static readonly RunTimerKey BloodAltarFinalRound =
            new RunTimerKey("blood-altar", "final-round");

        public static readonly RunTimerKey BloodAltarRankingToReward =
            new RunTimerKey("blood-altar", "ranking-to-reward");

        public static readonly RunTimerKey BloodAltarRewardToExit =
            new RunTimerKey("blood-altar", "reward-to-exit");

        public static readonly RunTimerKey BloodAltarReturnToTown =
            new RunTimerKey("blood-altar", "return-to-town");

        public static readonly RunTimerKey BloodAltarSettlementRetry =
            new RunTimerKey("blood-altar", "settlement-retry");
    }

    /// <summary>
    /// Identifies one delayed action owned by one runtime owner, such as a
    /// participant run or a physical dungeon instance.
    /// A mechanism may own multiple purposes, but replacing one purpose never
    /// cancels another purpose accidentally.
    /// </summary>
    public readonly struct RunTimerKey : IEquatable<RunTimerKey>
    {
        public RunTimerKey(string mechanism, string purpose)
        {
            if (string.IsNullOrWhiteSpace(mechanism))
                throw new ArgumentException("A timer mechanism is required.", nameof(mechanism));
            if (string.IsNullOrWhiteSpace(purpose))
                throw new ArgumentException("A timer purpose is required.", nameof(purpose));

            Mechanism = mechanism;
            Purpose = purpose;
        }

        public string Mechanism { get; }
        public string Purpose { get; }

        public bool Equals(RunTimerKey other) =>
            string.Equals(Mechanism, other.Mechanism, StringComparison.Ordinal)
            && string.Equals(Purpose, other.Purpose, StringComparison.Ordinal);

        public override bool Equals(object obj) =>
            obj is RunTimerKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(Mechanism ?? string.Empty),
                StringComparer.Ordinal.GetHashCode(Purpose ?? string.Empty));

        public override string ToString() => Mechanism + "/" + Purpose;
    }

    /// <summary>
    /// Captured by a ClockService callback. It is valid only while the
    /// registry still owns the exact key/generation pair.
    /// </summary>
    public readonly struct RunTimerTicket
    {
        internal RunTimerTicket(RunTimerKey key, int generation)
        {
            Key = key;
            Generation = generation;
        }

        public RunTimerKey Key { get; }
        public int Generation { get; }
        public bool IsValid => Generation != 0;
    }

    public enum RunTimerDetachPolicy
    {
        Cancel,
        SuspendUntilResume,
    }

    public readonly struct RunTimerSnapshot
    {
        internal RunTimerSnapshot(
            RunTimerKey key,
            int generation,
            DateTime deadlineUtc,
            RunTimerDetachPolicy detachPolicy,
            bool suspended)
        {
            Key = key;
            Generation = generation;
            DeadlineUtc = deadlineUtc;
            DetachPolicy = detachPolicy;
            IsSuspended = suspended;
        }

        public RunTimerKey Key { get; }
        public int Generation { get; }
        public DateTime DeadlineUtc { get; }
        public RunTimerDetachPolicy DetachPolicy { get; }
        public bool IsSuspended { get; }
        public bool HasDeadline => DeadlineUtc != DateTime.MinValue;
    }

    /// <summary>
    /// Owner-scoped timer ownership. Callers reserve a ticket before scheduling
    /// a ClockService callback, attach the resulting handle afterwards, and
    /// have the callback validate the same ticket before projecting a typed
    /// event.
    /// </summary>
    public sealed class RunTimerRegistry
    {
        private sealed class Entry
        {
            internal int Generation;
            internal ClockService.ClockTimerHandle Handle;
            internal DateTime DeadlineUtc;
            internal RunTimerDetachPolicy DetachPolicy;
            internal bool Suspended;
        }

        private readonly object _syncRoot = new object();
        private readonly Dictionary<RunTimerKey, Entry> _entries =
            new Dictionary<RunTimerKey, Entry>();

        public RunTimerTicket Begin(RunTimerKey key)
            => Begin(
                key,
                DateTime.MinValue,
                RunTimerDetachPolicy.Cancel);

        public RunTimerTicket Begin(
            RunTimerKey key,
            DateTime deadlineUtc,
            RunTimerDetachPolicy detachPolicy)
        {
            deadlineUtc = NormalizeUtc(deadlineUtc);
            if (detachPolicy == RunTimerDetachPolicy.SuspendUntilResume
                && deadlineUtc == DateTime.MinValue)
            {
                throw new ArgumentException(
                    "A resumable timer requires an absolute deadline.",
                    nameof(deadlineUtc));
            }

            ClockService.ClockTimerHandle previous = null;
            RunTimerTicket ticket;
            lock (_syncRoot)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry();
                    _entries[key] = entry;
                }

                previous = entry.Handle;
                entry.Handle = null;
                entry.Generation = NextGeneration(entry.Generation);
                entry.DeadlineUtc = deadlineUtc;
                entry.DetachPolicy = detachPolicy;
                entry.Suspended = false;
                ticket = new RunTimerTicket(key, entry.Generation);
            }

            previous?.Cancel();
            return ticket;
        }

        public void Attach(RunTimerTicket ticket, ClockService.ClockTimerHandle handle)
        {
            if (!ticket.IsValid || handle == null)
            {
                handle?.Cancel();
                return;
            }

            ClockService.ClockTimerHandle previous = null;
            var attach = false;
            lock (_syncRoot)
            {
                if (_entries.TryGetValue(ticket.Key, out var entry)
                    && entry.Generation == ticket.Generation
                    && !entry.Suspended)
                {
                    previous = entry.Handle;
                    entry.Handle = handle;
                    attach = true;
                }
            }

            if (!attach)
            {
                handle.Cancel();
                return;
            }

            if (previous != null && !ReferenceEquals(previous, handle))
                previous.Cancel();
        }

        public bool IsCurrent(RunTimerTicket ticket)
        {
            if (!ticket.IsValid)
                return false;

            lock (_syncRoot)
            {
                return _entries.TryGetValue(ticket.Key, out var entry)
                    && entry.Generation == ticket.Generation
                    && !entry.Suspended;
            }
        }

        public void Cancel(RunTimerKey key)
        {
            ClockService.ClockTimerHandle handle = null;
            lock (_syncRoot)
            {
                if (!_entries.TryGetValue(key, out var entry))
                    return;

                entry.Generation = NextGeneration(entry.Generation);
                handle = entry.Handle;
                entry.Handle = null;
                entry.DeadlineUtc = DateTime.MinValue;
                entry.DetachPolicy = RunTimerDetachPolicy.Cancel;
                entry.Suspended = false;
            }

            handle?.Cancel();
        }

        public void CancelAll()
        {
            List<ClockService.ClockTimerHandle> handles = null;
            lock (_syncRoot)
            {
                foreach (var entry in _entries.Values)
                {
                    entry.Generation = NextGeneration(entry.Generation);
                    entry.DeadlineUtc = DateTime.MinValue;
                    entry.DetachPolicy = RunTimerDetachPolicy.Cancel;
                    entry.Suspended = false;
                    if (entry.Handle != null)
                    {
                        (handles ??= new List<ClockService.ClockTimerHandle>())
                            .Add(entry.Handle);
                        entry.Handle = null;
                    }
                }
            }

            if (handles == null)
                return;
            foreach (var handle in handles)
                handle.Cancel();
        }

        public int SuspendForNetworkDetach()
        {
            List<ClockService.ClockTimerHandle> handles = null;
            var suspended = 0;
            lock (_syncRoot)
            {
                foreach (var entry in _entries.Values)
                {
                    entry.Generation = NextGeneration(entry.Generation);
                    if (entry.Handle != null)
                    {
                        (handles ??= new List<ClockService.ClockTimerHandle>())
                            .Add(entry.Handle);
                        entry.Handle = null;
                    }

                    if (entry.DetachPolicy
                            == RunTimerDetachPolicy.SuspendUntilResume
                        && entry.DeadlineUtc != DateTime.MinValue)
                    {
                        entry.Suspended = true;
                        suspended++;
                    }
                    else
                    {
                        entry.DeadlineUtc = DateTime.MinValue;
                        entry.DetachPolicy = RunTimerDetachPolicy.Cancel;
                        entry.Suspended = false;
                    }
                }
            }

            if (handles != null)
            {
                foreach (var handle in handles)
                    handle.Cancel();
            }
            return suspended;
        }

        public bool TryResume(
            RunTimerKey key,
            out RunTimerTicket ticket,
            out DateTime deadlineUtc)
        {
            lock (_syncRoot)
            {
                if (_entries.TryGetValue(key, out var entry)
                    && entry.Suspended
                    && entry.DetachPolicy
                        == RunTimerDetachPolicy.SuspendUntilResume
                    && entry.DeadlineUtc != DateTime.MinValue)
                {
                    entry.Generation = NextGeneration(entry.Generation);
                    entry.Suspended = false;
                    ticket = new RunTimerTicket(key, entry.Generation);
                    deadlineUtc = entry.DeadlineUtc;
                    return true;
                }
            }

            ticket = default;
            deadlineUtc = DateTime.MinValue;
            return false;
        }

        public bool TryComplete(RunTimerTicket ticket)
        {
            if (!ticket.IsValid)
                return false;

            ClockService.ClockTimerHandle handle = null;
            lock (_syncRoot)
            {
                if (!_entries.TryGetValue(ticket.Key, out var entry)
                    || entry.Generation != ticket.Generation
                    || entry.Suspended)
                {
                    return false;
                }

                entry.Generation = NextGeneration(entry.Generation);
                handle = entry.Handle;
                entry.Handle = null;
                entry.DeadlineUtc = DateTime.MinValue;
                entry.DetachPolicy = RunTimerDetachPolicy.Cancel;
                entry.Suspended = false;
            }

            handle?.Cancel();
            return true;
        }

        public bool TryGetSnapshot(
            RunTimerKey key,
            out RunTimerSnapshot snapshot)
        {
            lock (_syncRoot)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    snapshot = new RunTimerSnapshot(
                        key,
                        entry.Generation,
                        entry.DeadlineUtc,
                        entry.DetachPolicy,
                        entry.Suspended);
                    return true;
                }
            }

            snapshot = default;
            return false;
        }

        public int GetGeneration(RunTimerKey key)
        {
            lock (_syncRoot)
            {
                return _entries.TryGetValue(key, out var entry)
                    ? entry.Generation
                    : 0;
            }
        }

        public bool TryGetCurrentTicket(
            RunTimerKey key,
            out RunTimerTicket ticket)
        {
            lock (_syncRoot)
            {
                if (_entries.TryGetValue(key, out var entry)
                    && entry.Generation != 0
                    && !entry.Suspended)
                {
                    ticket = new RunTimerTicket(key, entry.Generation);
                    return true;
                }
            }

            ticket = default;
            return false;
        }

        private static int NextGeneration(int previous)
        {
            var next = previous == int.MaxValue ? 1 : previous + 1;
            return next == 0 ? 1 : next;
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value == DateTime.MinValue)
                return DateTime.MinValue;
            if (value.Kind == DateTimeKind.Utc)
                return value;
            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
