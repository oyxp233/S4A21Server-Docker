using System;
using System.Collections.Generic;
using DfoServer.Game.DeathTower;

namespace DfoServer.SelfTests
{
    internal static class DeathTowerSelfTestFactory
    {
        public static DeathTowerData.TowerConfig CreateConfig(
            int dungeonId,
            IReadOnlyList<int> stageMapIds,
            int basisLevel,
            int maxClearItemCount = 10,
            bool itemDropsEnabled = true,
            DeathTowerRewardProfile rewardProfile =
                DeathTowerRewardProfile.Standard,
            bool? usesFpCubePiece = null,
            bool? limitsStackableItems = null)
        {
            var standardTower = rewardProfile == DeathTowerRewardProfile.Standard;
            return new DeathTowerData.TowerConfig(
                dungeonId,
                stageMapIds,
                basisLevel,
                maxClearItemCount,
                itemDropsEnabled,
                usesFpCubePiece ?? standardTower,
                limitsStackableItems ?? standardTower,
                rewardProfile,
                Array.Empty<DeathTowerData.TowerEntryItem>(),
                Array.Empty<DeathTowerData.TowerEntryItem>());
        }
    }
}
