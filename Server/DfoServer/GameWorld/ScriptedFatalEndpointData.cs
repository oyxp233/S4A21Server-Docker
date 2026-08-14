using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DfoServer.GameWorld
{
    internal sealed class ScriptedFatalEndpointActorDefinition
    {
        internal int MonsterCode { get; set; }
        internal int TriggerPassiveObjectCode { get; set; }
        internal int CustomActionIndex { get; set; }
        internal string MonsterPath { get; set; }
        internal string WaitingActionPath { get; set; }
        internal string FatalActionPath { get; set; }
    }

    internal sealed class ScriptedFatalEndpointDefinition
    {
        internal int QuestId { get; set; }
        internal int MazeIndex { get; set; }
        internal int EndpointX { get; set; }
        internal int EndpointY { get; set; }
        internal int MapId { get; set; }
        internal IReadOnlyList<ScriptedFatalEndpointActorDefinition> Actors { get; set; } =
            Array.Empty<ScriptedFatalEndpointActorDefinition>();

        internal bool MatchesFixtureMonster(int monsterCode)
            => Actors.Any(actor => actor.MonsterCode == monsterCode);

        internal bool MatchesTriggerPassiveObject(int objectCode)
            => Actors.Any(actor => actor.TriggerPassiveObjectCode == objectCode);
    }

    // Resolves quest-connected endpoint scenes whose fixture scripts kill the
    // player after a room passive object disappears. IDs are relation keys read
    // from PVF; a definition is enabled only when the complete script chain is
    // unambiguous.
    internal static class ScriptedFatalEndpointData
    {
        private sealed class Resolution
        {
            internal ScriptedFatalEndpointDefinition Definition { get; set; }
            internal string Reason { get; set; }
        }

        private static readonly Lazy<LstFile> MonsterList =
            new Lazy<LstFile>(
                () => Dungeon.LoadLstFile(Path.Combine("monster", "monster.lst")),
                LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly ConcurrentDictionary<string, Lazy<Resolution>> Cache =
            new ConcurrentDictionary<string, Lazy<Resolution>>(
                StringComparer.Ordinal);

        internal static bool TryResolve(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze,
            int[] bossPosition,
            int selectedBossMapId,
            int difficulty,
            ICollection<int> activeQuestIds,
            out ScriptedFatalEndpointDefinition definition,
            out string reason)
        {
            definition = null;
            reason = null;
            if (dungeonId <= 0 || maze == null)
                return false;

            DungeonFile dungeonFile;
            try
            {
                dungeonFile = Dungeon.GetDungeonFile(dungeonId);
            }
            catch (Exception ex)
            {
                reason = $"dungeon PVF lookup failed: {ex.Message}";
                return false;
            }

            if (!TryResolveActiveConnection(
                    dungeonFile,
                    maze,
                    difficulty,
                    activeQuestIds,
                    out var questId,
                    out reason))
            {
                return false;
            }

            var endpoint = ResolveEndpoint(maze, bossPosition);
            if (endpoint == null)
            {
                reason = "active quest connection has no Boss endpoint";
                return false;
            }

            if (!TryResolveEndpointMapId(
                    maze,
                    endpoint.Value.X,
                    endpoint.Value.Y,
                    selectedBossMapId,
                    out var mapId,
                    out reason))
            {
                return false;
            }

            var cacheKey = string.Join(
                ":",
                dungeonId,
                mazeIndex,
                mapId,
                questId,
                endpoint.Value.X,
                endpoint.Value.Y);
            var resolution = Cache.GetOrAdd(
                cacheKey,
                _ => new Lazy<Resolution>(
                    () => ResolveDefinition(
                        dungeonId,
                        mazeIndex,
                        maze,
                        questId,
                        endpoint.Value.X,
                        endpoint.Value.Y,
                        mapId),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;

            definition = resolution.Definition;
            reason = resolution.Reason;
            return definition != null;
        }

        private static Resolution ResolveDefinition(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze,
            int questId,
            int endpointX,
            int endpointY,
            int mapId)
        {
            try
            {
                if (maze.ClearConditions != null && maze.ClearConditions.Count > 0)
                {
                    return Failure(
                        "quest endpoint has an explicit clear condition");
                }

                var mapList = Dungeon.LoadLstFile(Path.Combine("map", "map.lst"));
                var mapPath = Dungeon.ResolveFilePath(mapList, mapId, "map");
                var map = MapFile.Parse(PvfArchiveAccessor.ReadText(
                    Path.Combine("map", mapPath)));

                if (map.Monsters == null || map.Monsters.Count == 0)
                    return Failure("endpoint has no MAP monster actors");
                if (map.AICharacters != null && map.AICharacters.Count > 0)
                    return Failure("endpoint contains APC actors");

                var passiveCodes = new HashSet<int>(
                    (map.PassiveObjects ?? new List<PassiveObjectInfo>())
                        .Where(item => item != null && item.ObjectCode > 0)
                        .Select(item => item.ObjectCode));
                var actorsByMonster =
                    new Dictionary<int, ScriptedFatalEndpointActorDefinition>();
                var actors = new List<ScriptedFatalEndpointActorDefinition>();

                foreach (var mapMonster in map.Monsters)
                {
                    if (!mapMonster.MonsterId.HasValue
                        || mapMonster.MonsterId.Value <= 0)
                    {
                        return Failure("endpoint contains an invalid monster actor");
                    }

                    var monsterCode = mapMonster.MonsterId.Value;
                    if (!actorsByMonster.TryGetValue(monsterCode, out var actor))
                    {
                        if (!TryResolveActor(
                                monsterCode,
                                passiveCodes,
                                out actor,
                                out var actorReason))
                        {
                            return Failure(
                                $"monster {monsterCode} is not a scripted fatal fixture: " +
                                actorReason);
                        }

                        actorsByMonster[monsterCode] = actor;
                    }

                    actors.Add(actor);
                }

                if (actors.Count == 0)
                    return Failure("endpoint has no scripted fatal fixture actors");

                return new Resolution
                {
                    Definition = new ScriptedFatalEndpointDefinition
                    {
                        QuestId = questId,
                        MazeIndex = mazeIndex,
                        EndpointX = endpointX,
                        EndpointY = endpointY,
                        MapId = mapId,
                        Actors = actors,
                    },
                };
            }
            catch (Exception ex)
            {
                return Failure($"PVF relation lookup failed: {ex.Message}");
            }
        }

        private static bool TryResolveActor(
            int monsterCode,
            ISet<int> roomPassiveCodes,
            out ScriptedFatalEndpointActorDefinition definition,
            out string reason)
        {
            definition = null;
            reason = string.Empty;

            var monsterEntry = MonsterList.Value.GetById(monsterCode);
            if (monsterEntry == null || string.IsNullOrWhiteSpace(monsterEntry.FilePath))
            {
                reason = "monster.lst entry is missing";
                return false;
            }

            var monsterPath = NormalizePvfPath(
                "monster/" + monsterEntry.FilePath);
            var monster = MonsterFile.Parse(PvfArchiveAccessor.ReadText(monsterPath));
            if (!ContainsToken(monster.Categories, "[fixture]"))
            {
                reason = "MOB category does not contain [fixture]";
                return false;
            }
            if (string.IsNullOrWhiteSpace(monster.WaitingAction))
            {
                reason = "MOB waiting action is missing";
                return false;
            }

            var monsterDirectory = GetDirectory(monsterPath);
            var waitingActionPath = NormalizePvfPath(
                monsterDirectory + "/" + monster.WaitingAction);
            var waitingAction = ActFile.Parse(
                PvfArchiveAccessor.ReadText(waitingActionPath));
            if (!TryResolveCustomTransition(
                    waitingAction,
                    out var passiveObjectCode,
                    out var customActionIndex,
                    out reason))
            {
                return false;
            }

            if (!roomPassiveCodes.Contains(passiveObjectCode))
            {
                reason = $"trigger passive object {passiveObjectCode} is not in endpoint map";
                return false;
            }
            if (customActionIndex < 0
                || monster.EtcActions == null
                || customActionIndex >= monster.EtcActions.Count)
            {
                reason = $"CUSTOM {customActionIndex} is outside MOB [etc action] list";
                return false;
            }

            var fatalActionPath = NormalizePvfPath(
                monsterDirectory + "/" + monster.EtcActions[customActionIndex]);
            var fatalAction = ActFile.Parse(
                PvfArchiveAccessor.ReadText(fatalActionPath));
            if (!HasFatalAllEnemyCharacterBehavior(fatalAction))
            {
                reason = "CUSTOM action has no ALL ENEMY CHARACTER HP <= -100% behavior";
                return false;
            }

            definition = new ScriptedFatalEndpointActorDefinition
            {
                MonsterCode = monsterCode,
                TriggerPassiveObjectCode = passiveObjectCode,
                CustomActionIndex = customActionIndex,
                MonsterPath = monsterPath,
                WaitingActionPath = waitingActionPath,
                FatalActionPath = fatalActionPath,
            };
            return true;
        }

        internal static bool TryResolveCustomTransition(
            ActFile action,
            out int passiveObjectCode,
            out int customActionIndex,
            out string reason)
        {
            passiveObjectCode = 0;
            customActionIndex = -1;
            reason = string.Empty;
            if (action == null)
            {
                reason = "waiting ACT is null";
                return false;
            }

            var matches = new List<(int PassiveObjectCode, int CustomActionIndex)>();
            foreach (var trigger in action.Triggers)
            {
                if (!ContainsToken(trigger.Selectors, "PASSIVE")
                    || !trigger.ObjectIndex.HasValue
                    || trigger.ObjectIndex.Value <= 0
                    || !trigger.CheckedNo
                    || trigger.Comparison != "<="
                    || trigger.ComparisonValue != 0)
                {
                    continue;
                }

                foreach (var reference in trigger.BehaviorReferences)
                {
                    if (!string.Equals(
                            reference.Target,
                            "ME",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var behavior = action.Behaviors.FirstOrDefault(
                        item => item.Index == reference.BehaviorIndex);
                    if (behavior == null
                        || !behavior.SetsAction
                        || !behavior.CustomActionIndex.HasValue
                        || behavior.CustomActionIndex.Value < 0)
                    {
                        continue;
                    }

                    matches.Add((
                        trigger.ObjectIndex.Value,
                        behavior.CustomActionIndex.Value));
                }
            }

            var distinct = matches.Distinct().ToList();
            if (distinct.Count != 1)
            {
                reason = distinct.Count == 0
                    ? "waiting ACT has no passive-disappearance CUSTOM transition"
                    : $"waiting ACT has {distinct.Count} matching transitions";
                return false;
            }

            passiveObjectCode = distinct[0].PassiveObjectCode;
            customActionIndex = distinct[0].CustomActionIndex;
            return true;
        }

        internal static bool HasFatalAllEnemyCharacterBehavior(ActFile action)
        {
            if (action == null)
                return false;

            foreach (var trigger in action.Triggers)
            {
                if (!ContainsToken(trigger.Selectors, "ALL ENEMY")
                    || !ContainsToken(trigger.ObjectTypes, "CHARACTER"))
                {
                    continue;
                }

                foreach (var reference in trigger.BehaviorReferences)
                {
                    if (!string.Equals(
                            reference.Target,
                            "CHECKUP OBJECT",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var behavior = action.Behaviors.FirstOrDefault(
                        item => item.Index == reference.BehaviorIndex);
                    if (behavior != null
                        && behavior.RestoresHp
                        && behavior.RestoreHpPercent
                        && behavior.RestoreHpValue.HasValue
                        && behavior.RestoreHpValue.Value <= -100)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryResolveActiveConnection(
            DungeonFile dungeonFile,
            MazeInfo maze,
            int difficulty,
            ICollection<int> activeQuestIds,
            out int questId,
            out string reason)
        {
            questId = 0;
            reason = null;
            if (activeQuestIds == null || activeQuestIds.Count == 0)
                return false;

            var matches = new HashSet<int>();
            AddActiveConnection(
                dungeonFile?.QuestConnection,
                difficulty,
                activeQuestIds,
                matches);
            AddActiveConnection(
                maze?.QuestConnection,
                difficulty,
                activeQuestIds,
                matches);

            if (matches.Count == 0)
                return false;
            if (matches.Count > 1)
            {
                reason = $"multiple active quest connections matched: {string.Join(",", matches)}";
                return false;
            }

            questId = matches.First();
            return true;
        }

        private static void AddActiveConnection(
            int[] connection,
            int difficulty,
            ICollection<int> activeQuestIds,
            ISet<int> matches)
        {
            if (connection == null
                || connection.Length < 2
                || connection[0] != 0
                || connection[1] <= 0
                || !activeQuestIds.Contains(connection[1]))
            {
                return;
            }

            if (connection.Length >= 3
                && connection[2] >= 0
                && difficulty < connection[2])
            {
                return;
            }

            matches.Add(connection[1]);
        }

        private static (int X, int Y)? ResolveEndpoint(
            MazeInfo maze,
            int[] bossPosition)
        {
            if (bossPosition != null && bossPosition.Length >= 2)
                return (bossPosition[0], bossPosition[1]);
            if (maze?.BossMap != null && maze.BossMap.Length >= 2)
                return (maze.BossMap[0], maze.BossMap[1]);
            return null;
        }

        private static bool TryResolveEndpointMapId(
            MazeInfo maze,
            int endpointX,
            int endpointY,
            int selectedBossMapId,
            out int mapId,
            out string reason)
        {
            mapId = 0;
            reason = string.Empty;
            if (selectedBossMapId > 0)
            {
                mapId = selectedBossMapId;
                return true;
            }

            var candidates = new HashSet<int>();
            foreach (var specification in
                maze?.MapSpecifications ?? new List<MapSpecificationItem>())
            {
                if (specification == null
                    || specification.X != endpointX
                    || specification.Y != endpointY)
                {
                    continue;
                }

                if (specification.Index > 0)
                    candidates.Add(specification.Index);
                if (specification.MapCandidates != null)
                {
                    foreach (var candidate in specification.MapCandidates)
                        if (candidate > 0)
                            candidates.Add(candidate);
                }
                if (specification.LayeredMapIds != null)
                {
                    foreach (var candidate in specification.LayeredMapIds)
                        if (candidate > 0)
                            candidates.Add(candidate);
                }
            }

            if (candidates.Count != 1)
            {
                reason = candidates.Count == 0
                    ? "Boss endpoint has no explicit map specification"
                    : $"Boss endpoint map specification is ambiguous ({candidates.Count})";
                return false;
            }

            mapId = candidates.First();
            return true;
        }

        private static bool ContainsToken(
            IEnumerable<string> values,
            string expected)
            => values != null && values.Any(
                value => string.Equals(
                    value?.Trim(),
                    expected,
                    StringComparison.OrdinalIgnoreCase));

        private static Resolution Failure(string reason)
            => new Resolution { Reason = reason ?? "unknown resolution failure" };

        private static string GetDirectory(string path)
        {
            var slash = path?.LastIndexOf('/') ?? -1;
            return slash > 0 ? path.Substring(0, slash) : string.Empty;
        }

        private static string NormalizePvfPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = new List<string>();
            foreach (var part in path.Replace('\\', '/').Split('/'))
            {
                if (string.IsNullOrWhiteSpace(part) || part == ".")
                    continue;
                if (part == "..")
                {
                    if (normalized.Count == 0)
                        return null;
                    normalized.RemoveAt(normalized.Count - 1);
                    continue;
                }

                normalized.Add(part);
            }

            return normalized.Count == 0 ? null : string.Join("/", normalized);
        }
    }
}
