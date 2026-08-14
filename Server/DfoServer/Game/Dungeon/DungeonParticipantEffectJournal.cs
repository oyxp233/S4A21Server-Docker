using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonParticipantEffectState
    {
        Pending = 0,
        InFlight = 1,
        Committed = 2,
        Failed = 3,
    }

    internal enum DungeonParticipantEffectAudience
    {
        Room = 0,
        Instance = 1,
    }

    internal static class DungeonParticipantEffectKinds
    {
        internal const string MonsterKill = "monster-kill-participant";
        internal const string DungeonClear = "dungeon-clear-participant";
    }

    // A roster entry is a frozen participant identity, not a live party lookup.
    // The Run reference is retained so an in-process recovery can execute the
    // existing application service; session ownership is resolved at execution time.
    internal sealed class DungeonParticipantRosterEntry
    {
        internal DungeonParticipantRosterEntry(
            int characterId,
            ushort participantUserId,
            DungeonRun run,
            DungeonRunIdentity runIdentity,
            DungeonRoomIdentity roomIdentity,
            long attachmentGeneration)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (participantUserId == 0)
                throw new ArgumentOutOfRangeException(nameof(participantUserId));
            if (run == null)
                throw new ArgumentNullException(nameof(run));
            if (!runIdentity.IsValid)
                throw new ArgumentException("A participant roster requires a valid run identity.", nameof(runIdentity));
            if (!roomIdentity.IsValid)
                throw new ArgumentException("A participant roster requires a valid room identity.", nameof(roomIdentity));

            CharacterId = characterId;
            ParticipantUserId = participantUserId;
            Run = run;
            RunIdentity = runIdentity;
            RoomIdentity = roomIdentity;
            AttachmentGeneration = attachmentGeneration;
        }

        internal int CharacterId { get; }
        internal ushort ParticipantUserId { get; }
        internal DungeonRun Run { get; }
        internal DungeonRunIdentity RunIdentity { get; }
        internal DungeonRoomIdentity RoomIdentity { get; }
        internal long AttachmentGeneration { get; }
    }

    internal readonly struct DungeonParticipantEffectReservation
    {
        internal DungeonParticipantEffectReservation(
            Guid sourceEventId,
            DungeonParticipantEffectAudience audience,
            DungeonParticipantRunIdentity participantIdentity,
            string effectKind,
            Guid leaseId)
        {
            SourceEventId = sourceEventId;
            Audience = audience;
            ParticipantIdentity = participantIdentity;
            EffectKind = effectKind;
            LeaseId = leaseId;
        }

        internal Guid SourceEventId { get; }
        internal DungeonParticipantEffectAudience Audience { get; }
        internal DungeonParticipantRunIdentity ParticipantIdentity { get; }
        internal string EffectKind { get; }
        internal Guid LeaseId { get; }
        internal bool IsValid => SourceEventId != Guid.Empty && LeaseId != Guid.Empty;
    }

    internal sealed class DungeonParticipantEffectWorkItem
    {
        internal DungeonParticipantEffectWorkItem(
            DungeonEventEnvelope source,
            DungeonParticipantRosterEntry participant,
            string effectKind,
            DungeonParticipantEffectState state)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Participant = participant ?? throw new ArgumentNullException(nameof(participant));
            EffectKind = effectKind ?? throw new ArgumentNullException(nameof(effectKind));
            State = state;
        }

        internal DungeonEventEnvelope Source { get; }
        internal DungeonParticipantRosterEntry Participant { get; }
        internal string EffectKind { get; }
        internal DungeonParticipantEffectState State { get; }
    }

    // Instance-scoped recovery journal for effects which are personal but caused
    // by one shared world fact. It is deliberately in-process for now; a durable
    // outbox can persist the same record shape without changing callers.
    public sealed class DungeonParticipantEffectJournal
    {
        private sealed class ParticipantKey : IEquatable<ParticipantKey>
        {
            internal ParticipantKey(
                DungeonParticipantRunIdentity participantIdentity,
                string effectKind)
            {
                ParticipantIdentity = participantIdentity;
                EffectKind = effectKind;
            }

            internal DungeonParticipantRunIdentity ParticipantIdentity { get; }
            internal string EffectKind { get; }

            public bool Equals(ParticipantKey other) =>
                other != null
                && ParticipantIdentity.Equals(other.ParticipantIdentity)
                && string.Equals(EffectKind, other.EffectKind, StringComparison.Ordinal);

            public override bool Equals(object obj) => Equals(obj as ParticipantKey);

            public override int GetHashCode() =>
                HashCode.Combine(ParticipantIdentity, EffectKind);
        }

        private readonly struct EventKey : IEquatable<EventKey>
        {
            internal EventKey(
                Guid sourceEventId,
                DungeonParticipantEffectAudience audience)
            {
                SourceEventId = sourceEventId;
                Audience = audience;
            }

            internal Guid SourceEventId { get; }
            internal DungeonParticipantEffectAudience Audience { get; }

            public bool Equals(EventKey other) =>
                SourceEventId == other.SourceEventId && Audience == other.Audience;

            public override bool Equals(object obj) =>
                obj is EventKey other && Equals(other);

            public override int GetHashCode() =>
                HashCode.Combine(SourceEventId, Audience);
        }

        private sealed class EffectEntry
        {
            internal DungeonParticipantEffectState State;
            internal Guid LeaseId;
        }

        private sealed class EventEntry
        {
            internal EventEntry(
                DungeonEventEnvelope source,
                IReadOnlyList<DungeonParticipantRosterEntry> roster)
            {
                Source = source;
                Roster = roster;
            }

            internal DungeonEventEnvelope Source { get; }
            internal IReadOnlyList<DungeonParticipantRosterEntry> Roster { get; }
            internal Dictionary<ParticipantKey, EffectEntry> Effects { get; } =
                new Dictionary<ParticipantKey, EffectEntry>();
        }

        private readonly object _syncRoot = new object();
        private readonly Dictionary<EventKey, EventEntry> _events =
            new Dictionary<EventKey, EventEntry>();

        internal bool TryFreeze(
            DungeonEventEnvelope source,
            DungeonParticipantEffectAudience audience,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            out IReadOnlyList<DungeonParticipantRosterEntry> frozenRoster)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            var normalized = NormalizeRoster(source, audience, roster);
            lock (_syncRoot)
            {
                var key = new EventKey(source.SourceEventId, audience);
                if (_events.TryGetValue(key, out var existing))
                {
                    frozenRoster = existing.Roster;
                    return false;
                }

                _events.Add(
                    key,
                    new EventEntry(source, normalized));
                frozenRoster = normalized;
                return true;
            }
        }

        internal bool TryGetSource(
            Guid sourceEventId,
            DungeonParticipantEffectAudience audience,
            out DungeonEventEnvelope source)
        {
            lock (_syncRoot)
            {
                if (_events.TryGetValue(
                        new EventKey(sourceEventId, audience),
                        out var entry))
                {
                    source = entry.Source;
                    return true;
                }
            }

            source = null;
            return false;
        }

        internal IReadOnlyList<DungeonParticipantRosterEntry> GetRoster(
            Guid sourceEventId,
            DungeonParticipantEffectAudience audience)
        {
            lock (_syncRoot)
            {
                return _events.TryGetValue(
                        new EventKey(sourceEventId, audience),
                        out var entry)
                    ? entry.Roster
                    : Array.Empty<DungeonParticipantRosterEntry>();
            }
        }

        internal bool TryBegin(
            Guid sourceEventId,
            DungeonParticipantEffectAudience audience,
            DungeonParticipantRosterEntry participant,
            string effectKind,
            out DungeonParticipantEffectReservation reservation,
            out DungeonParticipantEffectState state)
        {
            if (participant == null)
                throw new ArgumentNullException(nameof(participant));
            if (string.IsNullOrWhiteSpace(effectKind))
                throw new ArgumentException("An effect kind is required.", nameof(effectKind));

            lock (_syncRoot)
            {
                if (!_events.TryGetValue(
                        new EventKey(sourceEventId, audience),
                        out var eventEntry))
                {
                    reservation = default;
                    state = DungeonParticipantEffectState.Failed;
                    return false;
                }

                var key = new ParticipantKey(
                    participant.RunIdentity.ParticipantIdentity,
                    effectKind);
                if (eventEntry.Effects.TryGetValue(key, out var existing)
                    && (existing.State == DungeonParticipantEffectState.InFlight
                        || existing.State == DungeonParticipantEffectState.Committed))
                {
                    reservation = default;
                    state = existing.State;
                    return false;
                }

                var leaseId = Guid.NewGuid();
                eventEntry.Effects[key] = new EffectEntry
                {
                    State = DungeonParticipantEffectState.InFlight,
                    LeaseId = leaseId,
                };
                reservation = new DungeonParticipantEffectReservation(
                    sourceEventId,
                    audience,
                    participant.RunIdentity.ParticipantIdentity,
                    effectKind,
                    leaseId);
                state = DungeonParticipantEffectState.InFlight;
                return true;
            }
        }

        internal bool TryCommit(DungeonParticipantEffectReservation reservation)
        {
            lock (_syncRoot)
            {
                if (!TryGetEntry(reservation, out var entry))
                    return false;
                entry.State = DungeonParticipantEffectState.Committed;
                entry.LeaseId = Guid.Empty;
                return true;
            }
        }

        internal bool TryFail(DungeonParticipantEffectReservation reservation)
        {
            lock (_syncRoot)
            {
                if (!TryGetEntry(reservation, out var entry))
                    return false;
                entry.State = DungeonParticipantEffectState.Failed;
                entry.LeaseId = Guid.Empty;
                return true;
            }
        }

        internal DungeonParticipantEffectState GetState(
            Guid sourceEventId,
            DungeonParticipantEffectAudience audience,
            DungeonParticipantRunIdentity participantIdentity,
            string effectKind)
        {
            lock (_syncRoot)
            {
                if (!_events.TryGetValue(
                        new EventKey(sourceEventId, audience),
                        out var eventEntry)
                    || !eventEntry.Effects.TryGetValue(
                        new ParticipantKey(participantIdentity, effectKind),
                        out var entry))
                {
                    return DungeonParticipantEffectState.Pending;
                }

                return entry.State;
            }
        }

        internal IReadOnlyList<DungeonParticipantEffectWorkItem> GetRecoverable(
            Guid sourceEventId,
            DungeonParticipantEffectAudience audience,
            string effectKind)
        {
            var result = new List<DungeonParticipantEffectWorkItem>();
            lock (_syncRoot)
            {
                if (!_events.TryGetValue(
                        new EventKey(sourceEventId, audience),
                        out var eventEntry))
                    return result;

                foreach (var participant in eventEntry.Roster)
                {
                    var key = new ParticipantKey(
                        participant.RunIdentity.ParticipantIdentity,
                        effectKind);
                    var state = eventEntry.Effects.TryGetValue(key, out var entry)
                        ? entry.State
                        : DungeonParticipantEffectState.Pending;
                    if (state == DungeonParticipantEffectState.Committed)
                        continue;
                    result.Add(new DungeonParticipantEffectWorkItem(
                        eventEntry.Source,
                        participant,
                        effectKind,
                        state));
                }
            }
            return result;
        }

        internal IReadOnlyList<DungeonParticipantEffectWorkItem>
            GetRecoverableForParticipant(
                DungeonParticipantRunIdentity participantIdentity,
                DungeonParticipantEffectAudience audience,
                string effectKind)
        {
            var result = new List<DungeonParticipantEffectWorkItem>();
            if (!participantIdentity.IsValid || string.IsNullOrWhiteSpace(effectKind))
                return result;

            lock (_syncRoot)
            {
                foreach (var pair in _events)
                {
                    if (pair.Key.Audience != audience)
                        continue;
                    var eventEntry = pair.Value;
                    DungeonParticipantRosterEntry participant = null;
                    foreach (var candidate in eventEntry.Roster)
                    {
                        if (candidate.RunIdentity.ParticipantIdentity.Equals(
                                participantIdentity))
                        {
                            participant = candidate;
                            break;
                        }
                    }
                    if (participant == null)
                        continue;

                    var key = new ParticipantKey(participantIdentity, effectKind);
                    var state = eventEntry.Effects.TryGetValue(key, out var entry)
                        ? entry.State
                        : DungeonParticipantEffectState.Pending;
                    if (state == DungeonParticipantEffectState.Committed
                        || state == DungeonParticipantEffectState.InFlight)
                    {
                        continue;
                    }

                    result.Add(new DungeonParticipantEffectWorkItem(
                        eventEntry.Source,
                        participant,
                        effectKind,
                        state));
                }
            }
            return result;
        }

        private bool TryGetEntry(
            DungeonParticipantEffectReservation reservation,
            out EffectEntry entry)
        {
            entry = null;
            if (!reservation.IsValid
                || !_events.TryGetValue(
                    new EventKey(reservation.SourceEventId, reservation.Audience),
                    out var eventEntry))
            {
                return false;
            }

            return eventEntry.Effects.TryGetValue(
                       new ParticipantKey(
                           reservation.ParticipantIdentity,
                           reservation.EffectKind),
                       out entry)
                && entry.State == DungeonParticipantEffectState.InFlight
                && entry.LeaseId == reservation.LeaseId;
        }

        private static IReadOnlyList<DungeonParticipantRosterEntry> NormalizeRoster(
            DungeonEventEnvelope source,
            DungeonParticipantEffectAudience audience,
            IReadOnlyList<DungeonParticipantRosterEntry> roster)
        {
            var result = new List<DungeonParticipantRosterEntry>();
            var seen = new HashSet<DungeonParticipantRunIdentity>();
            if (roster != null)
            {
                foreach (var participant in roster)
                {
                    if (participant == null
                        || !participant.RunIdentity.InstanceIdentity.Equals(source.InstanceIdentity)
                        || !participant.RoomIdentity.Instance.Equals(source.InstanceIdentity)
                        || (audience == DungeonParticipantEffectAudience.Room
                            && !participant.RoomIdentity.Equals(source.RoomIdentity))
                        || !seen.Add(participant.RunIdentity.ParticipantIdentity))
                    {
                        continue;
                    }
                    result.Add(participant);
                }
            }

            if (result.Count == 0 && source.AffectedPlayerId.HasValue)
            {
                // Callers should normally pass the registry snapshot. This fallback
                // keeps a single-player/test run recoverable when no registry exists.
                return result;
            }

            return result.AsReadOnly();
        }
    }
}
