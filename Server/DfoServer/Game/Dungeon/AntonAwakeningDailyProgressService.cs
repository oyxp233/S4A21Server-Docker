using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Dungeon
{
    internal enum AntonAwakeningAdmissionStatus
    {
        NotApplicable = 0,
        Allowed = 1,
        MissingPrerequisites = 2,
        InvalidState = 3,
    }

    internal sealed class AntonAwakeningAdmissionDecision
    {
        internal AntonAwakeningAdmissionDecision(
            AntonAwakeningAdmissionStatus status,
            IReadOnlyList<int> missingDungeonIds = null)
        {
            Status = status;
            MissingDungeonIds = missingDungeonIds ?? Array.Empty<int>();
        }

        internal AntonAwakeningAdmissionStatus Status { get; }
        internal IReadOnlyList<int> MissingDungeonIds { get; }
        internal bool Allowed =>
            Status == AntonAwakeningAdmissionStatus.NotApplicable
            || Status == AntonAwakeningAdmissionStatus.Allowed;
    }

    internal sealed class AntonAwakeningDailyProgressService
    {
        private readonly AntonAwakeningDailyProgressRepository _repository;
        private readonly SequentialDungeonDefinitionCatalog _catalog;
        private readonly Func<DateTime> _utcNow;

        internal AntonAwakeningDailyProgressService(
            AntonAwakeningDailyProgressRepository repository,
            SequentialDungeonDefinitionCatalog catalog = null,
            Func<DateTime> utcNow = null)
        {
            _repository = repository
                ?? throw new ArgumentNullException(nameof(repository));
            _catalog = catalog ?? SequentialDungeonDefinitionCatalog.Current;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal bool TryRestore(
            int characterId,
            int configKey,
            out AntonNormalSyncState state)
        {
            state = null;
            if (characterId <= 0
                || !_catalog.TryGetByGroupKey(configKey, out var definition)
                || !definition.ShowIndividualProcess)
            {
                return false;
            }

            var permissions = _repository.EnsureCurrentDayAndLoad(
                characterId,
                definition,
                _utcNow());
            state = BuildState(definition, permissions);
            return true;
        }

        internal bool TryApplyClear(
            int characterId,
            int dungeonId,
            out AntonNormalClearApplicationResult result)
        {
            result = null;
            if (characterId <= 0
                || !_catalog.TryResolvePrimaryByDungeonId(
                    dungeonId,
                    out var definition)
                || !definition.ShowIndividualProcess)
            {
                return false;
            }

            var sequence = new AntonNormalSequence(definition);
            if (!AntonNormalConquest.TryResolveClearPlan(
                    sequence,
                    dungeonId,
                    out var plan))
            {
                return false;
            }

            var updates = new List<DungeonPermissionEntrySnapshot>();
            AddPermissionUpdate(
                updates,
                dungeonId,
                sequence.Difficulty,
                completed: true);
            AddPermissionUpdate(
                updates,
                plan.NextDungeonId,
                sequence.Difficulty,
                completed: false);
            AddPreviewPermissionUpdate(
                updates,
                plan.PreviewDungeonId,
                sequence.Difficulty);

            var permissions = _repository.RecordClearAndLoad(
                characterId,
                definition,
                updates,
                _utcNow(),
                out var changes);
            var state = BuildState(definition, permissions);
            result = new AntonNormalClearApplicationResult(state, changes);
            FileLogger.Log(
                $"[AntonAwakeningProgress] clear persisted: " +
                $"cid={characterId} dungeon={dungeonId} " +
                $"key={definition.GroupKey} progress={state.ProgressIndex} " +
                $"routeMask=0x{state.RouteMask:X2}");
            return true;
        }

        internal AntonAwakeningAdmissionDecision EvaluateAdmission(
            int characterId,
            int dungeonId)
        {
            var resolution = _catalog.ResolvePrimaryByDungeonId(
                dungeonId,
                out var definition);
            if (resolution == SequentialDungeonCapabilityResolution.Absent)
            {
                return new AntonAwakeningAdmissionDecision(
                    AntonAwakeningAdmissionStatus.NotApplicable);
            }
            if (resolution == SequentialDungeonCapabilityResolution.Ambiguous)
            {
                FileLogger.Log(
                    "[AntonAwakeningProgress] ambiguous admission definition: "
                    + $"dungeon={dungeonId}");
                return new AntonAwakeningAdmissionDecision(
                    AntonAwakeningAdmissionStatus.InvalidState);
            }
            if (!definition.ShowIndividualProcess)
            {
                return new AntonAwakeningAdmissionDecision(
                    AntonAwakeningAdmissionStatus.NotApplicable);
            }
            var targetIndex = definition.IndexOf(dungeonId);
            if (targetIndex < 0)
            {
                FileLogger.Log(
                    "[AntonAwakeningProgress] admission target missing from definition: "
                    + $"dungeon={dungeonId} key={definition.GroupKey}");
                return new AntonAwakeningAdmissionDecision(
                    AntonAwakeningAdmissionStatus.InvalidState);
            }
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (targetIndex == 0)
            {
                return new AntonAwakeningAdmissionDecision(
                    AntonAwakeningAdmissionStatus.Allowed);
            }

            var permissions = _repository.EnsureCurrentDayAndLoad(
                characterId,
                definition,
                _utcNow());
            var clearStates = GroupClearStates(permissions);

            var missing = new List<int>();
            for (var index = 0; index < targetIndex; index++)
            {
                var prerequisiteDungeonId = definition.DungeonIds[index];
                if (!IsCompleted(
                        prerequisiteDungeonId,
                        definition.Difficulty,
                        clearStates))
                {
                    missing.Add(prerequisiteDungeonId);
                }
            }
            return missing.Count == 0
                ? new AntonAwakeningAdmissionDecision(
                    AntonAwakeningAdmissionStatus.Allowed)
                : new AntonAwakeningAdmissionDecision(
                    AntonAwakeningAdmissionStatus.MissingPrerequisites,
                    missing);
        }

        internal void EnsureCurrentDay(int characterId)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            var utcNow = _utcNow();
            foreach (var definition in _catalog.Definitions)
            {
                if (!definition.ShowIndividualProcess)
                    continue;
                _repository.EnsureCurrentDayAndLoad(
                    characterId,
                    definition,
                    utcNow);
            }
        }

        private static AntonNormalSyncState BuildState(
            SequentialDungeonDefinition definition,
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> permissions)
        {
            var sequence = new AntonNormalSequence(definition);
            var clearStates = GroupClearStates(permissions);
            var routeMask = 0;
            var progressIndex = 0;
            for (var index = 0; index < sequence.DungeonIds.Count; index++)
            {
                var dungeonId = sequence.DungeonIds[index];
                if (!IsCompleted(
                        dungeonId,
                        definition.Difficulty,
                        clearStates))
                {
                    continue;
                }

                progressIndex = Math.Max(progressIndex, index + 1);
            }
            for (var index = 0;
                index < definition.PrerequisiteDungeonIds.Count;
                index++)
            {
                if (IsCompleted(
                        definition.PrerequisiteDungeonIds[index],
                        definition.Difficulty,
                        clearStates))
                {
                    routeMask |= 1 << index;
                }
            }

            var entries = clearStates
                .OrderBy(pair => sequence.IndexOf(pair.Key))
                .Where(pair => sequence.IndexOf(pair.Key) >= 0 && pair.Value > 0)
                .Select(pair => new DungeonPermissionEntrySnapshot
                {
                    DungeonId = (ushort)pair.Key,
                    ClearState = pair.Value,
                })
                .ToList();
            return new AntonNormalSyncState(
                sequence,
                (byte)Math.Min(progressIndex, byte.MaxValue),
                entries,
                routeMask);
        }

        private static Dictionary<int, byte> GroupClearStates(
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> permissions)
            => (permissions ?? Array.Empty<DungeonPermissionEntrySnapshot>())
                .GroupBy(entry => (int)entry.DungeonId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Max(entry => entry.ClearState));

        private static bool IsCompleted(
            int dungeonId,
            byte difficulty,
            IReadOnlyDictionary<int, byte> clearStates)
            => AntonNormalConquest.TryResolveCompletedState(
                    dungeonId,
                    difficulty,
                    out var completedState)
                && clearStates.TryGetValue(
                    dungeonId,
                    out var persistedState)
                && persistedState >= completedState;

        private static void AddPermissionUpdate(
            ICollection<DungeonPermissionEntrySnapshot> updates,
            int dungeonId,
            byte difficulty,
            bool completed)
        {
            if (dungeonId <= 0)
                return;
            var resolved = completed
                ? AntonNormalConquest.TryResolveCompletedState(
                    dungeonId,
                    difficulty,
                    out var clearState)
                : AntonNormalConquest.TryResolveUnlockedState(
                    dungeonId,
                    difficulty,
                    out clearState);
            if (!resolved)
                return;
            updates.Add(new DungeonPermissionEntrySnapshot
            {
                DungeonId = (ushort)dungeonId,
                ClearState = clearState,
            });
        }

        private static void AddPreviewPermissionUpdate(
            ICollection<DungeonPermissionEntrySnapshot> updates,
            int dungeonId,
            byte difficulty)
        {
            if (dungeonId <= 0
                || !AntonNormalConquest.TryResolveUnlockedState(
                    dungeonId,
                    difficulty,
                    out var unlockedState))
            {
                return;
            }
            updates.Add(new DungeonPermissionEntrySnapshot
            {
                DungeonId = (ushort)dungeonId,
                ClearState = (byte)Math.Max(1, unlockedState - 1),
            });
        }
    }
}
