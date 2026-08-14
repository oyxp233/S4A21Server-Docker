using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonEncounterDirectiveKind
    {
        Start = 0,
        Succeed = 1,
        Fail = 2,
    }

    internal enum DungeonEncounterApplyStatus
    {
        Applied = 0,
        Replayed = 1,
        NoOp = 2,
        RejectedIdentity = 3,
        RejectedRoom = 4,
        InvalidTransition = 5,
    }

    internal sealed class DungeonEncounterDirective
    {
        internal const string DefaultEncounterKey = "room";

        internal DungeonEncounterDirective(
            DungeonEventEnvelope source,
            DungeonEncounterDirectiveKind kind,
            string encounterKey = null,
            string cause = null)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Kind = kind;
            EncounterKey = string.IsNullOrWhiteSpace(encounterKey)
                ? DefaultEncounterKey
                : encounterKey.Trim();
            Cause = cause ?? source.Cause ?? string.Empty;
        }

        internal DungeonEventEnvelope Source { get; }
        internal DungeonEncounterDirectiveKind Kind { get; }
        internal string EncounterKey { get; }
        internal string Cause { get; }
    }

    internal sealed class DungeonEncounterTransition
    {
        internal DungeonEncounterTransition(
            DungeonEncounterDirective directive,
            DungeonEncounterApplyStatus status,
            DungeonEncounterState before,
            DungeonEncounterState after)
        {
            Directive = directive;
            Status = status;
            Before = before;
            After = after;
        }

        internal DungeonEncounterDirective Directive { get; }
        internal DungeonEncounterApplyStatus Status { get; }
        internal DungeonEncounterState Before { get; }
        internal DungeonEncounterState After { get; }
        internal bool Applied => Status == DungeonEncounterApplyStatus.Applied;
    }

    internal sealed class DungeonEncounterRuntime
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<
            (Guid SourceEventId, DungeonEncounterDirectiveKind Kind),
            (DungeonEncounterState Before, DungeonEncounterState After)>
            _appliedEvents = new Dictionary<
                (Guid, DungeonEncounterDirectiveKind),
                (DungeonEncounterState, DungeonEncounterState)>();
        private DungeonEncounterState _state =
            DungeonEncounterState.NotStarted;

        internal DungeonEncounterState State
        {
            get
            {
                lock (_syncRoot)
                    return _state;
            }
        }

        internal DungeonEncounterTransition Apply(
            DungeonEncounterDirective directive)
        {
            if (directive == null)
                throw new ArgumentNullException(nameof(directive));

            lock (_syncRoot)
            {
                var eventKey = (directive.Source.SourceEventId, directive.Kind);
                if (_appliedEvents.TryGetValue(eventKey, out var replayed))
                {
                    return new DungeonEncounterTransition(
                        directive,
                        DungeonEncounterApplyStatus.Replayed,
                        replayed.Before,
                        replayed.After);
                }

                var before = _state;
                var status = ApplyCore(directive.Kind);
                var after = _state;
                _appliedEvents[eventKey] = (before, after);
                return new DungeonEncounterTransition(
                    directive,
                    status,
                    before,
                    after);
            }
        }

        internal bool TryApplyLegacy(DungeonEncounterDirectiveKind kind)
        {
            lock (_syncRoot)
                return ApplyCore(kind) == DungeonEncounterApplyStatus.Applied;
        }

        private DungeonEncounterApplyStatus ApplyCore(
            DungeonEncounterDirectiveKind kind)
        {
            switch (kind)
            {
                case DungeonEncounterDirectiveKind.Start:
                    if (_state == DungeonEncounterState.NotStarted)
                    {
                        _state = DungeonEncounterState.Active;
                        return DungeonEncounterApplyStatus.Applied;
                    }
                    return _state == DungeonEncounterState.Active
                        ? DungeonEncounterApplyStatus.NoOp
                        : DungeonEncounterApplyStatus.InvalidTransition;

                case DungeonEncounterDirectiveKind.Succeed:
                    if (_state == DungeonEncounterState.Active)
                    {
                        _state = DungeonEncounterState.Succeeded;
                        return DungeonEncounterApplyStatus.Applied;
                    }
                    return _state == DungeonEncounterState.Succeeded
                        ? DungeonEncounterApplyStatus.NoOp
                        : DungeonEncounterApplyStatus.InvalidTransition;

                case DungeonEncounterDirectiveKind.Fail:
                    if (_state == DungeonEncounterState.Active)
                    {
                        _state = DungeonEncounterState.Failed;
                        return DungeonEncounterApplyStatus.Applied;
                    }
                    return _state == DungeonEncounterState.Failed
                        ? DungeonEncounterApplyStatus.NoOp
                        : DungeonEncounterApplyStatus.InvalidTransition;

                default:
                    return DungeonEncounterApplyStatus.InvalidTransition;
            }
        }
    }

    internal static class DungeonEncounterApplicationService
    {
        internal static DungeonEncounterTransition Apply(
            DungeonRun run,
            DungeonEncounterDirective directive)
        {
            if (run == null)
                throw new ArgumentNullException(nameof(run));
            if (directive == null)
                throw new ArgumentNullException(nameof(directive));

            DungeonEncounterTransition transition;
            lock (run.SyncRoot)
            {
                var source = directive.Source;
                if (!run.Matches(source.RunIdentity))
                {
                    transition = Rejected(
                        directive,
                        DungeonEncounterApplyStatus.RejectedIdentity);
                }
                else if (!source.RoomInstanceId.HasValue
                    || source.RoomInstanceId.Value <= 0
                    || run.CurrentRoomInstanceId
                        != source.RoomInstanceId.Value
                    || !run.Instance.TryGetRoom(
                        source.RoomInstanceId.Value,
                        out var room))
                {
                    transition = Rejected(
                        directive,
                        DungeonEncounterApplyStatus.RejectedRoom);
                }
                else
                {
                    transition = room.ApplyEncounterDirective(directive);
                }
            }

            run.Instance?.Diagnostics.RecordEncounter(transition);
            return transition;
        }

        private static DungeonEncounterTransition Rejected(
            DungeonEncounterDirective directive,
            DungeonEncounterApplyStatus status)
        {
            return new DungeonEncounterTransition(
                directive,
                status,
                DungeonEncounterState.NotStarted,
                DungeonEncounterState.NotStarted);
        }
    }
}
