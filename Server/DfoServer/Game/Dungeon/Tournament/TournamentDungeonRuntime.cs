using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.Game.Dungeon.Tournament
{
    internal sealed class TournamentActorSnapshot
    {
        internal TournamentActorSnapshot(
            int code,
            int strength,
            string name,
            byte level,
            byte actorType)
        {
            Code = code;
            Strength = strength;
            Name = name ?? string.Empty;
            Level = level;
            ActorType = actorType;
        }

        internal int Code { get; }
        internal int Strength { get; }
        internal string Name { get; }
        internal byte Level { get; }
        internal byte ActorType { get; }
    }

    internal sealed class TournamentTeamSnapshot
    {
        internal TournamentTeamSnapshot(
            byte position,
            IReadOnlyList<TournamentActorSnapshot> members,
            bool isPlayer)
        {
            Position = position;
            Members = Freeze(members);
            IsPlayer = isPlayer;

            long strength = 0;
            foreach (var member in Members)
                strength += Math.Max(0, member.Strength);
            TotalStrength = strength > int.MaxValue
                ? int.MaxValue
                : (int)strength;
        }

        internal byte Position { get; }
        internal IReadOnlyList<TournamentActorSnapshot> Members { get; }
        internal bool IsPlayer { get; }
        internal int TotalStrength { get; }

        private static IReadOnlyList<TournamentActorSnapshot> Freeze(
            IReadOnlyList<TournamentActorSnapshot> source)
        {
            var copy = new TournamentActorSnapshot[source?.Count ?? 0];
            for (var index = 0; index < copy.Length; index++)
                copy[index] = source[index];
            return new ReadOnlyCollection<TournamentActorSnapshot>(copy);
        }
    }

    internal sealed class TournamentRoundSnapshot
    {
        internal TournamentRoundSnapshot(
            byte number,
            IReadOnlyList<TournamentTeamSnapshot> teams)
        {
            Number = number;
            var copy = new TournamentTeamSnapshot[teams?.Count ?? 0];
            for (var index = 0; index < copy.Length; index++)
                copy[index] = teams[index];
            Teams = new ReadOnlyCollection<TournamentTeamSnapshot>(copy);
        }

        internal byte Number { get; }
        internal IReadOnlyList<TournamentTeamSnapshot> Teams { get; }
    }

    internal enum TournamentActorDeathDisposition
    {
        Rejected = 0,
        Duplicate = 1,
        ActorAccepted = 2,
        RoundCompleted = 3,
        TournamentCompleted = 4,
    }

    internal enum TournamentTerminalResult : byte
    {
        Active = 0,
        Eliminated = 1,
        Champion = 2,
    }

    internal enum TournamentEliminationDisposition : byte
    {
        Rejected = 0,
        Duplicate = 1,
        Eliminated = 2,
    }

    internal readonly struct TournamentEliminationTransition
    {
        internal TournamentEliminationTransition(
            TournamentEliminationDisposition disposition,
            byte completedRounds)
        {
            Disposition = disposition;
            CompletedRounds = completedRounds;
        }

        internal TournamentEliminationDisposition Disposition { get; }
        internal byte CompletedRounds { get; }
        internal bool Accepted =>
            Disposition == TournamentEliminationDisposition.Eliminated;
        internal bool Handled =>
            Disposition != TournamentEliminationDisposition.Rejected;
    }

    internal readonly struct TournamentActorDeathTransition
    {
        internal TournamentActorDeathTransition(
            TournamentActorDeathDisposition disposition,
            byte completedRound,
            byte currentRound)
        {
            Disposition = disposition;
            CompletedRound = completedRound;
            CurrentRound = currentRound;
        }

        internal TournamentActorDeathDisposition Disposition { get; }
        internal byte CompletedRound { get; }
        internal byte CurrentRound { get; }
        internal bool Accepted =>
            Disposition >= TournamentActorDeathDisposition.ActorAccepted;
        internal bool RoundCompleted =>
            Disposition == TournamentActorDeathDisposition.RoundCompleted
            || Disposition == TournamentActorDeathDisposition.TournamentCompleted;
        internal bool TournamentCompleted =>
            Disposition == TournamentActorDeathDisposition.TournamentCompleted;
    }

    internal sealed class TournamentDungeonRuntime
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<Guid, ushort> _acceptedEvents =
            new Dictionary<Guid, ushort>();
        private readonly HashSet<ushort> _acceptedSequences = new HashSet<ushort>();
        private ushort _firstActorSequence;
        private byte _currentRound = 1;
        private byte _completedRounds;
        private TournamentTerminalResult _terminalResult;

        internal TournamentDungeonRuntime(
            TournamentDungeonDefinition definition,
            IReadOnlyList<TournamentRoundSnapshot> rounds,
            IReadOnlyList<GameWorld.Dungeon.MonsterSumInfo> pathActors)
        {
            Definition = definition
                ?? throw new ArgumentNullException(nameof(definition));
            Rounds = Freeze(rounds);
            PathActors = Freeze(pathActors);
        }

        internal TournamentDungeonDefinition Definition { get; }
        internal IReadOnlyList<TournamentRoundSnapshot> Rounds { get; }
        internal IReadOnlyList<GameWorld.Dungeon.MonsterSumInfo> PathActors { get; }
        internal ushort FirstActorSequence
        {
            get { lock (_syncRoot) return _firstActorSequence; }
        }
        internal byte CurrentRound
        {
            get { lock (_syncRoot) return _currentRound; }
        }
        internal bool IsComplete
        {
            get
            {
                lock (_syncRoot)
                    return _terminalResult == TournamentTerminalResult.Champion;
            }
        }
        internal bool IsTerminated
        {
            get
            {
                lock (_syncRoot)
                    return _terminalResult != TournamentTerminalResult.Active;
            }
        }
        internal bool IsChampion
        {
            get
            {
                lock (_syncRoot)
                    return _terminalResult == TournamentTerminalResult.Champion;
            }
        }
        internal byte CompletedRounds
        {
            get { lock (_syncRoot) return _completedRounds; }
        }
        internal TournamentTerminalResult TerminalResult
        {
            get { lock (_syncRoot) return _terminalResult; }
        }

        internal bool TryBindFirstActorSequence(ushort firstActorSequence)
        {
            if (firstActorSequence == 0)
                return false;

            lock (_syncRoot)
            {
                if (_firstActorSequence == 0)
                {
                    var finalSequence = (long)firstActorSequence
                        + PathActors.Count - 1L;
                    if (finalSequence > ushort.MaxValue)
                        return false;
                    _firstActorSequence = firstActorSequence;
                    return true;
                }

                return _firstActorSequence == firstActorSequence;
            }
        }

        internal bool CanAcceptActorDeath(Guid sourceEventId, ushort sequenceId)
        {
            if (sourceEventId == Guid.Empty)
                return false;

            lock (_syncRoot)
            {
                if (_firstActorSequence == 0)
                    return false;
                if (_acceptedEvents.TryGetValue(sourceEventId, out var acceptedSequence))
                    return acceptedSequence == sequenceId;
                if (_acceptedSequences.Contains(sequenceId))
                    return true;
                if (_terminalResult != TournamentTerminalResult.Active)
                    return false;

                var first = _firstActorSequence
                    + (_currentRound - 1) * Definition.PartyLimit;
                var endExclusive = first + Definition.PartyLimit;
                return sequenceId >= first && sequenceId < endExclusive;
            }
        }

        internal TournamentActorDeathTransition TryApplyActorDeath(
            DungeonActorDeathFact death)
        {
            if (death == null)
                return default;

            lock (_syncRoot)
            {
                if (_firstActorSequence == 0)
                    return default;
                if (_acceptedEvents.TryGetValue(
                        death.SourceEventId,
                        out var acceptedSequence))
                {
                    return new TournamentActorDeathTransition(
                        acceptedSequence == death.SequenceId
                            ? TournamentActorDeathDisposition.Duplicate
                            : TournamentActorDeathDisposition.Rejected,
                        completedRound: 0,
                        currentRound: _currentRound);
                }
                if (_acceptedSequences.Contains(death.SequenceId))
                {
                    return new TournamentActorDeathTransition(
                        TournamentActorDeathDisposition.Duplicate,
                        completedRound: 0,
                        currentRound: _currentRound);
                }
                if (_terminalResult != TournamentTerminalResult.Active)
                    return default;

                var first = _firstActorSequence
                    + (_currentRound - 1) * Definition.PartyLimit;
                var endExclusive = first + Definition.PartyLimit;
                if (death.SequenceId < first || death.SequenceId >= endExclusive)
                    return default;

                _acceptedEvents.Add(death.SourceEventId, death.SequenceId);
                _acceptedSequences.Add(death.SequenceId);
                for (var sequence = first; sequence < endExclusive; sequence++)
                {
                    if (!_acceptedSequences.Contains((ushort)sequence))
                    {
                        return new TournamentActorDeathTransition(
                            TournamentActorDeathDisposition.ActorAccepted,
                            completedRound: 0,
                            currentRound: _currentRound);
                    }
                }

                var completedRound = _currentRound;
                if (_currentRound == 4)
                {
                    _completedRounds = 4;
                    _terminalResult = TournamentTerminalResult.Champion;
                    return new TournamentActorDeathTransition(
                        TournamentActorDeathDisposition.TournamentCompleted,
                        completedRound,
                        currentRound: _currentRound);
                }

                _currentRound++;
                return new TournamentActorDeathTransition(
                    TournamentActorDeathDisposition.RoundCompleted,
                    completedRound,
                    currentRound: _currentRound);
            }
        }

        internal TournamentEliminationTransition TryEliminate()
        {
            lock (_syncRoot)
            {
                if (_terminalResult == TournamentTerminalResult.Eliminated)
                {
                    return new TournamentEliminationTransition(
                        TournamentEliminationDisposition.Duplicate,
                        _completedRounds);
                }
                if (_terminalResult != TournamentTerminalResult.Active)
                {
                    return new TournamentEliminationTransition(
                        TournamentEliminationDisposition.Rejected,
                        _completedRounds);
                }

                _completedRounds = (byte)Math.Max(0, _currentRound - 1);
                _terminalResult = TournamentTerminalResult.Eliminated;
                return new TournamentEliminationTransition(
                    TournamentEliminationDisposition.Eliminated,
                    _completedRounds);
            }
        }

        private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source)
        {
            var copy = new T[source?.Count ?? 0];
            for (var index = 0; index < copy.Length; index++)
                copy[index] = source[index];
            return new ReadOnlyCollection<T>(copy);
        }
    }

    internal static class TournamentDungeonRuntimeFactory
    {
        internal static bool TryCreate(
            TournamentDungeonDefinition definition,
            int partyCount,
            Func<int, int> next,
            out TournamentDungeonRuntime runtime,
            out string failureReason)
        {
            runtime = null;
            failureReason = string.Empty;
            if (definition == null)
            {
                failureReason = "tournament definition is missing";
                return false;
            }
            if (partyCount != definition.PartyLimit)
            {
                failureReason =
                    $"party count {partyCount} does not match configured limit " +
                    definition.PartyLimit;
                return false;
            }
            if (next == null)
            {
                failureReason = "random source is missing";
                return false;
            }

            var candidates = new List<TournamentActorSnapshot>();
            foreach (var candidate in definition.Candidates)
            {
                if (candidate.PartyCount != partyCount)
                    continue;
                candidates.Add(new TournamentActorSnapshot(
                    candidate.Code,
                    candidate.Strength,
                    candidate.Name,
                    candidate.Level,
                    candidate.ActorType));
            }

            var requiredActors = 15 * partyCount;
            if (candidates.Count < requiredActors)
            {
                failureReason =
                    $"candidate pool has {candidates.Count}, requires {requiredActors}";
                return false;
            }

            Shuffle(candidates, next);
            var npcTeams = new List<TournamentTeamSnapshot>(15);
            for (var teamIndex = 0; teamIndex < 15; teamIndex++)
            {
                var members = new TournamentActorSnapshot[partyCount];
                for (var memberIndex = 0; memberIndex < partyCount; memberIndex++)
                {
                    members[memberIndex] =
                        candidates[teamIndex * partyCount + memberIndex];
                }
                npcTeams.Add(new TournamentTeamSnapshot(
                    position: 0,
                    members,
                    isPlayer: false));
            }
            npcTeams.Sort((left, right) =>
                left.TotalStrength.CompareTo(right.TotalStrength));

            var playerMembers = new TournamentActorSnapshot[partyCount];
            for (var index = 0; index < playerMembers.Length; index++)
            {
                playerMembers[index] = new TournamentActorSnapshot(
                    code: 0,
                    strength: 0,
                    name: string.Empty,
                    level: 0,
                    actorType: 0);
            }

            var playerPosition = Next(next, 16);
            var opponentPosition = playerPosition ^ 1;
            var firstRound = new TournamentTeamSnapshot[16];
            firstRound[playerPosition] = new TournamentTeamSnapshot(
                (byte)playerPosition,
                playerMembers,
                isPlayer: true);
            firstRound[opponentPosition] = WithPosition(
                npcTeams[0],
                opponentPosition);

            var remainingTeams = npcTeams.GetRange(1, npcTeams.Count - 1);
            Shuffle(remainingTeams, next);
            var remainingIndex = 0;
            for (var position = 0; position < firstRound.Length; position++)
            {
                if (firstRound[position] == null)
                {
                    firstRound[position] = WithPosition(
                        remainingTeams[remainingIndex++],
                        position);
                }
            }

            var rounds = new List<TournamentRoundSnapshot>(4)
            {
                new TournamentRoundSnapshot(1, firstRound),
            };
            IReadOnlyList<TournamentTeamSnapshot> previous = firstRound;
            for (byte round = 2; round <= 4; round++)
            {
                var nextRound = new TournamentTeamSnapshot[previous.Count / 2];
                for (var match = 0; match < nextRound.Length; match++)
                {
                    var left = previous[match * 2];
                    var right = previous[match * 2 + 1];
                    var winner = left.IsPlayer
                        ? left
                        : right.IsPlayer
                            ? right
                            : PickNpcWinner(left, right, next);
                    nextRound[match] = new TournamentTeamSnapshot(
                        (byte)match,
                        winner.Members,
                        winner.IsPlayer);
                }

                rounds.Add(new TournamentRoundSnapshot(round, nextRound));
                previous = nextRound;
            }

            var pathActors = new List<GameWorld.Dungeon.MonsterSumInfo>(
                4 * partyCount);
            foreach (var round in rounds)
            {
                var playerIndex = FindPlayerIndex(round.Teams);
                if (playerIndex < 0)
                {
                    failureReason =
                        $"round {round.Number} does not contain the player team";
                    return false;
                }

                foreach (var actor in round.Teams[playerIndex ^ 1].Members)
                {
                    var actorType = actor.ActorType;
                    if (round.Number == 4)
                        actorType = actorType >= 5 ? (byte)8 : (byte)3;
                    pathActors.Add(new GameWorld.Dungeon.MonsterSumInfo
                    {
                        Code = actor.Code,
                        Level = actor.Level,
                        Type = actorType,
                        IsBlocking = true,
                    });
                }
            }

            runtime = new TournamentDungeonRuntime(
                definition,
                rounds,
                pathActors);
            return true;
        }

        private static TournamentTeamSnapshot WithPosition(
            TournamentTeamSnapshot source,
            int position)
            => new TournamentTeamSnapshot(
                (byte)position,
                source.Members,
                source.IsPlayer);

        private static TournamentTeamSnapshot PickNpcWinner(
            TournamentTeamSnapshot left,
            TournamentTeamSnapshot right,
            Func<int, int> next)
        {
            if (left.TotalStrength == right.TotalStrength)
                return Next(next, 2) == 0 ? left : right;
            return left.TotalStrength > right.TotalStrength ? left : right;
        }

        private static int FindPlayerIndex(
            IReadOnlyList<TournamentTeamSnapshot> teams)
        {
            for (var index = 0; index < teams.Count; index++)
            {
                if (teams[index].IsPlayer)
                    return index;
            }
            return -1;
        }

        private static void Shuffle<T>(IList<T> values, Func<int, int> next)
        {
            for (var index = values.Count - 1; index > 0; index--)
            {
                var swap = Next(next, index + 1);
                var value = values[index];
                values[index] = values[swap];
                values[swap] = value;
            }
        }

        private static int Next(Func<int, int> next, int exclusiveMaximum)
        {
            var value = next(exclusiveMaximum);
            if (value < 0 || value >= exclusiveMaximum)
            {
                throw new InvalidOperationException(
                    "Tournament random source returned an out-of-range value.");
            }
            return value;
        }
    }
}
