using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonPermissionProgressionPlan
    {
        internal DungeonPermissionProgressionPlan(
            IReadOnlyList<DungeonPermissionEntrySnapshot> entries,
            bool requiresPersistence)
        {
            Entries = entries ?? Array.Empty<DungeonPermissionEntrySnapshot>();
            RequiresPersistence = requiresPersistence;
        }

        internal IReadOnlyList<DungeonPermissionEntrySnapshot> Entries { get; }

        internal bool RequiresPersistence { get; }
    }

    internal static class DungeonPermissionProjector
    {
        internal static IReadOnlyList<DungeonPermissionEntrySnapshot>
            ProjectForClient(
                IReadOnlyCollection<DungeonPermissionEntrySnapshot> permissions)
        {
            if (permissions == null || permissions.Count == 0)
                return Array.Empty<DungeonPermissionEntrySnapshot>();

            var states = new Dictionary<int, byte>();
            var order = new List<int>();

            foreach (var permission in permissions)
            {
                if (!IsPersistentPermission(permission))
                    continue;

                var dungeonId = (int)permission.DungeonId;
                if (!states.TryGetValue(dungeonId, out var state))
                {
                    states[dungeonId] = permission.ClearState;
                    order.Add(dungeonId);
                }
                else if (state < permission.ClearState)
                {
                    states[dungeonId] = permission.ClearState;
                }
            }

            var result = new List<DungeonPermissionEntrySnapshot>();
            foreach (var dungeonId in order)
                result.Add(CreateEntry(dungeonId, states[dungeonId]));

            return result;
        }

        internal static DungeonPermissionProgressionPlan BuildProgressionPlan(
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> persisted,
            int dungeonId,
            byte requestedClearState)
        {
            if (!DungeonPermissionScopePolicy.IsAccountDifficulty(dungeonId)
                || requestedClearState == 0)
            {
                return new DungeonPermissionProgressionPlan(
                    Array.Empty<DungeonPermissionEntrySnapshot>(),
                    requiresPersistence: false);
            }

            var persistedStates = BuildStateLookup(persisted);
            var targetState = requestedClearState;
            if (persistedStates.TryGetValue(dungeonId, out var state)
                && targetState < state)
            {
                targetState = state;
            }

            var entries = new[] { CreateEntry(dungeonId, targetState) };
            var requiresPersistence = !persistedStates.TryGetValue(
                    dungeonId,
                    out var persistedState)
                || persistedState < targetState;
            return new DungeonPermissionProgressionPlan(
                entries,
                requiresPersistence);
        }

        internal static bool IsApplied(
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> persisted,
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> expected)
        {
            if (expected == null || expected.Count == 0)
                return true;

            var persistedStates = BuildStateLookup(persisted);
            return expected.All(entry =>
                entry != null
                && persistedStates.TryGetValue(entry.DungeonId, out var state)
                && state >= entry.ClearState);
        }

        private static Dictionary<int, byte> BuildStateLookup(
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> permissions)
        {
            var result = new Dictionary<int, byte>();
            if (permissions == null)
                return result;

            foreach (var permission in permissions)
            {
                if (permission == null
                    || permission.DungeonId == 0
                    || permission.ClearState == 0)
                {
                    continue;
                }

                if (!result.TryGetValue(permission.DungeonId, out var state)
                    || state < permission.ClearState)
                {
                    result[permission.DungeonId] = permission.ClearState;
                }
            }

            return result;
        }

        private static bool IsPersistentPermission(
            DungeonPermissionEntrySnapshot permission)
            => permission != null
                && permission.ClearState > 0
                && CanProjectDungeon(permission.DungeonId);

        private static bool CanProjectDungeon(int dungeonId)
        {
            var scope = DungeonPermissionScopePolicy.Resolve(dungeonId);
            return scope == DungeonPermissionPersistenceScope.AccountDifficulty
                || scope == DungeonPermissionPersistenceScope.CharacterMechanism;
        }

        private static DungeonPermissionEntrySnapshot CreateEntry(
            int dungeonId,
            byte clearState)
            => new DungeonPermissionEntrySnapshot
            {
                DungeonId = checked((ushort)dungeonId),
                ClearState = clearState,
            };
    }
}
