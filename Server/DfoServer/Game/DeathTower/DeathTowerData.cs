using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DfoServer.GameWorld;
using PvfLib;
using DungeonWorld = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.DeathTower
{
    // Builds the immutable tower definition from the shared DungeonCatalog.
    // The runtime session owns only stage progress, temporary items and combat state.
    public static class DeathTowerData
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<int, TowerConfig> Cache =
            new Dictionary<int, TowerConfig>();

        public readonly struct TowerEntryItem
        {
            public TowerEntryItem(int itemId, int count, bool consumeOnEntry)
            {
                ItemId = itemId;
                Count = count;
                ConsumeOnEntry = consumeOnEntry;
            }

            public int ItemId { get; }
            public int Count { get; }
            public bool ConsumeOnEntry { get; }
        }

        public sealed class TowerConfig
        {
            internal TowerConfig(
                int dungeonId,
                IReadOnlyList<int> stageMapIds,
                int basisLevel,
                int maxClearItemCount,
                bool itemDropsEnabled,
                bool usesFpCubePiece,
                bool limitsStackableItems,
                DeathTowerRewardProfile rewardProfile,
                IReadOnlyList<TowerEntryItem> requiredEntryItems,
                IReadOnlyList<TowerEntryItem> addedRequiredEntryItems)
            {
                if (dungeonId <= 0)
                    throw new ArgumentOutOfRangeException(nameof(dungeonId));
                if (stageMapIds == null || stageMapIds.Count == 0)
                    throw new ArgumentException("A tower requires at least one stage.", nameof(stageMapIds));
                if (basisLevel <= 0 || basisLevel > byte.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(basisLevel));
                if (maxClearItemCount < 0)
                    throw new ArgumentOutOfRangeException(nameof(maxClearItemCount));

                DungeonId = dungeonId;
                StageMapIds = Freeze(stageMapIds);
                BasisLevel = basisLevel;
                MaxClearItemCount = maxClearItemCount;
                ItemDropsEnabled = itemDropsEnabled;
                UsesFpCubePiece = usesFpCubePiece;
                LimitsStackableItems = limitsStackableItems;
                RewardProfile = rewardProfile;
                RequiredEntryItems = Freeze(requiredEntryItems);
                AddedRequiredEntryItems = Freeze(addedRequiredEntryItems);
            }

            public int DungeonId { get; }
            public int TotalStages => StageMapIds.Count;
            public IReadOnlyList<int> StageMapIds { get; }
            public int BasisLevel { get; }
            public int MaxClearItemCount { get; }
            public bool ItemDropsEnabled { get; }
            public bool UsesFpCubePiece { get; }
            public bool LimitsStackableItems { get; }
            public DeathTowerRewardProfile RewardProfile { get; }
            public IReadOnlyList<TowerEntryItem> RequiredEntryItems { get; }
            public IReadOnlyList<TowerEntryItem> AddedRequiredEntryItems { get; }

            private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source)
            {
                if (source == null || source.Count == 0)
                    return Array.Empty<T>();

                var copy = new T[source.Count];
                for (var index = 0; index < source.Count; index++)
                    copy[index] = source[index];
                return new ReadOnlyCollection<T>(copy);
            }
        }

        public static bool IsDeathTower(int dungeonId) => GetConfig(dungeonId) != null;

        public static TowerConfig GetConfig(int dungeonId)
        {
            lock (Sync)
            {
                if (Cache.TryGetValue(dungeonId, out var cached))
                    return cached;

                var config = TryLoadFromCatalog(dungeonId);
                Cache[dungeonId] = config;
                return config;
            }
        }

        private static TowerConfig TryLoadFromCatalog(int dungeonId)
        {
            try
            {
                var dungeon = DungeonWorld.GetDungeonFile(dungeonId);
                if (dungeon == null
                    || !string.Equals(
                        dungeon.DungeonType,
                        "tower of death",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (dungeon.DeathTowerMapIndexesMalformed
                    || dungeon.DeathTowerStages == null
                    || dungeon.DeathTowerStages.Count == 0)
                {
                    FileLogger.Log(
                        $"[DeathTower] definition rejected: dungeon={dungeonId} " +
                        "death tower map indexes are missing or malformed");
                    return null;
                }

                var mapIds = new List<int>(dungeon.DeathTowerStages.Count);
                for (var index = 0; index < dungeon.DeathTowerStages.Count; index++)
                {
                    var stage = dungeon.DeathTowerStages[index];
                    if (stage == null || stage.Stage != index + 1 || stage.MapId <= 0)
                    {
                        FileLogger.Log(
                            $"[DeathTower] definition rejected: dungeon={dungeonId} " +
                            $"invalid stage at index={index}");
                        return null;
                    }
                    mapIds.Add(stage.MapId);
                }

                if (dungeon.BasisLevel <= 0 || dungeon.BasisLevel > byte.MaxValue)
                {
                    FileLogger.Log(
                        $"[DeathTower] definition rejected: dungeon={dungeonId} " +
                        $"invalid basis level={dungeon.BasisLevel}");
                    return null;
                }

                var maxClearItemCount = dungeon.TowerMaxClearItemNum >= 0
                    ? dungeon.TowerMaxClearItemNum
                    : 10;
                if (!TryResolveRewardProfile(
                        dungeon.TowerLimitOfStackableItem,
                        out var rewardProfile))
                {
                    FileLogger.Log(
                        $"[DeathTower] definition rejected: dungeon={dungeonId} " +
                        $"unknown reward profile key=" +
                        $"{dungeon.TowerLimitOfStackableItem}");
                    return null;
                }
                var config = new TowerConfig(
                    dungeonId,
                    mapIds,
                    dungeon.BasisLevel,
                    maxClearItemCount,
                    itemDropsEnabled: dungeon.TowerItemDrop != 0,
                    usesFpCubePiece: dungeon.TowerFpCubepiece == 1,
                    limitsStackableItems:
                        dungeon.TowerLimitOfStackableItem == 1,
                    rewardProfile,
                    ProjectEntryItems(dungeon.RequiredItems),
                    ProjectEntryItems(dungeon.AddedRequiredItems));

                FileLogger.Log(
                    $"[DeathTower] definition loaded: dungeon={dungeonId} " +
                    $"stages={config.TotalStages} basisLv={config.BasisLevel} " +
                    $"maxClearItems={config.MaxClearItemCount} " +
                    $"towerItems={config.ItemDropsEnabled} " +
                    $"fpCubePiece={config.UsesFpCubePiece} " +
                    $"limitStackable={config.LimitsStackableItems} " +
                    $"rewardProfile={config.RewardProfile} " +
                    $"required={config.RequiredEntryItems.Count} " +
                    $"addedRequired={config.AddedRequiredEntryItems.Count} " +
                    $"firstMap={config.StageMapIds[0]} " +
                    $"lastMap={config.StageMapIds[config.StageMapIds.Count - 1]}");
                return config;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DeathTower] definition load failed for dungeon {dungeonId}: {ex.Message}");
                return null;
            }
        }

        private static IReadOnlyList<TowerEntryItem> ProjectEntryItems(
            IReadOnlyList<DungeonRequiredItem> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<TowerEntryItem>();

            var result = new List<TowerEntryItem>(source.Count);
            foreach (var item in source)
            {
                if (item == null || item.ItemId <= 0 || item.Count <= 0)
                    continue;
                result.Add(new TowerEntryItem(
                    item.ItemId,
                    item.Count,
                    item.ConsumeOnEntry));
            }
            return result;
        }

        private static bool TryResolveRewardProfile(
            int towerLimitOfStackableItem,
            out DeathTowerRewardProfile profile)
        {
            switch (towerLimitOfStackableItem)
            {
                case 1:
                    profile = DeathTowerRewardProfile.Standard;
                    return true;
                case 0:
                    profile = DeathTowerRewardProfile.Illusion;
                    return true;
                default:
                    profile = default;
                    return false;
            }
        }
    }
}
