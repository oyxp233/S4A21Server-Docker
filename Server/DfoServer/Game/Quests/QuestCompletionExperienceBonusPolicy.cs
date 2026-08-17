using System;
using DfoServer.Game.Dungeon;

namespace DfoServer.Game.Quests
{
    internal readonly struct QuestCompletionExperienceBonusSnapshot
    {
        internal QuestCompletionExperienceBonusSnapshot(
            ushort questId,
            int ratePercent,
            short dungeonId,
            byte difficulty,
            long runId,
            long runGeneration)
        {
            QuestId = questId;
            RatePercent = ratePercent;
            DungeonId = dungeonId;
            Difficulty = difficulty;
            RunId = runId;
            RunGeneration = runGeneration;
        }

        internal ushort QuestId { get; }
        internal int RatePercent { get; }
        internal short DungeonId { get; }
        internal byte Difficulty { get; }
        internal long RunId { get; }
        internal long RunGeneration { get; }
        internal bool IsEligible => QuestId > 0 && RatePercent > 0;

        internal bool Matches(ushort questId) =>
            IsEligible && QuestId == questId;
    }

    internal static class QuestCompletionExperienceBonusPolicy
    {
        internal static QuestCompletionExperienceBonusSnapshot Capture(
            DungeonRun run,
            int playerLevel)
        {
            if (run == null || playerLevel <= 0)
                return default;

            short dungeonId;
            byte difficulty;
            int activeQuestId;
            long runId;
            long runGeneration;
            QuestRunSnapshot questSnapshot;
            lock (run.SyncRoot)
            {
                dungeonId = run.DungeonId;
                difficulty = run.Difficulty;
                activeQuestId = run.ActiveQuestMazeQuestId;
                runId = run.RunId;
                runGeneration = run.RunGeneration;
                questSnapshot = run.QuestSnapshot;
            }

            if (dungeonId <= 0
                || activeQuestId <= 0
                || activeQuestId > ushort.MaxValue)
            {
                return default;
            }

            var questId = (ushort)activeQuestId;
            if (questSnapshot?.Contains(questId) != true
                || !GameWorld.Dungeon.IsSuitableLevelDungeon(
                    dungeonId,
                    playerLevel))
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
                return ratePercent > 0
                    ? new QuestCompletionExperienceBonusSnapshot(
                        questId,
                        ratePercent,
                        dungeonId,
                        difficulty,
                        runId,
                        runGeneration)
                    : default;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestCompletionExperienceBonusPolicy] capture failed: " +
                    $"quest={questId} dungeon={dungeonId} " +
                    $"difficulty={difficulty} error={ex.Message}");
                return default;
            }
        }

        internal static bool TryApply(
            ref GameWorld.QuestReward reward,
            ushort completedQuestId,
            string questGrade,
            GameWorld.QuestRewardKind rewardKind,
            QuestCompletionExperienceBonusSnapshot snapshot,
            out string error)
        {
            error = string.Empty;
            if (!snapshot.Matches(completedQuestId)
                || reward.Exp == 0
                || rewardKind == GameWorld.QuestRewardKind.CircleDungeon
                || !string.Equals(
                    questGrade,
                    "epic",
                    StringComparison.Ordinal))
            {
                return true;
            }

            var multiplier = 100UL + (uint)snapshot.RatePercent;
            var adjustedExperience = (ulong)reward.Exp * multiplier / 100UL;
            if (adjustedExperience > uint.MaxValue)
            {
                error =
                    $"mainline quest EXP exceeds uint32: base={reward.Exp} " +
                    $"rate={snapshot.RatePercent}";
                return false;
            }

            reward.Exp = (uint)adjustedExperience;
            return true;
        }
    }
}
