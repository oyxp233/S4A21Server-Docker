using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    public static class SpecialDungeonSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== SPECIAL_DUNGEON selftest ===");
            var failures = 0;

            TestPacketBodies(ref failures);
            TestDungeonCommandParser(ref failures);
            TestBossDieCheckParser(ref failures);
            TestTimeCrack(ref failures);
            TestTypedSpecialDungeonEffects(ref failures);
            TestGentInfiltrate(ref failures);
            TestSeizeMoney(ref failures);
            TestClearMapGroups(ref failures);
            TestBossRouteRuntime(ref failures);
            TestPvfBackedData(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void TestBossRouteRuntime(ref int failures)
        {
            var definition = new DungeonBossRouteDefinition(
                bossX: 4,
                bossY: 1,
                new[]
                {
                    new DungeonBossRouteEntryDefinition(
                        DungeonBossRouteDirection.Above, 4, 0, 101),
                    new DungeonBossRouteEntryDefinition(
                        DungeonBossRouteDirection.Below, 4, 2, 102),
                    new DungeonBossRouteEntryDefinition(
                        DungeonBossRouteDirection.Left, 3, 1, 103),
                    new DungeonBossRouteEntryDefinition(
                        DungeonBossRouteDirection.Left, 3, 1, 104),
                });
            var runtime = new DungeonBossRouteRuntime(definition, fallbackMapId: 102);

            var selected = runtime.TrySelectForMove(
                sourceX: 3,
                sourceY: 1,
                targetX: 4,
                targetY: 1,
                selectIndex: count => count - 1,
                out var selectedMapId,
                out var transitioned);
            Check(
                "Boss route commits the first matching entrance",
                selected
                    && transitioned
                    && selectedMapId == 104
                    && runtime.SelectedMapId == 104
                    && runtime.SelectedDirection == DungeonBossRouteDirection.Left
                    && !runtime.SelectedByFallback,
                ref failures);

            selected = runtime.TrySelectForMove(
                sourceX: 4,
                sourceY: 2,
                targetX: 4,
                targetY: 1,
                selectIndex: _ => 0,
                out selectedMapId,
                out transitioned);
            Check(
                "Boss route replay is an instance-level no-op",
                selected && !transitioned && selectedMapId == 104,
                ref failures);

            var fallbackRuntime = new DungeonBossRouteRuntime(
                definition,
                fallbackMapId: 102);
            var fallbackMapId = fallbackRuntime.ResolveForStartMap(
                out var fallbackCommitted);
            selected = fallbackRuntime.TrySelectForMove(
                sourceX: 3,
                sourceY: 1,
                targetX: 4,
                targetY: 1,
                selectIndex: _ => 0,
                out selectedMapId,
                out transitioned);
            Check(
                "Boss room creation freezes fallback before a late route event",
                fallbackCommitted
                    && fallbackMapId == 102
                    && selected
                    && !transitioned
                    && selectedMapId == 102
                    && fallbackRuntime.SelectedByFallback,
                ref failures);
        }

        private static void TestPacketBodies(ref int failures)
        {
            var passGate =
                SpecialDungeonNotificationBuilder
                    .BuildCompleteConditionPassGateTrigger();
            Check(
                "0x0138 body is five zero bytes",
                BytesEqual(passGate, 0x00, 0x00, 0x00, 0x00, 0x00),
                ref failures);

            var summon =
                SpecialDungeonNotificationBuilder
                    .BuildSummonMonsterCommandCreateResponse(
                        result: 0x01,
                        state: 0x01020304,
                        count: 0x01,
                        key: SpecialDungeonNotifier.BossSummonRuntimeKey,
                        monsterCode: 0x11223344,
                        mode: 0x03,
                        paramA: 0x5566);
            Check(
                "0x0211 command response layout and runtime key",
                BytesEqual(
                    summon,
                    0x01,
                    0x04, 0x03, 0x02, 0x01,
                    0x01,
                    0xDD, 0x42,
                    0x44, 0x33, 0x22, 0x11,
                    0x03,
                    0x66, 0x55),
                ref failures);
        }

        private static void TestDungeonCommandParser(ref int failures)
        {
            var summonBody = new byte[19];
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)7), 0, summonBody, 0, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(69264), 0, summonBody, 2, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(12345), 0, summonBody, 6, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(17120), 0, summonBody, 10, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)11), 0, summonBody, 14, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)22), 0, summonBody, 16, 2);
            summonBody[18] = 1;

            var parsedSummon = DungeonCommandParser.TryParse(
                (ushort)CmdPacketType.SUMMON_MONSTER,
                summonBody,
                out var rawSummon,
                out _);
            var summon = rawSummon as SummonMonsterDungeonCommand;
            Check(
                "typed dungeon command parser decodes SUMMON_MONSTER once",
                parsedSummon
                    && summon != null
                    && summon.ConditionalType == 7
                    && summon.MonsterCode == 69264
                    && summon.StateId == 12345
                    && summon.MapId == 17120
                    && summon.ConditionalParam0 == 11
                    && summon.ConditionalParam1 == 22
                    && summon.MatchCount == 1,
                ref failures);
            Check(
                "typed dungeon command parser rejects truncated SUMMON_MONSTER",
                !DungeonCommandParser.TryParse(
                    (ushort)CmdPacketType.SUMMON_MONSTER,
                    new byte[18],
                    out _,
                    out _),
                ref failures);

            var parsedResult = DungeonCommandParser.TryParse(
                (ushort)CmdPacketType.SEA_CHASE_MINI_GAME_RESULT,
                BitConverter.GetBytes(1),
                out var rawResult,
                out _);
            Check(
                "typed dungeon command parser decodes SeaChase int32 result",
                parsedResult
                    && rawResult is SeaChaseResultDungeonCommand result
                    && result.Result == 1,
                ref failures);
            Check(
                "typed dungeon command parser rejects truncated SeaChase result",
                !DungeonCommandParser.TryParse(
                    (ushort)CmdPacketType.SEA_CHASE_MINI_GAME_RESULT,
                    new byte[3],
                    out _,
                    out _),
                ref failures);

            Check(
                "typed dungeon command parser preserves empty NPC item-drop command",
                DungeonCommandParser.TryParse(
                    (ushort)CmdPacketType.EVENT_NPC_DROP_ITEM_,
                    Array.Empty<byte>(),
                    out var rawNpcDrop,
                    out _)
                && rawNpcDrop is NpcItemDropDungeonCommand npcDrop
                && !npcDrop.HasUnexpectedPayload,
                ref failures);
        }

        private static void TestBossDieCheckParser(ref int failures)
        {
            var parsed = BossDieCheckRequest.TryParse(
                new byte[] { 0x34, 0x12, 0x78, 0x56 },
                out var request);
            Check(
                "BOSS_DIE_CHECK parses little-endian user and sequence",
                parsed
                    && request.UserId == 0x1234
                    && request.BossSequence == 0x5678,
                ref failures);
            Check(
                "BOSS_DIE_CHECK rejects truncated body",
                !BossDieCheckRequest.TryParse(
                    new byte[] { 0x34, 0x12, 0x78 },
                    out _),
                ref failures);

            using (var client = new TcpClient())
            {
                var session = new EnhancedClientSession(
                    client,
                    new GamePacketHeader());
                var run = new DungeonRun(900, 0)
                {
                    BossEntranceConditionComplete = true,
                    ConditionalBossSpawned = true,
                    ConditionalBossCode = 222,
                };
                run.BossEntranceConditionTargets.Add(
                    new BossEntranceConditionTargetState
                    {
                        MonsterCode = 100,
                        X = 1,
                        Y = 1,
                        Completed = true,
                    });
                run.BossEntranceConditionalSummonCodes.Add(111);
                run.BossEntranceConditionalSummonCodes.Add(222);
                session.Player.CurrentRun = run;

                var clearRequest = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    new BossDieCheckRequest(
                        userId: 1,
                        bossSequence: SpecialDungeonNotifier.BossSummonRuntimeKey));
                Check(
                    "BOSS_DIE_CHECK uses the Boss actually spawned in this run",
                    clearRequest.ShouldClearDungeon
                        && clearRequest.BossCode == 222,
                    ref failures);
            }
        }

        private static void TestTimeCrack(ref int failures)
        {
            var definition = new SpecialDungeonDefinitionBuilder
            {
                TimeCrackSandGaugeMax = 100,
                TimeCrackSandGaugeGainOnKill = 10,
                TimeCrackSandGaugeGainOnChampion = 30,
            };
            definition.TimeCrackInvincibleMonsterCodes.Add(9000);
            definition.TimeCrackBuffWeights.Add(
                new TimeCrackBuffWeight(101, 1));
            definition.TimeCrackBuffWeights.Add(
                new TimeCrackBuffWeight(202, 100));

            var special = new SpecialDungeonRuntime(
                definition.Build(1000, SpecialDungeonKind.TimeCrack));

            var advanced = special.TryAddTimeCrackGauge(
                monsterCode: 100,
                isChampion: true,
                out var previous,
                out var current,
                out var delta,
                out var filled);
            Check(
                "TimeCrack champion grants configured 30 energy",
                advanced
                    && previous == 0
                    && current == 30
                    && delta == 30
                    && !filled,
                ref failures);

            Check(
                "TimeCrack invincible monster grants no energy",
                !special.TryAddTimeCrackGauge(
                    monsterCode: 9000,
                    isChampion: false,
                    out _,
                    out _,
                    out _,
                    out _)
                    && special.TimeCrackGauge == 30,
                ref failures);

            special.NoteTimeCrackBuffApplied(101);
            var picked = SpecialDungeonNotifier.TryPickTimeCrackBuff(
                special,
                new DnfLcg(1),
                out var buffId,
                out _,
                out var totalWeight,
                out var pickMode);
            Check(
                "TimeCrack weighted selection prefers missing buffs",
                picked
                    && buffId == 202
                    && totalWeight == 100
                    && pickMode == "missing_first",
                ref failures);

            special.NoteTimeCrackBuffApplied(202);
            picked = SpecialDungeonNotifier.TryPickTimeCrackBuff(
                special,
                new DnfLcg(1),
                out buffId,
                out _,
                out totalWeight,
                out pickMode);
            Check(
                "TimeCrack refreshes full weighted table after all buffs",
                picked
                    && (buffId == 101 || buffId == 202)
                    && totalWeight == 101
                    && pickMode == "refresh_all",
                ref failures);
        }

        private static void TestGentInfiltrate(ref int failures)
        {
            const string condition =
                "`[hunt monster]` 4 " +
                "1001 0 1 " +
                "1002 0 1 " +
                "1003 0 1 " +
                "1004 0 1";

            var withinTime = new SpecialDungeonRuntime(
                new SpecialDungeonDefinitionBuilder().Build(
                    2000,
                    SpecialDungeonKind.GentInfiltrate));
            withinTime.ConfigureGentInfiltrateBossEntrance(
                SpecialDungeonDefinitionCatalog
                    .ParseGentInfiltrateTowerRequirements(condition),
                timerSeconds: 180);

            var completed = false;
            for (var code = 1001; code <= 1004; code++)
            {
                withinTime.TryMarkGentInfiltrateTowerDestroyed(
                    code,
                    out _,
                    out _,
                    out _,
                    out _,
                    out completed);
            }

            Check(
                "Gent four towers within time enables strong Warlord path",
                completed
                    && withinTime.GentInfiltrateConditionComplete
                    && withinTime.GentInfiltrateStrongWarlord
                    && !withinTime.GentInfiltrateTimedOut,
                ref failures);

            var timedOut = new SpecialDungeonRuntime(
                new SpecialDungeonDefinitionBuilder().Build(
                    2000,
                    SpecialDungeonKind.GentInfiltrate));
            timedOut.ConfigureGentInfiltrateBossEntrance(
                SpecialDungeonDefinitionCatalog
                    .ParseGentInfiltrateTowerRequirements(condition),
                timerSeconds: 180);
            timedOut.TryCompleteGentInfiltrateByTimer(out _, out _);

            completed = false;
            for (var code = 1001; code <= 1004; code++)
            {
                timedOut.TryMarkGentInfiltrateTowerDestroyed(
                    code,
                    out _,
                    out _,
                    out _,
                    out _,
                    out completed);
            }

            Check(
                "Gent timeout still waits for four towers without strong Warlord",
                completed
                    && timedOut.GentInfiltrateConditionComplete
                    && !timedOut.GentInfiltrateStrongWarlord
                    && timedOut.GentInfiltrateTimedOut,
                ref failures);
        }

        private static void TestTypedSpecialDungeonEffects(ref int failures)
        {
            var timeCrackBuilder = new SpecialDungeonDefinitionBuilder
            {
                TimeCrackSandGaugeMax = 30,
                TimeCrackSandGaugeGainOnKill = 10,
                TimeCrackSandGaugeGainOnChampion = 30,
            };
            timeCrackBuilder.TimeCrackBuffWeights.Add(
                new TimeCrackBuffWeight(301, 1));
            var timeCrackRun = new DungeonRun(1000, 0);
            timeCrackRun.Mechanisms.SpecialDungeon =
                new SpecialDungeonRuntime(
                    timeCrackBuilder.Build(
                        1000,
                        SpecialDungeonKind.TimeCrack));
            timeCrackRun.Combat.RoomLcg = new DnfLcg(1);

            var application =
                new SpecialDungeonMechanismApplicationService();
            var effects = application.ApplyMonsterKilled(
                timeCrackRun,
                monsterCode: 100,
                monsterType: 1);
            Check(
                "TimeCrack transition emits ordered typed effects",
                effects.Count == 4
                    && effects[0].Kind
                        == SpecialDungeonEffectKind.GaugeChanged
                    && effects[0].Value == 30
                    && effects[1].Kind
                        == SpecialDungeonEffectKind.BuffAddedAndActivated
                    && effects[1].BuffIds.Count == 1
                    && effects[1].BuffIds[0] == 301
                    && effects[2].Kind
                        == SpecialDungeonEffectKind.ResetTimeCrackGauge
                    && effects[3].Kind
                        == SpecialDungeonEffectKind.GaugeChanged
                    && effects[3].Value == 0,
                ref failures);

            var seaBuilder = new SpecialDungeonDefinitionBuilder();
            seaBuilder.SeaChaseSuccessBuffIds.Add(401);
            var seaRun = new DungeonRun(1001, 0);
            seaRun.Mechanisms.SpecialDungeon = new SpecialDungeonRuntime(
                seaBuilder.Build(1001, SpecialDungeonKind.SeaChase));
            var command = new SeaChaseResultDungeonCommand(
                (ushort)CmdPacketType.SEA_CHASE_MINI_GAME_RESULT,
                result: 1);
            effects = application.ApplySeaChaseResult(seaRun, command);
            var replayEffects = application.ApplySeaChaseResult(
                seaRun,
                command);
            Check(
                "SeaChase first result emits ACK, buff and post-send record only once",
                effects.Count == 3
                    && effects[0].Kind
                        == SpecialDungeonEffectKind.CommandSuccessAck
                    && effects[1].Kind
                        == SpecialDungeonEffectKind.BuffAddedAndActivated
                    && effects[2].Kind
                        == SpecialDungeonEffectKind.RecordSeaChaseBuffs
                    && replayEffects.Count == 1
                    && replayEffects[0].Kind
                        == SpecialDungeonEffectKind.CommandSuccessAck,
                ref failures);
        }

        private static void TestSeizeMoney(ref int failures)
        {
            var definition = new SpecialDungeonDefinitionBuilder
            {
                SeizeMoneyGaugeMax = 1000,
                SeizeMoneyGaugeSubOnDamage = 100,
            };

            var special = new SpecialDungeonRuntime(
                definition.Build(3000, SpecialDungeonKind.SeizeMoney));
            var reserved = special.TryReserveAuthoritativeSeizeMoneyClearReward(
                maxDropCount: 4,
                out var plan,
                out var failureReason);
            Check(
                "SeizeMoney without an authoritative hit fact grants no reward",
                !reserved
                    && plan == null
                    && failureReason == "no_authoritative_hit_fact"
                    && special.SeizeMoneyGauge == 1000,
                ref failures);

            var recorded = special.ApplyAuthoritativeSeizeMoneyHits(5);
            reserved = special.TryReserveAuthoritativeSeizeMoneyClearReward(
                maxDropCount: 4,
                out plan,
                out failureReason);
            Check(
                "SeizeMoney scales clear reward from authoritative ETC gauge",
                recorded
                    && reserved
                    && plan != null
                    && plan.HitCount == 5
                    && plan.RemainingUnits == 5
                    && plan.Count == 2
                    && plan.Gauge == 500
                    && special.SeizeMoneyGauge == 500,
                ref failures);

            var reservedAgain = special.TryReserveAuthoritativeSeizeMoneyClearReward(
                maxDropCount: 4,
                out var replayPlan,
                out failureReason);
            Check(
                "SeizeMoney authoritative clear reward reservation is one-shot",
                !reservedAgain
                    && replayPlan == null
                    && failureReason == "already_generated"
                    && special.SeizeMoneyGauge == 500,
                ref failures);
        }

        private static void TestPvfBackedData(ref int failures)
        {
            try
            {
                _ = GameWorld.GameWorldConfig.PvfArchivePath;
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine(
                    "[SKIP] PVF-backed special dungeon checks: Script.pvf not found");
                return;
            }

            var gent = CreateSpecialDungeonRuntime(2005);
            var sea = CreateSpecialDungeonRuntime(2006);
            var timeCrack = CreateSpecialDungeonRuntime(2007);
            var sealForest = CreateSpecialDungeonRuntime(2009);
            var seizeMoney = CreateSpecialDungeonRuntime(2011);

            Check(
                "PVF identifies all stateful part-one special dungeon kinds",
                gent?.Kind == SpecialDungeonKind.GentInfiltrate
                    && sea?.Kind == SpecialDungeonKind.SeaChase
                    && timeCrack?.Kind == SpecialDungeonKind.TimeCrack
                    && sealForest?.Kind == SpecialDungeonKind.SealForest
                    && seizeMoney?.Kind == SpecialDungeonKind.SeizeMoney,
                ref failures);

            Check(
                "PVF loads core ETC settings for part-one dungeons",
                gent?.GentInfiltrateTimerSeconds == 300
                    && sea?.Definition.SeaChase.SuccessBuffIds.Count > 0
                    && timeCrack?.Definition.TimeCrack.BuffWeights.Count > 0
                    && timeCrack.Definition.TimeCrack.SandGaugeGainOnChampion == 30
                    && sealForest?.Definition.SealForest.BuffsByMonsterCode.Count > 0
                    && seizeMoney?.Definition.SeizeMoney.GaugeMax > 0,
                ref failures);

            var meltdownMaze = GameWorld.Dungeon.GetDungeonMaze(2010, 0);
            var meltdownRun = new DungeonRun(2010, 0) { MazeIndex = 0 };
            SpecialDungeonRunCoordinator.ConfigureSelection(
                meltdownRun,
                meltdownMaze,
                meltdownMaze.BossMap,
                Array.Empty<ActiveQuest>());
            Check(
                "PVF hunt+summon condition enables generic Boss entrance mechanism",
                meltdownRun.HasBossEntranceConditionalSummon
                    && meltdownRun.BossEntranceConditionTargets.Count == 3
                    && meltdownRun.BossEntranceConditionalSummonCodes
                        .SequenceEqual(new[] { 69264 }),
                ref failures);

            var stationMaze = GameWorld.Dungeon.GetDungeonMaze(2016, 0);
            var stationRun = new DungeonRun(2016, 0) { MazeIndex = 0 };
            SpecialDungeonRunCoordinator.ConfigureSelection(
                stationRun,
                stationMaze,
                stationMaze.BossMap,
                Array.Empty<ActiveQuest>());
            Check(
                "same PVF condition enables StationEscape without a dungeon-kind branch",
                stationRun.HasBossEntranceConditionalSummon
                    && stationRun.BossEntranceConditionTargets.Count == 4
                    && stationRun.BossEntranceConditionTargets.TrueForAll(
                        target => target.MonsterCode == 61574)
                    && stationRun.BossEntranceConditionalSummonCodes
                        .SequenceEqual(new[] { 56616 }),
                ref failures);

            var gentMaze = GameWorld.Dungeon.GetDungeonMaze(2005, 0);
            var gentRun = new DungeonRun(2005, 0)
            {
                MazeIndex = 0,
                SpecialDungeon = gent,
            };
            SpecialDungeonRunCoordinator.ConfigureSelection(
                gentRun,
                gentMaze,
                gentMaze.BossMap,
                Array.Empty<ActiveQuest>());
            Check(
                "hunt-only Boss entrance condition does not enable conditional summon",
                !gentRun.HasBossEntranceConditionalSummon,
                ref failures);

            Check(
                "SeizeMoney reward resolves from the Boss independent drop",
                IndependentDropSystem.TryResolveSingleFixedDropTemplate(
                    monsterCode: 56631,
                    difficulty: 0,
                    dungeonLevel: 75,
                    partyMemberCount: 1,
                    out var seizeMoneyItemId,
                    out var seizeMoneyMaxCount)
                    && seizeMoneyItemId == 10089565
                    && seizeMoneyMaxCount == 4,
                ref failures);

            var gentClearConditions =
                GameWorld.Dungeon.GetDungeonFile(2005).Mazes[0].ClearConditions;
            var seaClearConditions =
                GameWorld.Dungeon.GetDungeonFile(2006).Mazes[0].ClearConditions;
            Check(
                "PVF Gent alternate final rooms share one-of-two clear group",
                IsClearMapGroup(
                    gentClearConditions,
                    required: 1,
                    17068,
                    17069),
                ref failures);
            Check(
                "PVF SeaChase alternate final rooms share one-of-two clear group",
                IsClearMapGroup(
                    seaClearConditions,
                    required: 1,
                    17080,
                    17083),
                ref failures);

            var gentClearState = new ClearConditionState(gentClearConditions);
            var seaClearState = new ClearConditionState(seaClearConditions);
            Check(
                "Gent non-endpoint final map satisfies explicit clear condition",
                gentClearState.TotalRequired == 1
                    && gentClearState.Check(1, 17069),
                ref failures);
            Check(
                "SeaChase double-Boss map satisfies explicit clear condition",
                seaClearState.TotalRequired == 1
                    && seaClearState.Check(1, 17083),
                ref failures);

            var seaHiddenRoom =
                GameWorld.Dungeon.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 2006,
                    x: 0,
                    y: 1,
                    mazeIndex: 0,
                    overrideMapId: 17083);
            var seaBossCodes = new HashSet<int>();
            var seaBlockingBossCount = 0;
            foreach (var monster in seaHiddenRoom.Monsters)
            {
                if (!monster.IsBlocking)
                    continue;

                seaBlockingBossCount++;
                seaBossCodes.Add(monster.Code);
            }
            Check(
                "PVF SeaChase hidden room registers both blocking Bosses",
                seaBlockingBossCount == 2
                    && seaBossCodes.SetEquals(new[] { 69225, 69261 }),
                ref failures);

            var seaRoomRun = new DungeonRun(2006, 0)
            {
                RoomMonsters = seaHiddenRoom.Monsters,
                RoomStartSequence = 300,
            };
            seaRoomRun.RoomKilledSeqIds.Add(300);
            var oneBossCleared = DungeonRoomTopology.ComputeRoomClearedLocked(
                seaRoomRun,
                out var seaBlockingCount,
                out var oneBossKilledCount);
            seaRoomRun.RoomKilledSeqIds.Add(301);
            var bothBossesCleared = DungeonRoomTopology.ComputeRoomClearedLocked(
                seaRoomRun,
                out _,
                out var bothBossesKilledCount);
            Check(
                "SeaChase hidden room clears only after both Bosses die",
                seaBlockingCount == 2
                    && oneBossKilledCount == 1
                    && !oneBossCleared
                    && bothBossesKilledCount == 2
                    && bothBossesCleared,
                ref failures);

            var conditionActors =
                GameWorld.Dungeon.GetMapMonsterConditionSummaryInformation(
                    mapId: 17120,
                    dungeonId: 2010,
                    x: 3,
                    y: 3,
                    monsterCodes: new[] { 56611 });
            Check(
                "PVF condition target is visible and blocks room progress",
                conditionActors.Count > 0
                    && conditionActors[0].Code == 56611
                    && conditionActors[0].IsBlocking
                    && conditionActors[0].Flag0 == 0
                    && conditionActors[0].PacketIndex.HasValue,
                ref failures);

            var bossTemplates =
                GameWorld.Dungeon.GetMapConditionalSummonSummaryInformation(
                    mapId: 17120,
                    dungeonId: 2010,
                    x: 3,
                    y: 3,
                    monsterCodes: new[] { 69264 });
            Check(
                "PVF hidden Boss template preserves line, position and level",
                bossTemplates.Count > 0
                    && bossTemplates[0].Code == 69264
                    && bossTemplates[0].Type == 3
                    && !bossTemplates[0].IsBlocking
                    && bossTemplates[0].Flag0 == 1
                    && bossTemplates[0].TemplateOrder == 6
                    && bossTemplates[0].X > 100
                    && bossTemplates[0].Y > 100,
                ref failures);

            var stationBossTemplates =
                GameWorld.Dungeon.GetMapConditionalSummonSummaryInformation(
                    mapId: 11057,
                    dungeonId: 2016,
                    x: 1,
                    y: 0,
                    monsterCodes: new[] { 56616 });
            Check(
                "PVF StationEscape Boss template inherits dungeon level",
                stationBossTemplates.Count > 0
                    && stationBossTemplates[0].Code == 56616
                    && stationBossTemplates[0].Type == 3
                    && stationBossTemplates[0].Level == 86,
                ref failures);

            using (var client = new TcpClient())
            {
                var session = new EnhancedClientSession(
                    client,
                    new GamePacketHeader());
                var run = new DungeonRun(2010, 0);
                run.BossEntranceConditionTargets.Add(
                    new BossEntranceConditionTargetState
                    {
                        MonsterCode = 56611,
                        X = 3,
                        Y = 3,
                    });
                run.BossEntranceConditionalSummonCodes.Add(69264);
                session.Player.CurrentRun = run;

                var startMap =
                    GameWorld.Dungeon.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 2010,
                        x: 3,
                        y: 3,
                        mazeIndex: 0,
                        overrideMapId: 17120);
                SpecialDungeonRunCoordinator.AppendStartMapActors(
                    session,
                    startMap);

                var bossIndex =
                    startMap.Monsters.FindIndex(monster => monster.Code == 69264);
                var targetIndex =
                    startMap.Monsters.FindIndex(monster => monster.Code == 56611);
                Check(
                    "START_MAP places hidden Boss template before condition target",
                    bossIndex >= 0
                        && targetIndex > bossIndex
                        && startMap.Monsters[bossIndex].Flag0 == 1
                        && startMap.Monsters[targetIndex].Flag0 == 0,
                    ref failures);
            }

            var hasWarp = GameWorld.Dungeon.TryGetWarpMapOverride(
                dungeonId: 2005,
                mazeIndex: 0,
                targetX: 2,
                targetY: 1,
                out var sourceX,
                out var sourceY,
                out var destX,
                out var destY,
                out var overrideMapId);
            Check(
                "PVF Gent warp condition resolves source and destination map",
                hasWarp
                    && sourceX == 2
                    && sourceY == 1
                    && destX == 0
                    && destY == 0
                    && overrideMapId == 17069,
                ref failures);

            var hasSeaChaseRules = GameWorld.Dungeon.TryGetWarpMapConditionRules(
                dungeonId: 2006,
                mazeIndex: 0,
                out var seaChaseRules);
            Check(
                "PVF SeaChase keeps all warp condition rules",
                hasSeaChaseRules
                    && seaChaseRules.Count == 3
                    && seaChaseRules[0].SourceX == 0
                    && seaChaseRules[0].SourceY == 0
                    && seaChaseRules[0].DestinationX == 1
                    && seaChaseRules[0].DestinationY == 0
                    && seaChaseRules[1].SourceX == 0
                    && seaChaseRules[1].SourceY == 0
                    && seaChaseRules[1].DestinationX == 6
                    && seaChaseRules[1].DestinationY == 0
                    && seaChaseRules[2].SourceX == 7
                    && seaChaseRules[2].SourceY == 0
                    && seaChaseRules[2].DestinationX == 0
                    && seaChaseRules[2].DestinationY == 1,
                ref failures);

            var ambiguousSeaChaseWarp = GameWorld.Dungeon.TryGetWarpMapOverride(
                dungeonId: 2006,
                mazeIndex: 0,
                targetX: 0,
                targetY: 0,
                out _,
                out _,
                out _,
                out _,
                out _);
            Check(
                "PVF SeaChase does not guess between same-source warp destinations",
                !ambiguousSeaChaseWarp,
                ref failures);

            TestTimeCrackBossSelectionPvf(ref failures);
            TestQuestBoundBossSelectionPvf(ref failures);
        }

        private static void TestClearMapGroups(ref int failures)
        {
            const string ordinaryContent =
                "[maze info]\n" +
                "[size]\n1 1\n" +
                "[clear condition]\n" +
                "[clear map]\n16030 1\n" +
                "[/clear condition]\n";
            var ordinaryParsed = PvfLib.DungeonFile.Parse(ordinaryContent);
            var ordinaryConditions = ordinaryParsed.Mazes.Count == 1
                ? ordinaryParsed.Mazes[0].ClearConditions
                : new List<PvfLib.ClearConditionEntry>();
            var ordinaryState = new ClearConditionState(ordinaryConditions);
            Check(
                "ordinary clear-map condition keeps map and count semantics",
                ordinaryConditions.Count == 1
                    && ordinaryConditions[0].Type == 1
                    && ordinaryConditions[0].TargetId == 16030
                    && ordinaryConditions[0].Count == 1
                    && ordinaryConditions[0].GroupId == 0
                    && ordinaryState.Check(1, 16030),
                ref failures);

            const string content =
                "[maze info]\n" +
                "[size]\n1 1\n" +
                "[clear condition]\n" +
                "[clear map]\n`list` 3 100 101 102 2\n" +
                "[/clear condition]\n";
            var parsed = PvfLib.DungeonFile.Parse(content);
            var conditions = parsed.Mazes.Count == 1
                ? parsed.Mazes[0].ClearConditions
                : new List<PvfLib.ClearConditionEntry>();

            Check(
                "clear-map list parser preserves members and required count",
                IsClearMapGroup(conditions, required: 2, 100, 101, 102),
                ref failures);

            var state = new ClearConditionState(conditions);
            var first = state.Check(1, 100);
            var duplicate = state.Check(1, 100);
            var secondDistinct = state.Check(1, 101);
            Check(
                "clear-map group counts distinct maps only",
                state.TotalRequired == 2
                    && !first
                    && !duplicate
                    && secondDistinct
                    && state.CurrentProgress == 2,
                ref failures);
        }

        private static bool IsClearMapGroup(
            IReadOnlyList<PvfLib.ClearConditionEntry> conditions,
            int required,
            params int[] mapIds)
        {
            if (conditions == null || conditions.Count != mapIds.Length)
                return false;

            var groupId = 0;
            var actualMapIds = new HashSet<int>();
            foreach (var condition in conditions)
            {
                if (condition == null
                    || condition.Type != 1
                    || condition.Count != 1
                    || condition.GroupId <= 0
                    || condition.GroupRequired != required)
                {
                    return false;
                }

                if (groupId == 0)
                    groupId = condition.GroupId;
                else if (groupId != condition.GroupId)
                    return false;

                actualMapIds.Add(condition.TargetId);
            }

            return actualMapIds.SetEquals(mapIds);
        }

        private static void TestTimeCrackBossSelectionPvf(ref int failures)
        {
            var expectedBossMapIds = new HashSet<int>
            {
                17139,
                17144,
                17145,
                17148,
            };
            var huntTargets =
                GameWorld.QuestData.GetHuntMonsterTargets(13509);
            Check(
                "TimeCrack quest 13509 parses three Boss targets",
                huntTargets.Count == 3
                    && huntTargets[0].DungeonId == 2007
                    && huntTargets[0].MinimumDifficulty == -1
                    && huntTargets[0].MonsterCode == 69267
                    && huntTargets[1].MonsterCode == 69268
                    && huntTargets[2].MonsterCode == 69269,
                ref failures);
            Check(
                "TimeCrack quest trigger preserves three unfinished channels",
                GameWorld.QuestData.GetTriggerChannel(0x00040201, 0) == 1
                    && GameWorld.QuestData.GetTriggerChannel(0x00040201, 1) == 1
                    && GameWorld.QuestData.GetTriggerChannel(0x00040201, 2) == 1,
                ref failures);

            var maze = GameWorld.Dungeon.GetDungeonMaze(2007, 0);
            var bossPos = new[] { 9, 0 };
            var questBossMapId =
                SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 2007,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            Slot = 3,
                            QuestId = 13509,
                            TriggerValue = 0x00040201,
                        },
                    });
            Check(
                "Unfinished TimeCrack quest fixes Boss room to target map",
                questBossMapId == 17148,
                ref failures);

            var everyUnfinishedChannelUsesTargetMap = true;
            foreach (var trigger in new[]
                {
                    0x00000001u,
                    0x00000200u,
                    0x00040000u,
                })
            {
                var selected =
                    SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                        dungeonId: 2007,
                        maze: maze,
                        bossPos: bossPos,
                        activeQuests: new List<ActiveQuest>
                        {
                            new ActiveQuest
                            {
                                QuestId = 13509,
                                TriggerValue = trigger,
                            },
                        });
                if (selected != 17148)
                {
                    everyUnfinishedChannelUsesTargetMap = false;
                    break;
                }
            }
            Check(
                "Any unfinished TimeCrack target channel keeps target Boss map",
                everyUnfinishedChannelUsesTargetMap,
                ref failures);

            Check(
                "Completed TimeCrack quest does not override Boss map",
                SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 2007,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            QuestId = 13509,
                            TriggerValue = 0,
                        },
                    }) == -1,
                ref failures);
            Check(
                "Unrelated quest list does not override TimeCrack Boss map",
                SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 2007,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>()) == -1,
                ref failures);
            Check(
                "TimeCrack material quest sources in ordinary rooms do not override Boss map",
                SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 2007,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            QuestId = 13510,
                            TriggerValue = 1,
                        },
                    }) == -1,
                ref failures);

            var normalSelectionsValid = true;
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var selected =
                    SpecialDungeonRunCoordinator.ResolveSelectedBossMapId(
                        dungeonId: 2007,
                        mazeIndex: 0,
                        maze: maze,
                        bossPos: bossPos,
                        activeQuests: new List<ActiveQuest>());
                if (!expectedBossMapIds.Contains(selected))
                {
                    normalSelectionsValid = false;
                    break;
                }
            }
            Check(
                "TimeCrack without related quest uses explicit Boss candidate pool",
                normalSelectionsValid,
                ref failures);
        }

        private static void TestQuestBoundBossSelectionPvf(ref int failures)
        {
            var maze = GameWorld.Dungeon.GetDungeonMaze(154, 0);
            var bossPos = new[] { 4, 1 };
            var cases = new[]
            {
                (QuestId: 1900, MonsterCode: 63517, MapId: 8213),
                (QuestId: 1919, MonsterCode: 64054, MapId: 8212),
                (QuestId: 1923, MonsterCode: 64055, MapId: 8211),
            };

            var allRewardSourcesResolve = true;
            foreach (var testCase in cases)
            {
                var targets = GameWorld.QuestData.GetUnfinishedDungeonActorTargets(
                    testCase.QuestId,
                    trigger: 1,
                    dungeonId: 154,
                    difficulty: 0);
                var selected = SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 154,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            QuestId = (ushort)testCase.QuestId,
                            TriggerValue = 1,
                        },
                    },
                    difficulty: 0);
                if (!targets.Exists(target =>
                        target.ActorCode == testCase.MonsterCode
                        && target.Source == "monster reward item")
                    || selected != testCase.MapId)
                {
                    allRewardSourcesResolve = false;
                    break;
                }
            }
            Check(
                "Quest reward sources bind Ancient Tomb to the matching Boss map",
                allRewardSourcesResolve,
                ref failures);

            Check(
                "Non-Boss quest reward source does not force an unrelated Boss map",
                SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 154,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            QuestId = 1920,
                            TriggerValue = 1,
                        },
                    },
                    difficulty: 0) == -1,
                ref failures);

            var run = new DungeonRun(154, 0)
            {
                MazeIndex = 0,
            };
            SpecialDungeonRunCoordinator.ConfigureSelection(
                run,
                maze,
                bossPos,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        QuestId = 1919,
                        TriggerValue = 1,
                    },
                });
            Check(
                "Generic selection fixes quest-bound Boss maps outside TimeCrack",
                run.SpecialDungeon == null
                && run.SelectedBossMapId == 8212,
                ref failures);

            var routeDefinition = DungeonBossRouteDefinitionProjector.Project(
                dungeonId: 154,
                mazeIndex: 0,
                maze,
                bossPos);
            Check(
                "Ancient Tomb projects Boss candidates from MAP greed masks",
                routeDefinition != null
                    && routeDefinition.Routes.Count == 3
                    && routeDefinition.Routes[0].Direction
                        == DungeonBossRouteDirection.Above
                    && routeDefinition.Routes[0].MapId == 8211
                    && routeDefinition.Routes[1].Direction
                        == DungeonBossRouteDirection.Below
                    && routeDefinition.Routes[1].MapId == 8212
                    && routeDefinition.Routes[2].Direction
                        == DungeonBossRouteDirection.Left
                    && routeDefinition.Routes[2].MapId == 8213,
                ref failures);
            Check(
                "Ancient Tomb MAP greed decodes authoritative entrance bits",
                DungeonMapResolver.TryGetMapEntranceMask(8211, out var aboveMask)
                    && aboveMask == 2
                    && DungeonMapResolver.TryGetMapEntranceMask(8212, out var belowMask)
                    && belowMask == 8
                    && DungeonMapResolver.TryGetMapEntranceMask(8213, out var leftMask)
                    && leftMask == 4,
                ref failures);

            var blazingGrakaMaze = GameWorld.Dungeon.GetDungeonMaze(8, 0);
            var blazingGrakaRoute = DungeonBossRouteDefinitionProjector.Project(
                dungeonId: 8,
                mazeIndex: 0,
                blazingGrakaMaze,
                new[] { 3, 1 });
            Check(
                "MAP greed route projection applies outside Ancient Tomb",
                blazingGrakaRoute != null
                    && blazingGrakaRoute.Routes.Count == 2
                    && blazingGrakaRoute.Routes[0].Direction
                        == DungeonBossRouteDirection.Below
                    && blazingGrakaRoute.Routes[0].MapId == 1223
                    && blazingGrakaRoute.Routes[1].Direction
                        == DungeonBossRouteDirection.Right
                    && blazingGrakaRoute.Routes[1].MapId == 1224,
                ref failures);

            var butterflyMaze = GameWorld.Dungeon.GetDungeonMaze(52, 0);
            var butterflyRoute = DungeonBossRouteDefinitionProjector.Project(
                dungeonId: 52,
                mazeIndex: 0,
                butterflyMaze,
                new[] { 1, 1 });
            Check(
                "MAP greed route projection preserves another ordinary dungeon",
                butterflyRoute != null
                    && butterflyRoute.Routes.Count == 2
                    && butterflyRoute.Routes[0].Direction
                        == DungeonBossRouteDirection.Left
                    && butterflyRoute.Routes[0].MapId == 6512
                    && butterflyRoute.Routes[1].Direction
                        == DungeonBossRouteDirection.Right
                    && butterflyRoute.Routes[1].MapId == 6513,
                ref failures);

            run.RoomKey = new RoomKey(3, 1, -1);
            var routeOverrideMapId = -1;
            var routeApplied = SpecialDungeonRunCoordinator
                .TryApplyBossRouteOverride(
                    run,
                    new DungeonRoomPoint(4, 1),
                    ref routeOverrideMapId);
            Check(
                "Ancient Tomb actual route overrides its quest-bound fallback",
                routeApplied
                    && routeOverrideMapId == 8213
                    && run.SelectedBossMapId == 8213
                    && run.Instance.Mechanisms.BossRoute?.SelectedMapId == 8213,
                ref failures);

            var partyRun = new DungeonRun(
                run.Instance,
                runId: 2,
                runGeneration: 1,
                initialState: DungeonRunState.Active);
            SpecialDungeonRunCoordinator.CloneSelectionState(run, partyRun);
            SpecialDungeonRunCoordinator.CopyBossRouteStateForPartyMove(
                run,
                partyRun);
            var replayOverrideMapId = -1;
            partyRun.RoomKey = new RoomKey(4, 2, -1);
            var replayApplied = SpecialDungeonRunCoordinator
                .TryApplyBossRouteOverride(
                    partyRun,
                    new DungeonRoomPoint(4, 1),
                    ref replayOverrideMapId);
            Check(
                "Party participants share one immutable Boss route result",
                partyRun.SelectedBossMapId == 8213
                    && replayApplied
                    && replayOverrideMapId == 8213
                    && run.Instance.Mechanisms.BossRoute?.SelectedDirection
                        == DungeonBossRouteDirection.Left,
                ref failures);

            var timeCrackMaze = GameWorld.Dungeon.GetDungeonMaze(2007, 0);
            var timeCrackRun = new DungeonRun(2007, 0) { MazeIndex = 0 };
            SpecialDungeonRunCoordinator.ConfigureSelection(
                timeCrackRun,
                timeCrackMaze,
                new[] { 9, 0 },
                Array.Empty<ActiveQuest>());
            Check(
                "Ordinary random Boss pools do not become route-bound",
                timeCrackRun.Instance.Mechanisms.BossRoute == null,
                ref failures);
        }

        private static SpecialDungeonRuntime CreateSpecialDungeonRuntime(
            int dungeonId)
            => SpecialDungeonDefinitionCatalog.TryGet(
                dungeonId,
                out var definition)
                ? new SpecialDungeonRuntime(definition)
                : null;

        private static bool BytesEqual(byte[] actual, params byte[] expected)
        {
            if (actual == null || actual.Length != expected.Length)
                return false;

            for (var i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expected[i])
                    return false;
            }

            return true;
        }

        private static void Check(
            string name,
            bool ok,
            ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
