using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Concurrent;

namespace DfoServer.GameWorld
{
    internal static class DungeonRewardPolicyData
    {
        private const float InteractiveTrainingExperienceRate = 0.001f;
        private const float ExperienceRateTolerance = 0.000001f;

        private static readonly ConcurrentDictionary<int, DungeonRewardPolicy> Cache =
            new ConcurrentDictionary<int, DungeonRewardPolicy>();

        internal static DungeonRewardPolicy Resolve(int dungeonId)
        {
            if (dungeonId <= 0)
                return DungeonRewardPolicy.Standard;

            if (IsAntonRaidDungeonId(dungeonId))
                return DungeonRewardPolicy.AntonRaid;

            return Cache.GetOrAdd(dungeonId, ResolveUncached);
        }

        private static DungeonRewardPolicy ResolveUncached(int dungeonId)
        {
            string assetPath = null;
            try
            {
                assetPath = Dungeon.LoadDungeonLstFile().GetById(dungeonId)?.FilePath;
                var trainingPath = IsInteractiveTrainingAssetPath(assetPath);
                if (!trainingPath)
                    return DungeonRewardPolicy.Standard;

                var dungeonFile = Dungeon.LoadDungeonFileWithPath(dungeonId).File;
                if (!HasInteractiveTrainingStructure(dungeonFile))
                {
                    FileLogger.Log(
                        $"[DungeonRewardPolicy] training asset has unexpected structure; " +
                        $"rewards disabled fail-closed dungeon={dungeonId} path={assetPath}");
                }

                return DungeonRewardPolicy.InteractiveTraining;
            }
            catch (Exception ex)
            {
                if (IsInteractiveTrainingAssetPath(assetPath))
                {
                    FileLogger.Log(
                        $"[DungeonRewardPolicy] training asset parse failed; " +
                        $"rewards disabled fail-closed dungeon={dungeonId} path={assetPath}: {ex.Message}");
                    return DungeonRewardPolicy.InteractiveTraining;
                }

                FileLogger.Log(
                    $"[DungeonRewardPolicy] ordinary policy resolution failed open " +
                    $"dungeon={dungeonId}: {ex.Message}");
                return DungeonRewardPolicy.Standard;
            }
        }

        internal static bool IsAntonRaidDungeonId(int dungeonId)
        {
            return (dungeonId >= 210 && dungeonId <= 215)
                || (dungeonId >= 218 && dungeonId <= 224);
        }

        internal static bool IsInteractiveTrainingConfiguration(
            string assetPath,
            DungeonFile dungeonFile)
        {
            return IsInteractiveTrainingAssetPath(assetPath)
                && HasInteractiveTrainingStructure(dungeonFile);
        }

        internal static bool IsInteractiveTrainingAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return false;

            var segments = assetPath.Replace('\\', '/').Split('/');
            for (var index = 0; index < segments.Length - 1; index++)
            {
                if (segments[index].EndsWith(
                        "trainingroom",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool HasInteractiveTrainingStructure(DungeonFile dungeonFile)
        {
            if (dungeonFile == null
                || Math.Abs(
                    dungeonFile.ExperienceIncreasingPoint
                    - InteractiveTrainingExperienceRate) > ExperienceRateTolerance
                || dungeonFile.LimitPartyCount != 1
                || dungeonFile.DesignateDungeonDifficulty == null
                || dungeonFile.DesignateDungeonDifficulty.Length != 1
                || dungeonFile.DesignateDungeonDifficulty[0] != 0
                || dungeonFile.Mazes == null
                || dungeonFile.Mazes.Count != 1)
            {
                return false;
            }

            var maze = dungeonFile.Mazes[0];
            return maze != null
                && maze.Width == 1
                && maze.Height == 1
                && IsOrigin(maze.StartMap)
                && IsOrigin(maze.BossMap)
                && (maze.MapSpecifications == null
                    || maze.MapSpecifications.Count == 0);
        }

        private static bool IsOrigin(int[] position)
        {
            return position != null
                && position.Length >= 2
                && position[0] == 0
                && position[1] == 0;
        }
    }
}
