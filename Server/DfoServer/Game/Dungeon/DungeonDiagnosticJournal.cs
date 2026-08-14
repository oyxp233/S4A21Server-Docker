using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonDiagnosticRecordKind
    {
        EncounterDirective = 0,
        ClearIntent = 1,
        ClearCommit = 2,
    }

    internal sealed class DungeonDiagnosticRecord
    {
        internal long Sequence { get; init; }
        internal DungeonDiagnosticRecordKind Kind { get; init; }
        internal Guid SourceEventId { get; init; }
        internal DungeonRunIdentity RunIdentity { get; init; }
        internal long? RoomInstanceId { get; init; }
        internal long OccurredTick { get; init; }
        internal string Name { get; init; }
        internal string Outcome { get; init; }
        internal string Detail { get; init; }
        internal string EncounterKey { get; init; }
        internal DungeonEncounterDirectiveKind? EncounterDirective { get; init; }
        internal DungeonEncounterApplyStatus? EncounterStatus { get; init; }
        internal DungeonEncounterState? EncounterBefore { get; init; }
        internal DungeonEncounterState? EncounterAfter { get; init; }
    }

    internal sealed class DungeonEncounterReplayResult
    {
        internal DungeonEncounterState FinalState { get; init; }
        internal int AppliedRecordCount { get; init; }
        internal int RejectedRecordCount { get; init; }
        internal int DivergenceCount { get; init; }
        internal long TruncatedRecordCount { get; init; }
        internal bool IsComplete => TruncatedRecordCount == 0;
        internal bool IsConsistent => DivergenceCount == 0;
    }

    internal sealed class DungeonDiagnosticJournal
    {
        private const int DefaultCapacity = 512;
        private readonly object _syncRoot = new object();
        private readonly Queue<DungeonDiagnosticRecord> _records;
        private readonly int _capacity;
        private long _nextSequence;
        private long _truncatedRecordCount;

        internal DungeonDiagnosticJournal(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _records = new Queue<DungeonDiagnosticRecord>(capacity);
        }

        internal long TruncatedRecordCount
        {
            get
            {
                lock (_syncRoot)
                    return _truncatedRecordCount;
            }
        }

        internal void RecordEncounter(DungeonEncounterTransition transition)
        {
            if (transition?.Directive == null)
                return;

            var directive = transition.Directive;
            Append(new DungeonDiagnosticRecord
            {
                Kind = DungeonDiagnosticRecordKind.EncounterDirective,
                SourceEventId = directive.Source.SourceEventId,
                RunIdentity = directive.Source.RunIdentity,
                RoomInstanceId = directive.Source.RoomInstanceId,
                OccurredTick = directive.Source.OccurredTick,
                Name = "encounter:" + directive.EncounterKey,
                Outcome = transition.Status.ToString(),
                Detail = directive.Cause,
                EncounterKey = directive.EncounterKey,
                EncounterDirective = directive.Kind,
                EncounterStatus = transition.Status,
                EncounterBefore = transition.Before,
                EncounterAfter = transition.After,
            });
        }

        internal void Record(
            DungeonDiagnosticRecordKind kind,
            DungeonEventEnvelope source,
            string name,
            string outcome,
            string detail = null)
        {
            if (source == null)
                return;

            Append(new DungeonDiagnosticRecord
            {
                Kind = kind,
                SourceEventId = source.SourceEventId,
                RunIdentity = source.RunIdentity,
                RoomInstanceId = source.RoomInstanceId,
                OccurredTick = source.OccurredTick,
                Name = name ?? string.Empty,
                Outcome = outcome ?? string.Empty,
                Detail = detail ?? string.Empty,
            });
        }

        internal IReadOnlyList<DungeonDiagnosticRecord> Snapshot()
        {
            lock (_syncRoot)
                return new ReadOnlyCollection<DungeonDiagnosticRecord>(
                    _records.ToArray());
        }

        internal DungeonEncounterReplayResult ReplayEncounter(
            long roomInstanceId,
            string encounterKey = DungeonEncounterDirective.DefaultEncounterKey)
        {
            DungeonDiagnosticRecord[] snapshot;
            long truncated;
            lock (_syncRoot)
            {
                snapshot = _records.ToArray();
                truncated = _truncatedRecordCount;
            }

            var runtime = new DungeonEncounterRuntime();
            var count = 0;
            var rejected = 0;
            var divergences = 0;
            foreach (var record in snapshot)
            {
                if (record.Kind != DungeonDiagnosticRecordKind.EncounterDirective
                    || record.RoomInstanceId != roomInstanceId
                    || !string.Equals(
                        record.EncounterKey,
                        encounterKey,
                        StringComparison.Ordinal)
                    || !record.EncounterDirective.HasValue)
                {
                    continue;
                }

                if (record.EncounterStatus
                    == DungeonEncounterApplyStatus.RejectedIdentity
                    || record.EncounterStatus
                    == DungeonEncounterApplyStatus.RejectedRoom)
                {
                    rejected++;
                    continue;
                }

                var replaySource = new DungeonEventEnvelope(
                    record.SourceEventId,
                    record.RunIdentity,
                    record.RoomInstanceId,
                    sourcePlayerId: 0,
                    affectedPlayerId: null,
                    sourceActorId: null,
                    sourceActorCode: null,
                    record.Detail,
                    record.OccurredTick);
                var replayed = runtime.Apply(
                    new DungeonEncounterDirective(
                        replaySource,
                        record.EncounterDirective.Value,
                        record.EncounterKey,
                        record.Detail));
                count++;
                if (replayed.Status != record.EncounterStatus
                    || replayed.Before != record.EncounterBefore
                    || replayed.After != record.EncounterAfter)
                {
                    divergences++;
                }
            }

            return new DungeonEncounterReplayResult
            {
                FinalState = runtime.State,
                AppliedRecordCount = count,
                RejectedRecordCount = rejected,
                DivergenceCount = divergences,
                TruncatedRecordCount = truncated,
            };
        }

        private void Append(DungeonDiagnosticRecord record)
        {
            lock (_syncRoot)
            {
                while (_records.Count >= _capacity)
                {
                    _records.Dequeue();
                    _truncatedRecordCount++;
                }

                _records.Enqueue(new DungeonDiagnosticRecord
                {
                    Sequence = ++_nextSequence,
                    Kind = record.Kind,
                    SourceEventId = record.SourceEventId,
                    RunIdentity = record.RunIdentity,
                    RoomInstanceId = record.RoomInstanceId,
                    OccurredTick = record.OccurredTick,
                    Name = record.Name,
                    Outcome = record.Outcome,
                    Detail = record.Detail,
                    EncounterKey = record.EncounterKey,
                    EncounterDirective = record.EncounterDirective,
                    EncounterStatus = record.EncounterStatus,
                    EncounterBefore = record.EncounterBefore,
                    EncounterAfter = record.EncounterAfter,
                });
            }
        }
    }
}
