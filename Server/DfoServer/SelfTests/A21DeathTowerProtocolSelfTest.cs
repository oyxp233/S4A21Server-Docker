using System;
using System.Collections.Generic;
using DfoServer.Game.DeathTower;
using DfoServer.Network.Builders;

namespace DfoServer.SelfTests
{
    public static class A21DeathTowerProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_DEATH_TOWER_PROTOCOL selftest ===");
            var failures = 0;
            var config = new DeathTowerData.TowerConfig(
                11000,
                new[] { 30001 },
                60,
                10,
                itemDropsEnabled: true,
                usesFpCubePiece: false,
                limitsStackableItems: false,
                rewardProfile: DeathTowerRewardProfile.Standard,
                requiredEntryItems: Array.Empty<DeathTowerData.TowerEntryItem>(),
                addedRequiredEntryItems: Array.Empty<DeathTowerData.TowerEntryItem>());
            var tower = new DeathTowerSession(config);
            var monsters = new List<StageMonster>
            {
                new StageMonster
                {
                    ListIndex = 7,
                    MonsterUniqueId = 3,
                    MonsterIndex = 55001,
                    MonsterLevel = 50,
                    MonsterType = 2,
                    IsBoxMonster = 1,
                    BoxIndex = 4,
                },
            };
            var items = new[]
            {
                new StageTowerItem
                {
                    SourceListIndex = 7,
                    SourceMonsterUniqueId = 3,
                    ItemUniqueId = 9,
                    ItemId = 3037,
                    DropRate = 10000,
                    StackCount = 2,
                },
            };
            const uint seed = 0x12345678;

            var body = DeathTowerPacketBuilder.BuildStageMap(
                tower,
                monsters,
                items,
                seed);

            Check("A21 stage-map body keeps the 11-byte header", body.Length == 44, ref failures);
            Check("stage is a 1-based UInt16", ReadUInt16(body, 0) == 1, ref failures);
            Check("seed follows the stage", ReadUInt32(body, 2) == seed, ref failures);
            Check("A21 map id is UInt32", ReadUInt32(body, 6) == 30001, ref failures);
            Check("monster count follows the 4-byte map id", body[10] == 1, ref failures);
            Check(
                "monster rows remain aligned after the A21 header",
                ReadUInt32(body, 11) == 7
                && ReadUInt16(body, 15) == 3
                && ReadUInt32(body, 17) == 55001
                && body[21] == 50
                && body[22] == 2
                && body[23] == 1
                && body[24] == 4,
                ref failures);
            Check(
                "item rows remain aligned after the monster list",
                body[25] == 1
                && ReadUInt32(body, 26) == 7
                && ReadUInt16(body, 30) == 9
                && ReadUInt32(body, 32) == 3037
                && ReadUInt32(body, 36) == 10000
                && ReadUInt32(body, 40) == 2,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_DEATH_TOWER_PROTOCOL selftest passed."
                    : $"A21_DEATH_TOWER_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static ushort ReadUInt16(byte[] data, int offset)
            => BitConverter.ToUInt16(data, offset);

        private static uint ReadUInt32(byte[] data, int offset)
            => BitConverter.ToUInt32(data, offset);

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
