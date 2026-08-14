using PvfLib;
using System;
using System.Linq;

namespace DfoServer.GameWorld
{
    internal sealed class DungeonPermissionDefinition
    {
        internal DungeonPermissionDefinition(
            int dungeonId,
            string filePath,
            bool isTaskExclusive,
            bool hasWorldMapReference,
            bool hasExplicitDifficultyConfiguration)
        {
            DungeonId = dungeonId;
            FilePath = filePath ?? string.Empty;
            IsTaskExclusive = isTaskExclusive;
            HasWorldMapReference = hasWorldMapReference;
            HasExplicitDifficultyConfiguration =
                hasExplicitDifficultyConfiguration;
        }

        internal int DungeonId { get; }

        internal string FilePath { get; }

        internal bool IsTaskExclusive { get; }

        internal bool HasWorldMapReference { get; }

        internal bool HasExplicitDifficultyConfiguration { get; }
    }

    internal static class DungeonPermissionDefinitionResolver
    {
        internal static bool TryResolve(
            int dungeonId,
            out DungeonPermissionDefinition definition,
            out string failureReason)
        {
            definition = null;
            failureReason = string.Empty;
            if (dungeonId <= 0 || dungeonId > ushort.MaxValue)
            {
                failureReason = "dungeon id is outside the protocol range";
                return false;
            }

            try
            {
                var loaded = Dungeon.LoadDungeonFileWithPath(dungeonId);
                var file = loaded.File;
                if (file == null)
                {
                    failureReason = "dungeon definition is null";
                    return false;
                }

                var filePath = NormalizePath(loaded.FilePath);
                if (string.IsNullOrWhiteSpace(filePath)
                    || filePath.Any(char.IsControl))
                {
                    failureReason = "dungeon definition path is empty or malformed";
                    return false;
                }

                if (file.Root == null
                    || string.IsNullOrWhiteSpace(file.Content)
                    || file.Mazes == null
                    || file.Mazes.Count == 0)
                {
                    failureReason = "dungeon definition is incomplete";
                    return false;
                }

                var hasWorldMapReference = WorldMap.TryGetAdmissionDefinition(
                    dungeonId,
                    out var admission);
                definition = new DungeonPermissionDefinition(
                    dungeonId,
                    filePath,
                    hasWorldMapReference
                        ? admission.IsTaskExclusive
                        : WorldMap.IsTaskExclusiveDungeon(dungeonId),
                    hasWorldMapReference,
                    HasExplicitDifficultyConfiguration(file));
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        private static string NormalizePath(string filePath) =>
            (filePath ?? string.Empty)
                .Replace('\\', '/')
                .Trim()
                .TrimStart('/');

        private static bool HasExplicitDifficultyConfiguration(
            DungeonFile file)
        {
            if (file.DifficultyLevel != null
                && file.DifficultyLevel.Any(value => value != 0))
            {
                return true;
            }

            return (file.DesignateDungeonDifficulty != null
                    && file.DesignateDungeonDifficulty.Length > 0)
                || file.Difficulty >= 0;
        }
    }
}
