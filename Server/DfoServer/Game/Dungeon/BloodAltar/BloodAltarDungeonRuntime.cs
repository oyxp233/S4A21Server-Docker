using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DfoServer.Game.Dungeon.BloodAltar
{
    internal enum BloodAltarProgress
    {
        None,
        ReadyForNextRound,
        AwaitingUltimateDifficulty,
        ReadyForFinalRound,
        MapComplete,
        DungeonComplete,
    }

    internal sealed class BloodAltarScheduledWave
    {
        internal int DelayMilliseconds { get; set; }
        internal int RoundIndex { get; set; }
        internal int RoundNumber { get; set; }
        internal int PhaseIndex { get; set; }
        internal int BatchIndex { get; set; }
        internal int SpawnCount { get; set; }
        internal bool IsFinalMapWave { get; set; }
        internal BloodAltarPhaseDefinition Phase { get; set; }
        internal BloodAltarMonsterDefinition Monster { get; set; }
    }

    internal sealed class BloodAltarRoundSchedule
    {
        internal long Generation { get; set; }
        internal int RoundIndex { get; set; }
        internal int RoundNumber { get; set; }
        internal byte Difficulty { get; set; }
        internal int InitialIntervalMilliseconds { get; set; }
        internal DateTime StartedUtc { get; set; }
        internal IReadOnlyList<BloodAltarScheduledWave> Waves { get; set; }
            = Array.Empty<BloodAltarScheduledWave>();
    }

    internal sealed class BloodAltarSpawn
    {
        internal byte Variant { get; set; }
        internal ushort SequenceId { get; set; }
        internal int MonsterCode { get; set; }
        internal byte MonsterType { get; set; }
        internal byte Level { get; set; }
        internal ushort Scale { get; set; }
        internal ushort X { get; set; }
        internal ushort Y { get; set; }
        internal ushort Z { get; set; }
        internal long ProviderGeneration { get; set; }
        internal int WaveIdentity { get; set; }
    }

    internal sealed class BloodAltarWave
    {
        internal IReadOnlyList<BloodAltarSpawn> Monsters { get; set; }
            = Array.Empty<BloodAltarSpawn>();
        internal short TailValue { get; set; }
        internal int RoundNumber { get; set; }
        internal int PhaseIndex { get; set; }
        internal int BatchIndex { get; set; }
        internal long ProviderGeneration { get; set; }
        internal int WaveIdentity { get; set; }
    }

    internal readonly struct BloodAltarWaveReservation
    {
        internal BloodAltarWaveReservation(
            Guid reservationId,
            long scheduleGeneration,
            int waveIndex)
        {
            ReservationId = reservationId;
            ScheduleGeneration = scheduleGeneration;
            WaveIndex = waveIndex;
        }

        internal Guid ReservationId { get; }
        internal long ScheduleGeneration { get; }
        internal int WaveIndex { get; }
        internal bool IsValid => ReservationId != Guid.Empty;
    }

    internal sealed class BloodAltarDungeonRuntime
    {
        internal const string DynamicActorProvider = "blood-altar";
        private const byte DefaultMonsterVariant = 19;
        private const int MaxConcurrentPhases = 10;

        private readonly object _syncRoot = new object();
        private readonly HashSet<ushort> _activeSequences =
            new HashSet<ushort>();
        private readonly HashSet<ushort> _finalMapWaveSequences =
            new HashSet<ushort>();
        private readonly Dictionary<ushort, BloodAltarSpawn> _activeSpawns =
            new Dictionary<ushort, BloodAltarSpawn>();
        private readonly HashSet<long> _completedRoomIds = new HashSet<long>();
        private readonly List<byte> _completedUltimateDifficulties =
            new List<byte>();

        private BloodAltarMapDefinition _map;
        private DungeonRoomIdentity _roomIdentity;
        private long _generation;
        private int _nextRoundIndex;
        private bool _roundInProgress;
        private bool _roundScheduling;
        private bool _awaitingDifficulty;
        private byte _pendingDifficulty;
        private int _difficultyPromptVersion;
        private int _nextSequence = 1;
        private int _nextWaveIdentity = 1;
        private BloodAltarRoundSchedule _schedule;
        private int _nextWaveIndex;
        private Guid _pendingWaveReservationId;
        private BloodAltarWave _pendingWave;

        internal BloodAltarDungeonRuntime(BloodAltarDungeonDefinition definition)
        {
            Definition = definition
                ?? throw new ArgumentNullException(nameof(definition));
        }

        internal BloodAltarDungeonDefinition Definition { get; }

        // Progress timers belong to the physical altar instance, not to one
        // participant session. Network detach therefore does not pause waves.
        internal RunTimerRegistry Timers { get; } = new RunTimerRegistry();

        internal int CompletedRounds
        {
            get { lock (_syncRoot) return _completedRounds; }
        }
        private int _completedRounds;

        internal DungeonRoomIdentity CurrentRoomIdentity
        {
            get { lock (_syncRoot) return _roomIdentity; }
        }

        internal int CurrentMapId
        {
            get { lock (_syncRoot) return _map?.MapId ?? 0; }
        }

        internal long Generation
        {
            get { lock (_syncRoot) return _generation; }
        }

        internal bool AwaitingUltimateDifficulty
        {
            get { lock (_syncRoot) return _awaitingDifficulty; }
        }

        internal int DifficultyPromptVersion
        {
            get { lock (_syncRoot) return _difficultyPromptVersion; }
        }

        internal bool BlocksMapMove
        {
            get
            {
                lock (_syncRoot)
                {
                    return _map != null
                        && _roomIdentity.IsValid
                        && !_completedRoomIds.Contains(
                            _roomIdentity.RoomInstanceId);
                }
            }
        }

        internal bool IsDungeonComplete
        {
            get
            {
                lock (_syncRoot)
                    return _completedRounds >= Definition.MaxRounds;
            }
        }

        internal bool TryBindMap(
            BloodAltarMapDefinition map,
            DungeonRoomIdentity roomIdentity,
            out bool changed)
        {
            changed = false;
            if (map == null
                || !roomIdentity.IsValid
                || roomIdentity.Instance.PartyDungeonInstanceId <= 0)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (_roomIdentity.IsValid
                    && _roomIdentity.Equals(roomIdentity)
                    && _map?.MapId == map.MapId)
                {
                    return true;
                }
                if (_map != null
                    && _roomIdentity.IsValid
                    && !_completedRoomIds.Contains(_roomIdentity.RoomInstanceId)
                    && (_roundInProgress
                        || _roundScheduling
                        || _activeSequences.Count > 0))
                {
                    return false;
                }

                _generation = NextGeneration(_generation);
                _map = map;
                _roomIdentity = roomIdentity;
                _nextRoundIndex = 0;
                _roundInProgress = false;
                _roundScheduling = false;
                _awaitingDifficulty = false;
                _pendingDifficulty = 0;
                _difficultyPromptVersion = 0;
                _schedule = null;
                _nextWaveIndex = 0;
                _pendingWaveReservationId = Guid.Empty;
                _pendingWave = null;
                _activeSequences.Clear();
                _finalMapWaveSequences.Clear();
                _activeSpawns.Clear();
                changed = true;
                return true;
            }
        }

        internal bool IsBoundTo(
            DungeonRoomIdentity roomIdentity,
            int mapId)
        {
            lock (_syncRoot)
                return _roomIdentity.Equals(roomIdentity)
                    && _map?.MapId == mapId;
        }

        internal bool IsCurrentMapComplete()
        {
            lock (_syncRoot)
                return _roomIdentity.IsValid
                    && _completedRoomIds.Contains(_roomIdentity.RoomInstanceId);
        }

        internal bool TryBeginNextRound(
            DateTime startedUtc,
            out BloodAltarRoundSchedule schedule)
        {
            schedule = null;
            startedUtc = NormalizeUtc(startedUtc);
            lock (_syncRoot)
            {
                if (_map == null
                    || !_roomIdentity.IsValid
                    || _completedRoomIds.Contains(_roomIdentity.RoomInstanceId)
                    || _roundInProgress
                    || _roundScheduling
                    || _awaitingDifficulty
                    || _activeSequences.Count > 0
                    || _nextRoundIndex >= _map.Rounds.Count)
                {
                    return false;
                }

                var difficulty = (byte)0;
                if (Definition.Kind == BloodAltarDungeonKind.Ultimate)
                {
                    var fixedDifficulty = _nextRoundIndex == 0
                        || _nextRoundIndex == _map.Rounds.Count - 1;
                    difficulty = fixedDifficulty ? (byte)1 : _pendingDifficulty;
                    if (difficulty != 1 && difficulty != 2)
                        return false;
                }

                var phases = _map.Rounds[_nextRoundIndex].Phases
                    .Select((phase, index) => new IndexedPhase(phase, index))
                    .Where(item =>
                        Definition.Kind != BloodAltarDungeonKind.Ultimate
                        || item.Phase.Difficulty == difficulty)
                    .ToArray();
                if (phases.Length == 0)
                    return false;

                var waves = BuildRoundSchedule(
                    phases,
                    _nextRoundIndex,
                    _completedRounds);
                if (waves.Count == 0)
                    return false;

                _generation = NextGeneration(_generation);
                _pendingDifficulty = 0;
                _roundInProgress = true;
                _roundScheduling = true;
                _nextWaveIndex = 0;
                _pendingWaveReservationId = Guid.Empty;
                _pendingWave = null;
                _schedule = new BloodAltarRoundSchedule
                {
                    Generation = _generation,
                    RoundIndex = _nextRoundIndex,
                    RoundNumber = _completedRounds,
                    Difficulty = difficulty,
                    InitialIntervalMilliseconds = Math.Max(
                        0,
                        phases[0].Phase.DelayMilliseconds),
                    StartedUtc = startedUtc,
                    Waves = new ReadOnlyCollection<BloodAltarScheduledWave>(waves),
                };
                schedule = _schedule;
                return true;
            }
        }

        internal bool TryGetNextWaveDeadline(
            long scheduleGeneration,
            out int waveIndex,
            out DateTime deadlineUtc)
        {
            lock (_syncRoot)
            {
                if (_schedule == null
                    || _schedule.Generation != scheduleGeneration
                    || !_roundScheduling
                    || _pendingWaveReservationId != Guid.Empty
                    || _nextWaveIndex < 0
                    || _nextWaveIndex >= _schedule.Waves.Count)
                {
                    waveIndex = -1;
                    deadlineUtc = DateTime.MinValue;
                    return false;
                }

                waveIndex = _nextWaveIndex;
                deadlineUtc = _schedule.StartedUtc.AddMilliseconds(
                    _schedule.Waves[waveIndex].DelayMilliseconds);
                return true;
            }
        }

        internal bool TryReserveScheduledWave(
            long scheduleGeneration,
            int waveIndex,
            out BloodAltarWaveReservation reservation,
            out BloodAltarWave wave)
        {
            reservation = default;
            wave = null;
            lock (_syncRoot)
            {
                if (_schedule == null
                    || _schedule.Generation != scheduleGeneration
                    || !_roundScheduling
                    || _pendingWaveReservationId != Guid.Empty
                    || waveIndex != _nextWaveIndex
                    || waveIndex < 0
                    || waveIndex >= _schedule.Waves.Count)
                {
                    return false;
                }

                var scheduled = _schedule.Waves[waveIndex];
                var waveIdentity = _nextWaveIdentity++;
                var spawns = new List<BloodAltarSpawn>(scheduled.SpawnCount);
                for (var index = 0; index < scheduled.SpawnCount; index++)
                {
                    if (_nextSequence <= 0 || _nextSequence > ushort.MaxValue)
                        return false;
                    spawns.Add(new BloodAltarSpawn
                    {
                        Variant = DefaultMonsterVariant,
                        SequenceId = (ushort)_nextSequence++,
                        MonsterCode = scheduled.Monster.MonsterCode,
                        MonsterType = scheduled.IsFinalMapWave
                            && scheduled.RoundNumber == Definition.MaxRounds - 1
                                ? (byte)3
                                : (byte)0,
                        Level = Definition.BasisLevel,
                        Scale = ClampUInt16((int)Math.Round(
                            scheduled.Phase.Scale * 100f)),
                        X = ClampUInt16(scheduled.Monster.X),
                        Y = ClampUInt16(scheduled.Monster.Y),
                        Z = ClampUInt16(scheduled.Monster.Z),
                        ProviderGeneration = scheduleGeneration,
                        WaveIdentity = waveIdentity,
                    });
                }
                if (spawns.Count == 0)
                    return false;

                wave = new BloodAltarWave
                {
                    Monsters = new ReadOnlyCollection<BloodAltarSpawn>(spawns),
                    TailValue = scheduled.IsFinalMapWave
                        ? (short)0
                        : ClampInt16(
                            scheduled.Monster.SpawnIntervalMilliseconds),
                    RoundNumber = scheduled.RoundNumber,
                    PhaseIndex = scheduled.PhaseIndex,
                    BatchIndex = scheduled.BatchIndex,
                    ProviderGeneration = scheduleGeneration,
                    WaveIdentity = waveIdentity,
                };
                _pendingWaveReservationId = Guid.NewGuid();
                _pendingWave = wave;
                reservation = new BloodAltarWaveReservation(
                    _pendingWaveReservationId,
                    scheduleGeneration,
                    waveIndex);
                return true;
            }
        }

        internal bool TryCommitScheduledWave(
            BloodAltarWaveReservation reservation,
            out bool schedulingComplete)
        {
            schedulingComplete = false;
            if (!reservation.IsValid)
                return false;

            lock (_syncRoot)
            {
                if (_schedule == null
                    || reservation.ReservationId != _pendingWaveReservationId
                    || reservation.ScheduleGeneration != _schedule.Generation
                    || reservation.WaveIndex != _nextWaveIndex
                    || _pendingWave == null)
                {
                    return false;
                }

                var scheduled = _schedule.Waves[_nextWaveIndex];
                foreach (var spawn in _pendingWave.Monsters)
                {
                    _activeSequences.Add(spawn.SequenceId);
                    _activeSpawns[spawn.SequenceId] = spawn;
                    if (scheduled.IsFinalMapWave)
                        _finalMapWaveSequences.Add(spawn.SequenceId);
                }

                _nextWaveIndex++;
                _pendingWaveReservationId = Guid.Empty;
                _pendingWave = null;
                if (_nextWaveIndex >= _schedule.Waves.Count)
                {
                    _roundScheduling = false;
                    schedulingComplete = true;
                }
                return true;
            }
        }

        internal void FailScheduledWave(BloodAltarWaveReservation reservation)
        {
            if (!reservation.IsValid)
                return;
            lock (_syncRoot)
            {
                if (reservation.ReservationId != _pendingWaveReservationId)
                    return;
                _pendingWaveReservationId = Guid.Empty;
                _pendingWave = null;
            }
        }

        internal bool CanAcceptActorDeath(
            DungeonRoomIdentity roomIdentity,
            ushort sequenceId,
            long providerGeneration)
        {
            lock (_syncRoot)
            {
                return _roomIdentity.Equals(roomIdentity)
                    && _schedule?.Generation == providerGeneration
                    && _activeSequences.Contains(sequenceId);
            }
        }

        internal bool TryApplyActorDeath(
            DungeonDynamicActorDefinition actor,
            out BloodAltarProgress progress,
            out IReadOnlyList<ushort> releasedSequences)
        {
            progress = BloodAltarProgress.None;
            releasedSequences = Array.Empty<ushort>();
            if (actor == null
                || !string.Equals(
                    actor.Provider,
                    DynamicActorProvider,
                    StringComparison.Ordinal))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_roomIdentity.Equals(actor.RoomIdentity)
                    || _schedule?.Generation != actor.ProviderGeneration
                    || !_activeSequences.Remove(actor.SequenceId))
                {
                    return false;
                }

                _activeSpawns.Remove(actor.SequenceId);
                var finalWaveDeath = _finalMapWaveSequences.Remove(
                    actor.SequenceId);
                if (Definition.Kind == BloodAltarDungeonKind.Endless
                    && finalWaveDeath
                    && _finalMapWaveSequences.Count == 0
                    && _map != null
                    && _nextRoundIndex == _map.Rounds.Count - 1
                    && _activeSequences.Count > 0)
                {
                    var released = _activeSequences.ToArray();
                    foreach (var sequenceId in released)
                        _activeSpawns.Remove(sequenceId);
                    _activeSequences.Clear();
                    releasedSequences = released;
                }

                return TryAdvanceAfterClearLocked(out progress);
            }
        }

        internal bool TryAdvanceAfterScheduling(
            out BloodAltarProgress progress)
        {
            lock (_syncRoot)
                return TryAdvanceAfterClearLocked(out progress);
        }

        internal bool TryResolveUltimateDifficulty(
            byte difficulty,
            int expectedPromptVersion,
            out int roundNumber)
        {
            roundNumber = 0;
            lock (_syncRoot)
            {
                if (Definition.Kind != BloodAltarDungeonKind.Ultimate
                    || !_awaitingDifficulty
                    || expectedPromptVersion != _difficultyPromptVersion
                    || (difficulty != 1 && difficulty != 2))
                {
                    return false;
                }

                _awaitingDifficulty = false;
                _pendingDifficulty = difficulty;
                roundNumber = _completedRounds;
                return true;
            }
        }

        internal IReadOnlyList<BloodAltarSpawn> CaptureActiveSpawns()
        {
            lock (_syncRoot)
                return new ReadOnlyCollection<BloodAltarSpawn>(
                    _activeSpawns.Values
                        .OrderBy(spawn => spawn.SequenceId)
                        .ToArray());
        }

        internal IReadOnlyList<byte> CaptureCompletedUltimateDifficulties()
        {
            lock (_syncRoot)
                return new ReadOnlyCollection<byte>(
                    _completedUltimateDifficulties.ToArray());
        }

        internal bool TryCaptureCurrentSchedule(
            out BloodAltarRoundSchedule schedule)
        {
            lock (_syncRoot)
            {
                schedule = _schedule;
                return schedule != null && _roundInProgress;
            }
        }

        private bool TryAdvanceAfterClearLocked(out BloodAltarProgress progress)
        {
            progress = BloodAltarProgress.None;
            if (!_roundInProgress
                || _roundScheduling
                || _pendingWaveReservationId != Guid.Empty
                || _activeSequences.Count > 0)
            {
                return false;
            }

            if (Definition.Kind == BloodAltarDungeonKind.Ultimate
                && _schedule != null
                && (_schedule.Difficulty == 1 || _schedule.Difficulty == 2))
            {
                _completedUltimateDifficulties.Add(_schedule.Difficulty);
            }

            _roundInProgress = false;
            _completedRounds++;
            _nextRoundIndex++;
            _schedule = null;
            _nextWaveIndex = 0;
            _finalMapWaveSequences.Clear();

            if (_map == null || _nextRoundIndex >= _map.Rounds.Count)
            {
                _completedRoomIds.Add(_roomIdentity.RoomInstanceId);
                progress = _completedRounds >= Definition.MaxRounds
                    ? BloodAltarProgress.DungeonComplete
                    : BloodAltarProgress.MapComplete;
                return true;
            }

            if (Definition.Kind == BloodAltarDungeonKind.Ultimate)
            {
                if (_nextRoundIndex == _map.Rounds.Count - 1)
                {
                    _pendingDifficulty = 1;
                    progress = BloodAltarProgress.ReadyForFinalRound;
                }
                else
                {
                    _awaitingDifficulty = true;
                    _difficultyPromptVersion++;
                    progress = BloodAltarProgress.AwaitingUltimateDifficulty;
                }
            }
            else
            {
                progress = BloodAltarProgress.ReadyForNextRound;
            }
            return true;
        }

        private List<BloodAltarScheduledWave> BuildRoundSchedule(
            IReadOnlyList<IndexedPhase> phases,
            int roundIndex,
            int globalRoundNumber)
        {
            var waves = new List<BloodAltarScheduledWave>();
            long elapsed = 0;
            for (var index = 0; index < phases.Count;)
            {
                var concurrent = Math.Min(
                    MaxConcurrentPhases,
                    Math.Min(
                        phases.Count - index,
                        Math.Max(1, phases[index].Phase.ConcurrentPhaseCount)));
                var groupEnd = elapsed;
                BloodAltarMonsterDefinition groupTailMonster = null;
                for (var concurrentIndex = 0;
                    concurrentIndex < concurrent;
                    concurrentIndex++)
                {
                    var indexed = phases[index + concurrentIndex];
                    var monster = _map.Monsters[
                        indexed.Phase.MonsterTemplateIndex];
                    var batches = Math.Max(1, monster.BatchCount);
                    var interval = Math.Max(
                        0,
                        monster.SpawnIntervalMilliseconds);
                    for (var batchIndex = 0;
                        batchIndex < batches;
                        batchIndex++)
                    {
                        var spawnCount = CalculateSpawnCount(
                            monster,
                            batchIndex);
                        if (monster.MonsterCode <= 0 || spawnCount <= 0)
                            continue;
                        var delay = elapsed
                            + Math.Max(0, indexed.Phase.DelayMilliseconds)
                            + (long)interval * batchIndex;
                        waves.Add(new BloodAltarScheduledWave
                        {
                            DelayMilliseconds = ClampDelay(delay),
                            RoundIndex = roundIndex,
                            RoundNumber = globalRoundNumber,
                            PhaseIndex = indexed.Index,
                            BatchIndex = batchIndex,
                            SpawnCount = spawnCount,
                            Phase = indexed.Phase,
                            Monster = monster,
                        });
                    }

                    var candidateEnd = elapsed
                        + Math.Max(0, indexed.Phase.DelayMilliseconds)
                        + (long)interval * (batches - 1);
                    if (groupTailMonster == null || candidateEnd >= groupEnd)
                    {
                        groupEnd = candidateEnd;
                        groupTailMonster = monster;
                    }
                }

                if (groupTailMonster != null)
                {
                    var tailDelay = Math.Max(
                        0L,
                        groupTailMonster.DurationMilliseconds
                            - (long)Math.Max(
                                0,
                                groupTailMonster.SpawnIntervalMilliseconds)
                            * Math.Max(1, groupTailMonster.BatchCount));
                    elapsed = groupEnd + tailDelay;
                }
                index += concurrent;
            }

            waves = waves
                .OrderBy(wave => wave.DelayMilliseconds)
                .ThenBy(wave => wave.PhaseIndex)
                .ThenBy(wave => wave.BatchIndex)
                .ToList();
            if (roundIndex == _map.Rounds.Count - 1 && waves.Count > 0)
                waves[waves.Count - 1].IsFinalMapWave = true;
            return waves;
        }

        private static int CalculateSpawnCount(
            BloodAltarMonsterDefinition monster,
            int batchIndex)
        {
            var count = monster.BaseSpawnCount
                + (long)monster.SpawnCountIncrement * batchIndex;
            return (int)Math.Min(ushort.MaxValue, Math.Max(0, count));
        }

        private static int ClampDelay(long value)
            => (int)Math.Min(int.MaxValue, Math.Max(0, value));

        private static ushort ClampUInt16(int value)
            => (ushort)Math.Min(ushort.MaxValue, Math.Max(0, value));

        private static short ClampInt16(int value)
            => (short)Math.Min(short.MaxValue, Math.Max(0, value));

        private static long NextGeneration(long previous)
            => previous == long.MaxValue ? 1 : Math.Max(1, previous + 1);

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value == DateTime.MinValue)
                return DateTime.UtcNow;
            if (value.Kind == DateTimeKind.Utc)
                return value;
            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private readonly struct IndexedPhase
        {
            internal IndexedPhase(BloodAltarPhaseDefinition phase, int index)
            {
                Phase = phase;
                Index = index;
            }

            internal BloodAltarPhaseDefinition Phase { get; }
            internal int Index { get; }
        }
    }
}
