using System;
using System.Collections.Generic;
using DfoServer.Game.DeathTower;

namespace DfoServer.Network.Builders
{
    // Current CN A21 DNF.exe SHA256 1F8244D3...B00DD.
    // 0114E590 reads u32 dungeon, u16 floors, u8 mode, u8 buff.
    // 0115BA20 reads u16 floor, u32 seed, u32 map, u8 actors and 14B actors.
    // Its scene-item rows differ from DeathTower. Boss floors use the normal
    // authoritative drop pipeline, so the initial scene-item count is zero.
    internal static class BossTowerPacketBuilder
    {
        internal static byte[] BuildInfo(DeathTowerSession tower) =>
            DeathTowerPacketBuilder.BuildTowerInfo(
                tower.Config.DungeonId, checked((ushort)tower.Config.TotalStages));

        internal static byte[] BuildMap(
            DeathTowerSession tower, List<StageMonster> monsters, uint seed) =>
            DeathTowerPacketBuilder.BuildStageMap(
                tower, monsters, Array.Empty<StageTowerItem>(), seed);

        // 01147910 consumes eight groups, each u8 count + {u32 item,u32 value}.
        internal static byte[] BuildReward(int experience, IReadOnlyList<DeathTowerRewardCandidate> candidates) =>
            DeathTowerPacketBuilder.BuildReward(experience, candidates, participantGroupCount: 8);
    }
}
