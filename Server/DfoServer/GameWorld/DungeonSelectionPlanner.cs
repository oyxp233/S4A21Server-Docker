using System;
using System.Collections.Generic;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal static class DungeonSelectionPlanner
    {
        internal static (MazeInfo Maze, int Index) SelectMaze(
            int dungeonId,
            int difficulty,
            ICollection<int> activeQuestIds,
            ICollection<int> clearedQuestIds,
            Action<string> diagnosticSink)
        {
            var dungeon = Dungeon.GetDungeonFile(dungeonId);
            if (dungeon?.Mazes == null || dungeon.Mazes.Count == 0)
                throw new Exception("未解析到迷宫信息");

            var doingMatch = FindQuestConnectedMazeIndex(
                dungeon.Mazes,
                activeQuestIds,
                requiredQuestType: 0,
                difficulty);
            if (doingMatch >= 0)
            {
                diagnosticSink?.Invoke(BuildDiagnostic(
                    dungeon.Mazes,
                    activeQuestIds,
                    clearedQuestIds,
                    difficulty,
                    doingMatch,
                    "active_quest"));
                return (dungeon.Mazes[doingMatch], doingMatch);
            }

            var clearedMatch = FindQuestConnectedMazeIndex(
                dungeon.Mazes,
                clearedQuestIds,
                requiredQuestType: 1,
                difficulty);
            if (clearedMatch >= 0)
            {
                diagnosticSink?.Invoke(BuildDiagnostic(
                    dungeon.Mazes,
                    activeQuestIds,
                    clearedQuestIds,
                    difficulty,
                    clearedMatch,
                    "cleared_quest"));
                return (dungeon.Mazes[clearedMatch], clearedMatch);
            }

            var candidates = new List<(MazeInfo Maze, int Index)>();
            for (var index = 0; index < dungeon.Mazes.Count; index++)
            {
                if (dungeon.Mazes[index].QuestConnection == null)
                    candidates.Add((dungeon.Mazes[index], index));
            }

            if (candidates.Count == 0)
            {
                diagnosticSink?.Invoke(BuildDiagnostic(
                    dungeon.Mazes,
                    activeQuestIds,
                    clearedQuestIds,
                    difficulty,
                    0,
                    "no_ordinary_maze_fallback"));
                return (dungeon.Mazes[0], 0);
            }

            var selected = candidates[Infrastructure.ServerRandom.Next(candidates.Count)];
            diagnosticSink?.Invoke(BuildDiagnostic(
                dungeon.Mazes,
                activeQuestIds,
                clearedQuestIds,
                difficulty,
                selected.Index,
                "ordinary_maze"));
            return selected;
        }

        internal static bool IsQuestConnected(
            int dungeonId,
            MazeInfo maze,
            ICollection<int> activeQuestIds,
            int difficulty)
        {
            if (maze?.QuestConnection != null
                && maze.QuestConnection.Length >= 2)
            {
                return true;
            }

            try
            {
                var connection = Dungeon.GetDungeonFile(dungeonId)?.QuestConnection;
                return connection != null
                    && connection.Length >= 2
                    && connection[0] == 0
                    && connection[1] > 0
                    && activeQuestIds != null
                    && activeQuestIds.Contains(connection[1])
                    && (connection.Length < 3
                        || connection[2] < 0
                        || difficulty >= connection[2]);
            }
            catch
            {
                return false;
            }
        }

        private static int FindQuestConnectedMazeIndex(
            IReadOnlyList<MazeInfo> mazes,
            ICollection<int> questIds,
            int requiredQuestType,
            int difficulty)
        {
            if (mazes == null || questIds == null || questIds.Count == 0)
                return -1;

            var candidates = new List<int>();
            for (var index = 0; index < mazes.Count; index++)
            {
                var connection = mazes[index].QuestConnection;
                if (connection == null || connection.Length < 2)
                    continue;
                if (connection[0] != requiredQuestType)
                    continue;
                if (!questIds.Contains(connection[1]))
                    continue;
                if (requiredQuestType == 0
                    && connection.Length >= 3
                    && connection[2] >= 0
                    && difficulty < connection[2])
                {
                    continue;
                }

                candidates.Add(index);
            }

            if (candidates.Count == 0)
                return -1;
            if (candidates.Count == 1)
                return candidates[0];
            return candidates[Infrastructure.ServerRandom.Next(candidates.Count)];
        }

        private static string BuildDiagnostic(
            IReadOnlyList<MazeInfo> mazes,
            ICollection<int> activeQuestIds,
            ICollection<int> clearedQuestIds,
            int difficulty,
            int selectedMazeIndex,
            string reason)
        {
            var connections = new List<string>();
            for (var index = 0; index < mazes.Count; index++)
            {
                var connection = mazes[index].QuestConnection;
                if (connection == null)
                    continue;
                if (connection.Length < 2)
                {
                    connections.Add($"m{index}:invalid");
                    continue;
                }

                var questType = connection[0];
                var questId = connection[1];
                var minimumDifficulty = connection.Length >= 3 ? connection[2] : -1;
                var isActive = activeQuestIds != null && activeQuestIds.Contains(questId);
                var isCleared = clearedQuestIds != null && clearedQuestIds.Contains(questId);
                var stateMatches = questType == 0
                    ? isActive
                    : questType == 1 && isCleared;
                var difficultyMatches = questType != 0
                    || minimumDifficulty < 0
                    || difficulty >= minimumDifficulty;
                var result = questType != 0 && questType != 1
                    ? "unsupported_type"
                    : !stateMatches
                        ? "quest_state_miss"
                        : !difficultyMatches
                            ? "difficulty_miss"
                            : "eligible";

                connections.Add(
                    $"m{index}:type={questType},quest={questId},min={minimumDifficulty}," +
                    $"active={(isActive ? 1 : 0)},cleared={(isCleared ? 1 : 0)},result={result}");
            }

            return
                $"difficulty={difficulty} active={FormatQuestIds(activeQuestIds)} " +
                $"clearedCount={clearedQuestIds?.Count ?? 0} " +
                $"connections=[{string.Join(";", connections)}] " +
                $"selectedMaze={selectedMazeIndex} reason={reason}";
        }

        private static string FormatQuestIds(ICollection<int> questIds)
        {
            if (questIds == null || questIds.Count == 0)
                return "[]";

            const int maxLoggedQuestIds = 64;
            var sorted = new List<int>(questIds);
            sorted.Sort();
            var values = new List<string>();
            var count = Math.Min(sorted.Count, maxLoggedQuestIds);
            for (var index = 0; index < count; index++)
                values.Add(sorted[index].ToString());
            if (sorted.Count > count)
                values.Add($"...(+{sorted.Count - count})");
            return $"[{string.Join(",", values)}]";
        }
    }
}
