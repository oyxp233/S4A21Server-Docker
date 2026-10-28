using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class BossTowerSelfTest
    {
        internal static int Run()
        {
            var failures = 0;
            void Check(string name, bool condition)
            {
                Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
                if (!condition) failures++;
            }

            var definition = Dungeon.GetDungeonFile(3505);
            var config = DeathTowerData.GetConfig(3505);
            Check("boss tower retains all eight repeated PVF stage sections",
                definition.DungeonType == "tower of boss" && definition.Mazes.Count == 0
                && !definition.RandomTowerStagesMalformed && definition.RandomTowerStages.Count == 8);
            Check("boss tower projects its own reward profile and consumable PVF ticket",
                config?.IsBossTower == true && config.TotalStages == 8
                && config.RequiredEntryItems.Count == 1 && config.RequiredEntryItems[0].ItemId == 2749110
                && config.RequiredEntryItems[0].Count == 1 && config.RequiredEntryItems[0].ConsumeOnEntry);
            if (config == null) return 1;

            var first = config.ResolveStageMaps(_ => 0);
            var last = config.ResolveStageMaps(maximum => maximum - 1);
            Check("stage selection respects every weighted interval including rare maps",
                first[0] == 25044 && first[1] == 25045 && last[1] == 25048
                && first[7] == 25071 && last[7] == 25074);
            var pool = new DeathTowerData.StageMapPool(new[]
            {
                new TowerMapCandidate { MapId = 101, Weight = 3 },
                new TowerMapCandidate { MapId = 102, Weight = 1 },
            });
            Check("weight boundaries use cumulative probabilities without an off-by-one",
                pool.Select(_ => 0) == 101 && pool.Select(_ => 2) == 101 && pool.Select(_ => 3) == 102);
            var invalidRollRejected = false;
            try { pool.Select(maximum => maximum); }
            catch (ArgumentOutOfRangeException) { invalidRollRejected = true; }
            Check("invalid RNG output cannot select an out-of-range map", invalidRollRejected);
            var malformed = DungeonFile.Parse("[tower stage]\n1 25044 0\n[/tower stage]\n");
            Check("invalid map weights are diagnosed rather than silently repaired", malformed.RandomTowerStagesMalformed);

            var tower = new DeathTowerSession(config, new ushort[] { 1 });
            Check("a fresh boss tower grants no unearned cleared floor", tower.ClearedStageCount == 0);
            var frozenMap = tower.GetCurrentMapId();
            Check("a chosen floor stays frozen across repeated reads", Enumerable.Range(0, 20).All(_ => tower.GetCurrentMapId() == frozenMap));
            tower.SetFighting();
            Check("fighting alone cannot skip a boss floor", !tower.TryAdvanceStage());
            var combat = new DungeonRun
            {
                Tower = tower,
                RoomMonsters = new List<Dungeon.MonsterSumInfo>
                {
                    new Dungeon.MonsterSumInfo { PacketIndex = 2, IsBlocking = true },
                    new Dungeon.MonsterSumInfo { PacketIndex = 3, IsBlocking = false },
                },
                RoomKilledSeqIds = new HashSet<ushort> { 999 },
            };
            Check("unknown actor deaths cannot authorize a boss-floor clear",
                !DeathTowerCoordinator.IsBossStageCleared(combat));
            using (var socket = new TcpClient())
            {
                var session = new EnhancedClientSession(socket, new GamePacketHeader());
                session.Player.CurrentRun = combat;
                Check("premature MOVE_MAP is handled without loading or clearing another floor",
                    new DeathTowerCoordinator().TryHandleMoveMap(session).GetAwaiter().GetResult()
                    && tower.CurrentStage == 0 && tower.State == 1);
                session.Player.CurrentRun = null;
            }
            combat.RoomKilledSeqIds.Add(2);
            Check("boss-floor clear is derived from all authoritative blocking actors",
                DeathTowerCoordinator.IsBossStageCleared(combat));
            var everyMapLoads = true;
            foreach (var candidateMap in definition.RandomTowerStages.SelectMany(stage => stage.Maps).Select(candidate => candidate.MapId).Distinct())
            {
                var mapConfig = new DeathTowerData.TowerConfig(3505, new[] { candidateMap },
                    config.BasisLevel, config.MaxClearItemCount, false, false, false,
                    DeathTowerRewardProfile.Boss, Array.Empty<DeathTowerData.TowerEntryItem>(),
                    Array.Empty<DeathTowerData.TowerEntryItem>());
                var mapTower = new DeathTowerSession(mapConfig, new ushort[] { 1 });
                everyMapLoads &= DeathTowerMapLoader.LoadStageMonsters(mapTower).Count > 0;
            }
            Check("every ordinary and rare boss-floor map loads real combat actors", everyMapLoads);
            var info = BossTowerPacketBuilder.BuildInfo(tower);
            Check("boss INFO matches the eight-byte client reader", info.Length == 8
                && BitConverter.ToInt32(info, 0) == 3505 && BitConverter.ToUInt16(info, 4) == 8);
            var monsters = new List<StageMonster>
            {
                new StageMonster { ListIndex = 0, MonsterIndex = 12345, MonsterLevel = 60, MonsterType = 3 },
            };
            tower.AssignMonsterSequences(monsters);
            var map = BossTowerPacketBuilder.BuildMap(tower, monsters, 0x12345678);
            Check("boss MAP uses its selected map, collision-free actor UID and empty scene-item section",
                map.Length == 26 && BitConverter.ToUInt16(map, 0) == 1
                && BitConverter.ToUInt32(map, 2) == 0x12345678
                && BitConverter.ToInt32(map, 6) == frozenMap && map[10] == 1
                && BitConverter.ToUInt16(map, 15) != 1 && map[25] == 0);
            for (var stage = 0; stage < 8; stage++)
            {
                tower.SetCleared();
                tower.SetFighting();
                Check($"boss floor {stage + 1} clear is stable against a late fight-start packet",
                    tower.State == 2 && tower.ClearedStageCount == stage + 1);
                if (stage < 7) Check($"boss floor {stage + 1} advances exactly once", tower.TryAdvanceStage());
            }
            Check("boss tower stops at its eighth floor", tower.IsLastStage && !tower.TryAdvanceStage());

            var rewardConfig = DeathTowerRewardConfig.Load(DeathTowerRewardProfile.Boss);
            Check("boss rewards use global_bosstower rather than death-tower rates",
                rewardConfig.IsAvailable && rewardConfig.GoldAmountWeight == 11
                && rewardConfig.GetExpWeight(DeathTowerRewardProfile.Boss, 8) == 4
                && rewardConfig.GetRewardCardCount(DeathTowerRewardProfile.Boss, 8) == 10);
            var reward = BossTowerPacketBuilder.BuildReward(123,
                new[] { DeathTowerRewardCandidate.Item(111, 2) });
            Check("boss reward includes all eight participant groups required by the client",
                reward.Length == 20 && BitConverter.ToInt32(reward, 0) == 123
                && reward[4] == 1 && BitConverter.ToInt32(reward, 5) == 111
                && BitConverter.ToInt32(reward, 9) == 2 && reward.Skip(13).All(value => value == 0));
            Check("death tower keeps its original reward group count",
                DeathTowerPacketBuilder.BuildReward(0, Array.Empty<DeathTowerRewardCandidate>()).Length == 8);
            Console.WriteLine($"BOSS_TOWER checks complete failures={failures}");
            return failures == 0 ? 0 : 1;
        }
    }
}
