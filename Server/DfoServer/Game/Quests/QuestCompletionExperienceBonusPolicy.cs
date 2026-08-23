using System;
using DfoServer.Game.Dungeon;

namespace DfoServer.Game.Quests
{
    internal readonly struct DungeonStoryExperienceProfileSnapshot
    {
        internal DungeonStoryExperienceProfileSnapshot(
            ushort questId,
            int ratePercent,
            int experienceDifficulty,
            short dungeonId,
            byte difficulty,
            long runId,
            long runGeneration)
        {
            QuestId = questId;
            RatePercent = ratePercent;
            ExperienceDifficulty = experienceDifficulty;
            DungeonId = dungeonId;
            Difficulty = difficulty;
            RunId = runId;
            RunGeneration = runGeneration;
        }

        internal ushort QuestId { get; }
        internal int RatePercent { get; }
        internal int ExperienceDifficulty { get; }
        internal short DungeonId { get; }
        internal byte Difficulty { get; }
        internal long RunId { get; }
        internal long RunGeneration { get; }
        internal bool IsStoryRun => QuestId > 0
            && ExperienceDifficulty >= 0;
        internal bool IsEligible => IsStoryRun && RatePercent > 0;

    }

    internal static class DungeonStoryExperienceProfilePolicy
    {
        internal static DungeonStoryExperienceProfileSnapshot Capture(
            DungeonRun run)
        {
            if (run == null)
                return default;

            short dungeonId;
            byte difficulty;
            int activeQuestId;
            long runId;
            long runGeneration;
            QuestRunSnapshot questSnapshot;
            GameWorld.DungeonExperienceDefinition experienceDefinition;
            lock (run.SyncRoot)
            {
                dungeonId = run.DungeonId;
                difficulty = run.Difficulty;
                activeQuestId = run.ActiveQuestMazeQuestId;
                runId = run.RunId;
                runGeneration = run.RunGeneration;
                questSnapshot = run.QuestSnapshot;
                experienceDefinition = run.ExperienceDefinition;
            }

            if (dungeonId <= 0
                || activeQuestId <= 0
                || activeQuestId > ushort.MaxValue)
            {
                return default;
            }

            var questId = (ushort)activeQuestId;
            if (questSnapshot?.Contains(questId) != true)
            {
                return default;
            }

            try
            {
                var quest = GameWorld.QuestData.GetQuestFile(questId);
                if (!string.Equals(
                        GameWorld.QuestData.NormalizeQuestTag(quest?.Grade),
                        "epic",
                        StringComparison.Ordinal))
                {
                    return default;
                }

                var storyMode = GameWorld.Dungeon
                    .GetDungeonFile(dungeonId)
                    ?.StoryMode;
                if (storyMode == null
                    || storyMode.QuestIds == null
                    || !storyMode.QuestIds.Contains(questId))
                {
                    return default;
                }

                var difficultyIndex = (int)difficulty;
                if ((storyMode.DifficultySize > 0
                        && difficultyIndex >= storyMode.DifficultySize)
                    || storyMode.IncreaseExperienceRates == null
                    || difficultyIndex >= storyMode.IncreaseExperienceRates.Length)
                {
                    return default;
                }

                var ratePercent = storyMode
                    .IncreaseExperienceRates[difficultyIndex];
                var experienceDifficulty = DungeonExperienceCalculator
                    .ResolveStoryModeExperienceDifficulty(
                        difficultyIndex,
                        experienceDefinition);
                return ratePercent >= 0
                    ? new DungeonStoryExperienceProfileSnapshot(
                        questId,
                        ratePercent,
                        experienceDifficulty,
                        dungeonId,
                        difficulty,
                        runId,
                        runGeneration)
                    : default;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonStoryExperienceProfilePolicy] capture failed: " +
                    $"quest={questId} dungeon={dungeonId} " +
                    $"difficulty={difficulty} error={ex.Message}");
                return default;
            }
        }
    }
}
