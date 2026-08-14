using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    public enum DungeonEffectScope
    {
        Instance = 0,
        Party = 1,
        Room = 2,
        Player = 3,
        Persistent = 4,
    }

    public enum DungeonEffectState
    {
        Absent = 0,
        Reserved = 1,
        Committed = 2,
        Failed = 3,
    }

    public readonly struct DungeonEffectId : IEquatable<DungeonEffectId>
    {
        public DungeonEffectId(
            Guid sourceEventId,
            string effectKind,
            DungeonEffectScope scope,
            long scopeTarget)
        {
            if (sourceEventId == Guid.Empty)
                throw new ArgumentException("An effect requires a source event ID.", nameof(sourceEventId));
            if (string.IsNullOrWhiteSpace(effectKind))
                throw new ArgumentException("An effect kind is required.", nameof(effectKind));

            SourceEventId = sourceEventId;
            EffectKind = effectKind;
            Scope = scope;
            ScopeTarget = scopeTarget;
        }

        public Guid SourceEventId { get; }
        public string EffectKind { get; }
        public DungeonEffectScope Scope { get; }
        public long ScopeTarget { get; }

        public bool Equals(DungeonEffectId other) =>
            SourceEventId == other.SourceEventId
            && string.Equals(EffectKind, other.EffectKind, StringComparison.Ordinal)
            && Scope == other.Scope
            && ScopeTarget == other.ScopeTarget;

        public override bool Equals(object obj) => obj is DungeonEffectId other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(SourceEventId, EffectKind, Scope, ScopeTarget);
    }

    public readonly struct DungeonEffectReservation
    {
        internal DungeonEffectReservation(DungeonEffectId effectId, Guid leaseId)
        {
            EffectId = effectId;
            LeaseId = leaseId;
        }

        public DungeonEffectId EffectId { get; }
        internal Guid LeaseId { get; }
        public bool IsValid => LeaseId != Guid.Empty;
    }

    public sealed class DungeonEffectLedger
    {
        private sealed class Entry
        {
            internal DungeonEffectState State;
            internal Guid LeaseId;
        }

        private readonly object _syncRoot = new object();
        private readonly Dictionary<DungeonEffectId, Entry> _entries =
            new Dictionary<DungeonEffectId, Entry>();

        public bool TryReserve(
            DungeonEffectId effectId,
            out DungeonEffectReservation reservation)
        {
            lock (_syncRoot)
            {
                if (_entries.TryGetValue(effectId, out var existing)
                    && (existing.State == DungeonEffectState.Reserved
                        || existing.State == DungeonEffectState.Committed))
                {
                    reservation = default;
                    return false;
                }

                var leaseId = Guid.NewGuid();
                _entries[effectId] = new Entry
                {
                    State = DungeonEffectState.Reserved,
                    LeaseId = leaseId,
                };
                reservation = new DungeonEffectReservation(effectId, leaseId);
                return true;
            }
        }

        public bool TryCommit(DungeonEffectReservation reservation)
        {
            lock (_syncRoot)
            {
                if (!TryGetOwnedReservation(reservation, out var entry))
                    return false;

                entry.State = DungeonEffectState.Committed;
                entry.LeaseId = Guid.Empty;
                return true;
            }
        }

        public bool TryFail(DungeonEffectReservation reservation)
        {
            lock (_syncRoot)
            {
                if (!TryGetOwnedReservation(reservation, out var entry))
                    return false;

                entry.State = DungeonEffectState.Failed;
                entry.LeaseId = Guid.Empty;
                return true;
            }
        }

        internal void ProjectCommitted(DungeonEffectId effectId)
        {
            lock (_syncRoot)
            {
                _entries[effectId] = new Entry
                {
                    State = DungeonEffectState.Committed,
                    LeaseId = Guid.Empty,
                };
            }
        }

        public DungeonEffectState GetState(DungeonEffectId effectId)
        {
            lock (_syncRoot)
            {
                return _entries.TryGetValue(effectId, out var entry)
                    ? entry.State
                    : DungeonEffectState.Absent;
            }
        }

        private bool TryGetOwnedReservation(
            DungeonEffectReservation reservation,
            out Entry entry)
        {
            entry = null;
            return reservation.IsValid
                && _entries.TryGetValue(reservation.EffectId, out entry)
                && entry.State == DungeonEffectState.Reserved
                && entry.LeaseId == reservation.LeaseId;
        }
    }
}
