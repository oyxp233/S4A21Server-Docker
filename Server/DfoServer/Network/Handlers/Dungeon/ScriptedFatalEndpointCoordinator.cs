using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using System.Collections.Generic;
using System.Linq;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal readonly struct ScriptedFatalDeathResult
    {
        internal ScriptedFatalDeathResult(
            bool suppressRespawn,
            bool shouldClearDungeon,
            string reason)
        {
            SuppressRespawn = suppressRespawn;
            ShouldClearDungeon = shouldClearDungeon;
            Reason = reason ?? string.Empty;
        }

        internal bool SuppressRespawn { get; }
        internal bool ShouldClearDungeon { get; }
        internal string Reason { get; }
    }

    internal static class ScriptedFatalEndpointCoordinator
    {
        internal static void ConfigureSelection(
            DungeonRun run,
            PvfLib.MazeInfo maze,
            int[] bossPosition,
            IReadOnlyList<ActiveQuest> activeQuests)
        {
            if (run == null)
                return;

            run.ScriptedFatalEndpoint = null;
            var activeQuestIds = activeQuests == null
                ? new HashSet<int>()
                : new HashSet<int>(activeQuests.Select(quest => (int)quest.QuestId));
            if (!GameWorld.ScriptedFatalEndpointData.TryResolve(
                    run.DungeonId,
                    run.MazeIndex,
                    maze,
                    bossPosition,
                    run.SelectedBossMapId,
                    run.Difficulty,
                    activeQuestIds,
                    out var definition,
                    out var reason))
            {
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    FileLogger.Log(
                        $"[ScriptedFatalEndpoint] selection disabled: " +
                        $"dungeon={run.DungeonId} maze={run.MazeIndex} " +
                        $"reason={reason}");
                }
                return;
            }

            run.ScriptedFatalEndpoint =
                new ScriptedFatalEndpointRuntime(definition);
            run.IgnoreDefaultDungeonClear = true;
            FileLogger.Log(
                $"[ScriptedFatalEndpoint] selection configured: " +
                $"dungeon={run.DungeonId} maze={run.MazeIndex} " +
                $"quest={definition.QuestId} endpoint=" +
                $"({definition.EndpointX},{definition.EndpointY}) " +
                $"map={definition.MapId} fixtures=" +
                $"[{string.Join(",", definition.Actors.Select(actor => actor.MonsterCode))}] " +
                $"passives=" +
                $"[{string.Join(",", definition.Actors.Select(actor => actor.TriggerPassiveObjectCode))}]");
        }

        internal static void CloneSelection(
            DungeonRun source,
            DungeonRun target)
        {
            if (target == null)
                return;

            target.ScriptedFatalEndpoint =
                source?.ScriptedFatalEndpoint?.CloneFresh();
        }

        internal static void OnPassiveObjectDestroyed(
            EnhancedClientSession session,
            int objectCode)
            => OnPassiveObjectDestroyed(
                session,
                session?.Player?.CurrentRun,
                objectCode);

        internal static void OnPassiveObjectDestroyed(
            EnhancedClientSession session,
            DungeonRun run,
            int objectCode)
        {
            if (session?.Player == null
                || run == null
                || objectCode <= 0
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
            {
                return;
            }

            bool armed;
            int mapId;
            lock (run.SyncRoot)
            {
                var runtime = run.ScriptedFatalEndpoint;
                if (run.Phase != DungeonRunPhase.InProgress
                    || runtime == null
                    || !TryGetCurrentEndpointRoom(run, runtime, out mapId))
                {
                    return;
                }

                armed = runtime.TryArmForPassiveObject(objectCode);
            }

            if (armed)
            {
                FileLogger.Log(
                    $"[ScriptedFatalEndpoint] armed by passive object: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"map={mapId} object={objectCode}");
            }
        }

        internal static void OnMonsterKilled(
            EnhancedClientSession session,
            int monsterCode)
            => OnMonsterKilled(
                session,
                session?.Player?.CurrentRun,
                monsterCode);

        internal static void OnMonsterKilled(
            EnhancedClientSession session,
            DungeonRun run,
            int monsterCode)
        {
            if (session?.Player == null
                || run == null
                || monsterCode <= 0
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
            {
                return;
            }

            bool armed;
            int mapId;
            lock (run.SyncRoot)
            {
                var runtime = run.ScriptedFatalEndpoint;
                if (run.Phase != DungeonRunPhase.InProgress
                    || runtime == null
                    || !TryGetCurrentEndpointRoom(run, runtime, out mapId))
                {
                    return;
                }

                armed = runtime.TryArmForFixtureMonster(monsterCode);
            }

            if (armed)
            {
                FileLogger.Log(
                    $"[ScriptedFatalEndpoint] armed by fixture death: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"map={mapId} monster={monsterCode}");
            }
        }

        internal static ScriptedFatalDeathResult OnCharacterDied(
            EnhancedClientSession session)
            => OnCharacterDied(
                session,
                session?.Player?.CurrentRun);

        internal static ScriptedFatalDeathResult OnCharacterDied(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (session?.Player == null
                || run == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
            {
                return default;
            }

            bool handled;
            bool shouldClear;
            int mapId;
            lock (run.SyncRoot)
            {
                var runtime = run.ScriptedFatalEndpoint;
                if (runtime == null
                    || !TryGetCurrentEndpointRoom(run, runtime, out mapId))
                {
                    return default;
                }

                handled = runtime.TryHandleCharacterDeath(out shouldClear);
            }

            if (!handled)
                return default;

            var reason =
                $"scripted fatal endpoint death map={mapId}";
            FileLogger.Log(
                $"[ScriptedFatalEndpoint] character death handled: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"map={mapId} clear={shouldClear}");
            return new ScriptedFatalDeathResult(
                suppressRespawn: true,
                shouldClearDungeon: shouldClear,
                reason: reason);
        }

        private static bool TryGetCurrentEndpointRoom(
            DungeonRun run,
            ScriptedFatalEndpointRuntime runtime,
            out int mapId)
        {
            mapId = 0;
            var definition = runtime?.Definition;
            if (definition == null
                || run.RoomStates == null
                || !run.RoomStates.TryGetValue(run.RoomKey, out var roomState)
                || roomState == null)
            {
                return false;
            }

            mapId = roomState.Maze.Index;
            return roomState.Maze.X == definition.EndpointX
                && roomState.Maze.Y == definition.EndpointY
                && mapId == definition.MapId;
        }
    }
}
