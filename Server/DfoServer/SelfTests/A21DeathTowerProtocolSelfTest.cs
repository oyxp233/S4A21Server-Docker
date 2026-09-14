using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Dungeon;
using DfoServer.Network.Builders;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;

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
                new[] { 30001, 30002, 30003 },
                60,
                10,
                itemDropsEnabled: true,
                usesFpCubePiece: false,
                limitsStackableItems: false,
                rewardProfile: DeathTowerRewardProfile.Standard,
                requiredEntryItems: Array.Empty<DeathTowerData.TowerEntryItem>(),
                addedRequiredEntryItems: Array.Empty<DeathTowerData.TowerEntryItem>());
            var tower = new DeathTowerSession(config, new ushort[] { 100 });
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

            VerifyStageLoadingReleaseState(config, ref failures);
            VerifyCharacterDeathRouting(ref failures);
            VerifyActorUidAllocation(config, ref failures);
            VerifyPvfStageActorUids(ref failures);

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

        private static List<StageMonster> CreateActors(int count)
            => Enumerable.Range(0, count).Select(index => new StageMonster
            {
                ListIndex = index,
                MonsterIndex = 55001 + index,
                MonsterLevel = 60,
                MonsterType = 5,
            }).ToList();

        private static void VerifyActorUidAllocation(
            DeathTowerData.TowerConfig config, ref int failures)
        {
            foreach (var playerUid in new ushort[] { 4, 5 })
            {
                var tower = new DeathTowerSession(config, new[] { playerUid });
                var first = CreateActors(3);
                var second = CreateActors(1);
                var third = CreateActors(1);
                tower.AssignMonsterSequences(first);
                tower.AssignMonsterSequences(second);
                tower.AssignMonsterSequences(third);
                var ids = first.Concat(second).Concat(third).Select(m => m.MonsterUniqueId).ToArray();
                Check($"player UID {playerUid}: first three floors never collide or reuse IDs",
                    ids.Distinct().Count() == 5 && !ids.Contains(playerUid)
                    && second[0].MonsterUniqueId == (playerUid == 4 ? 5 : 4)
                    && third[0].MonsterUniqueId == 6, ref failures);
            }

            var roster = new ushort[] { 5, 2, 4, 2 };
            var partyTower = new DeathTowerSession(config, roster);
            roster[0] = 500; // The allocator must own a frozen copy.
            var actors = CreateActors(3);
            partyTower.AssignMonsterSequences(new List<StageMonster>());
            partyTower.AssignMonsterSequences(actors);
            Check("party UID conflicts shift the entire floor, preserving contiguous actor lookup",
                actors.Select(m => m.MonsterUniqueId).SequenceEqual(new ushort[] { 6, 7, 8 }), ref failures);

            var item = new StageTowerItem
            {
                SourceListIndex = actors[1].ListIndex,
                SourceMonsterUniqueId = actors[1].MonsterUniqueId,
                ItemUniqueId = 1, ItemId = 3037, DropRate = 10000, StackCount = 1,
            };
            partyTower.BeginStage(123, new[] { item });
            var body = DeathTowerPacketBuilder.BuildStageMap(partyTower, actors, new[] { item }, 123);
            using (var tcp = new TcpClient())
            {
                var session = new EnhancedClientSession(tcp, new GamePacketHeader());
                var run = new DungeonRun(11000, 0) { Tower = partyTower };
                session.Player.CurrentRun = run;
                DeathTowerCoordinator.SyncCombatStage(session, partyTower, actors);
                Check("wire IDs and authoritative room IDs agree after a range shift",
                    actors.Select((m, i) => ReadUInt16(body, 15 + 14 * i) == m.MonsterUniqueId
                        && run.RoomStartSequence + i == m.MonsterUniqueId
                        && run.RoomStates[run.RoomKey].InstanceRoom.FirstActorSequenceId + i == m.MonsterUniqueId
                        && run.RoomMonsters[i].PacketIndex == m.MonsterUniqueId).All(v => v), ref failures);
                session.Player.CurrentRun = null;
            }
            Check("drop source follows reassigned NPC UID and remains single-use",
                partyTower.GenerateDropsForMonster(2).Count == 0
                && partyTower.GenerateDropsForMonster(actors[1].MonsterUniqueId).Count == 1
                && partyTower.GenerateDropsForMonster(actors[1].MonsterUniqueId).Count == 0, ref failures);

            var boundary = new DeathTowerSession(config, new ushort[] { 1 });
            // Consume through 65534 without reflection or a production test hook.
            for (var remaining = ushort.MaxValue - 2; remaining > 0;)
            {
                var count = Math.Min(byte.MaxValue, remaining);
                boundary.AssignMonsterSequences(CreateActors(count));
                remaining -= count;
            }
            var oversized = CreateActors(2);
            var rejected = false;
            try { boundary.AssignMonsterSequences(oversized); }
            catch (InvalidOperationException) { rejected = true; }
            var last = CreateActors(1);
            boundary.AssignMonsterSequences(last);
            var exhausted = false;
            try { boundary.AssignMonsterSequences(CreateActors(1)); }
            catch (InvalidOperationException) { exhausted = true; }
            Check("exhaustion rejects atomically, permits the remaining UID, and never wraps",
                rejected && oversized.All(m => m.MonsterUniqueId == 0)
                && last[0].MonsterUniqueId == ushort.MaxValue && exhausted, ref failures);

            var highPlayer = new DeathTowerSession(config, new ushort[] { ushort.MaxValue });
            var lowActors = CreateActors(3);
            highPlayer.AssignMonsterSequences(lowActors);
            Check("high player UID does not unnecessarily move the initial range",
                lowActors[0].MonsterUniqueId == 1 && lowActors[2].MonsterUniqueId == 3, ref failures);
        }

        private static void VerifyPvfStageActorUids(ref int failures)
        {
            var config = DeathTowerData.GetConfig(11000);
            Check("current PVF loads the 45-floor death tower", config?.TotalStages == 45, ref failures);
            if (config == null)
                return;
            var reserved = new ushort[] { 1, 4, 5, 8 };
            var tower = new DeathTowerSession(config, reserved);
            var seen = new HashSet<ushort>();
            var allValid = true;
            var stages = 0;
            do
            {
                var actors = DeathTowerMapLoader.LoadStageMonsters(tower);
                if (actors.Count > byte.MaxValue)
                    actors.RemoveRange(byte.MaxValue, actors.Count - byte.MaxValue);
                tower.AssignMonsterSequences(actors);
                var items = DeathTowerMapLoader.LoadStageItems(tower, actors);
                var body = DeathTowerPacketBuilder.BuildStageMap(tower, actors, items, 123);
                allValid &= actors.Count > 0;
                for (var i = 0; i < actors.Count; i++)
                {
                    var uid = actors[i].MonsterUniqueId;
                    allValid &= uid != 0 && !reserved.Contains(uid) && seen.Add(uid)
                        && uid == actors[0].MonsterUniqueId + i
                        && ReadUInt16(body, 15 + 14 * i) == uid;
                }
                allValid &= items.All(item => actors.Any(actor =>
                    actor.MonsterUniqueId == item.SourceMonsterUniqueId
                    && actor.ListIndex == item.SourceListIndex));
                stages++;
                tower.SetFighting();
            } while (tower.TryAdvanceStage());
            Check("all PVF floors keep unique contiguous actor IDs and correct item source links",
                stages == config.TotalStages && allValid, ref failures);
        }

        private static void VerifyStageLoadingReleaseState(
            DeathTowerData.TowerConfig config,
            ref int failures)
        {
            var tower = new DeathTowerSession(config, new ushort[] { 100 });
            var run = new DungeonRunIdentity(
                partyDungeonInstanceId: 71,
                runId: 81,
                runGeneration: 1);
            var staleGeneration = new DungeonRunIdentity(
                partyDungeonInstanceId: 71,
                runId: 81,
                runGeneration: 2);

            Check(
                "entry stage has no deferred loading release",
                !tower.HasPendingStageLoadingRelease(run, 0),
                ref failures);

            tower.SetFighting();
            Check(
                "first advance reaches the second floor",
                tower.TryAdvanceStage() && tower.CurrentStage == 1,
                ref failures);
            Check(
                "second floor defers one loading release for the current run",
                tower.TryDeferStageLoadingRelease(run, tower.CurrentStage)
                    && tower.HasPendingStageLoadingRelease(
                        run,
                        tower.CurrentStage),
                ref failures);
            Check(
                "duplicate defer cannot replace the pending floor",
                !tower.TryDeferStageLoadingRelease(run, tower.CurrentStage),
                ref failures);
            Check(
                "stale generation cannot consume the current floor release",
                !tower.TryConsumeStageLoadingRelease(
                    staleGeneration,
                    tower.CurrentStage),
                ref failures);
            Check(
                "current floor release is consumed exactly once",
                tower.TryConsumeStageLoadingRelease(run, tower.CurrentStage)
                    && !tower.TryConsumeStageLoadingRelease(
                        run,
                        tower.CurrentStage),
                ref failures);

            tower.SetFighting();
            Check(
                "next advance can establish a new floor release",
                tower.TryAdvanceStage()
                    && tower.CurrentStage == 2
                    && tower.TryDeferStageLoadingRelease(
                        run,
                        tower.CurrentStage),
                ref failures);
            Check(
                "old floor cannot consume the new floor release",
                !tower.TryConsumeStageLoadingRelease(run, 1)
                    && tower.TryConsumeStageLoadingRelease(
                        run,
                        tower.CurrentStage),
                ref failures);
        }

        private static void VerifyCharacterDeathRouting(ref int failures)
        {
            Check(
                "scripted fatal endpoint keeps precedence over tower settlement",
                DungeonCombatHandler.ResolveCharacterDeathFlow(
                    suppressRespawn: true,
                    isDeathTower: true,
                    isTournament: true)
                    == DungeonCharacterDeathFlow.Scripted,
                ref failures);
            Check(
                "death tower death bypasses tournament respawn and party wipe",
                DungeonCombatHandler.ResolveCharacterDeathFlow(
                    suppressRespawn: false,
                    isDeathTower: true,
                    isTournament: true)
                    == DungeonCharacterDeathFlow.DeathTower,
                ref failures);
            Check(
                "tournament death retains its respawn flow",
                DungeonCombatHandler.ResolveCharacterDeathFlow(
                    suppressRespawn: false,
                    isDeathTower: false,
                    isTournament: true)
                    == DungeonCharacterDeathFlow.Tournament,
                ref failures);
            Check(
                "ordinary dungeon death retains party wipe handling",
                DungeonCombatHandler.ResolveCharacterDeathFlow(
                    suppressRespawn: false,
                    isDeathTower: false,
                    isTournament: false)
                    == DungeonCharacterDeathFlow.PartyWipe,
                ref failures);
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
