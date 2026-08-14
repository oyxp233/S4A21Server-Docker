using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Handlers.Pets;

namespace DfoServer.SelfTests
{
    // 锁定 DungeonRun 状态模型的关键语义:
    // 1. 新局字段默认值 = 旧版"返城重置后"的取值(常量表);
    // 2. Begin/End 生命周期与幂等性;
    // 3. 跨局字段(华丽挑战开关)不随 run 重建;
    // 4. 翻牌定时器句柄随局取消, 换局时旧句柄必被取消。
    public static class DungeonRunLifecycleSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_RUN_LIFECYCLE selftest ===");
            var failures = 0;

            using var client = new TcpClient();
            var session = new EnhancedClientSession(client, new GamePacketHeader());
            var player = session.Player;

            // 1. 初始无局; 塔便捷入口跟随
            Check("fresh session has no run",
                player.CurrentRun == null
                && player.DeathTowerState == null
                && !player.IsInDeathTower,
                ref failures);

            Check("town map dungeon gate resolves to an adjacent movable return point",
                GameWorld.Town.TryFindDungeonGateReturnPosition(
                    new[] { 1323, 157, 30, 258, 17, 1, 340, 218, 30, 120, -1, -1 },
                    new[] { 617, 177, 750, 230, 346, 228, 280, 100, 555, 307, 280, 100 },
                    out var dungeonGateX,
                    out var dungeonGateY)
                && dungeonGateX == 378
                && dungeonGateY == 278,
                ref failures);
            Check("Saint Horn dungeon-gate return anchor is loaded through TOWN and MAP PVF data",
                GameWorld.Town.TryGetDungeonGateReturnInfo(
                    townId: 17,
                    areaId: 2,
                    out var saintHornReturn)
                && saintHornReturn.Town == 17
                && saintHornReturn.Area == 2
                && saintHornReturn.X == 378
                && saintHornReturn.Y == 278,
                ref failures);

            Check("ordinary quest-opened dungeons remain persistent products",
                !GameWorld.WorldMap.IsTaskExclusiveDungeon(70)
                && !GameWorld.WorldMap.IsTaskExclusiveDungeon(2014)
                && GameWorld.WorldMap.ShouldPersistDungeonPermission(70)
                && GameWorld.WorldMap.ShouldPersistDungeonPermission(2014),
                ref failures);
            Check("quest asset dungeons are transient and reject stale permissions",
                GameWorld.WorldMap.IsTaskExclusiveDungeon(515)
                && GameWorld.WorldMap.IsTaskExclusiveDungeon(518)
                && GameWorld.WorldMap.IsTaskExclusiveDungeon(3066)
                && GameWorld.WorldMap.IsTaskExclusiveDungeon(522)
                && !GameWorld.WorldMap.ShouldPersistDungeonPermission(522)
                && !GameWorld.WorldMap.IsTaskExclusiveDungeonAvailable(
                    522,
                    new HashSet<int>()),
                ref failures);
            Check("an active quest dungeon-info reference authorizes its transient dungeon",
                GameWorld.QuestData.ReferencesDungeon(2602, 522)
                && GameWorld.WorldMap.IsTaskExclusiveDungeonAvailable(
                    522,
                    new HashSet<int> { 2602 }),
                ref failures);

            var returnAck = Network.Handlers.TownHandler
                .BuildReturnToTownSuccessPacket(0x002A);
            Check("return-to-town CMD result keeps the A14 success envelope",
                returnAck.Length == 16
                && returnAck[0] == 0x01
                && BitConverter.ToUInt16(returnAck, 1) == 0x002A
                && returnAck[15] == 0x01,
                ref failures);
            // 2. 新局字段默认值 = 旧版返城重置后的取值(常量表)
            var fresh = new DungeonRun();
            Check("fresh run fields carry legacy reset defaults",
                fresh.DungeonId == 0
                && fresh.Phase == DungeonRunPhase.None
                && fresh.MazeIndex == -1
                && fresh.LayeredMapIndex == -1
                && fresh.MazeStartX == -1
                && fresh.MazeStartY == -1
                && !fresh.HellMode
                && fresh.HellMapId == -1
                && fresh.HellMapX == 0xFF
                && fresh.HellMapY == 0xFF
                && fresh.RoomMonsters.Count == 0
                && fresh.RoomKilledSeqIds.Count == 0
                && fresh.RoomStates.Count == 0
                && fresh.Drops.Count == 0
                && fresh.CardRewards == null
                && fresh.FreeCardSlots.Length == 4 && fresh.FreeCardSlots[0] == 0xFF
                && fresh.PaidCardSlots[3] == 0xFF
                && !fresh.IsWaitingDeathRespawn
                && fresh.DeathRespawnAvailableAt == DateTime.MinValue
                && fresh.Tower == null,
                ref failures);

            fresh.Selection.MazeIndex = 7;
            fresh.Combat.TotalExp = 123;
            fresh.Settlement.CardFlipCount = 2;
            fresh.QuestBridge.Snapshot = QuestRunSnapshot.Empty;
            Check("DungeonRun compatibility properties project composed state",
                fresh.MazeIndex == 7
                && fresh.TotalExp == 123
                && fresh.CardFlipCount == 2
                && ReferenceEquals(fresh.QuestSnapshot, QuestRunSnapshot.Empty)
                && fresh.Timers != null
                && fresh.Mechanisms != null,
                ref failures);

            CheckTowerSettlementPolicy(ref failures);
            // 3. Selection identity exists before a run and its participant anchor
            // is copied into the run that follows.
            var selectionAnchor = new DungeonTownReturnAnchor(
                townId: 13,
                areaId: 2,
                x: 378,
                y: 278,
                direction: 5,
                areaState: 3);
            var selection = player.BeginDungeonSelection(selectionAnchor);
            var repeatedSelection = player.BeginDungeonSelection(selectionAnchor);
            Check("dungeon selection has one no-run identity and deduplicates return",
                selection != null
                && ReferenceEquals(selection, repeatedSelection)
                && player.IsCurrentDungeonSelection(selection)
                && selection.TryBeginReturn()
                && !selection.TryBeginReturn(),
                ref failures);
            Check("a returning selection cannot atomically start a dungeon run",
                !DungeonRunLifecycle.BeginRun(
                    session,
                    1002,
                    1,
                    expectedSelection: selection)
                && player.CurrentRun == null
                && player.IsCurrentDungeonSelection(selection),
                ref failures);
            selection.CancelReturn();

            // 4. BeginRun 建立新局
            var beganFromSelection = DungeonRunLifecycle.BeginRun(
                session,
                1002,
                1,
                expectedSelection: selection);
            var run = player.CurrentRun;
            Check("BeginRun creates run with entry params",
                beganFromSelection
                && run != null
                && run.DungeonId == 1002
                && run.Difficulty == 1
                && run.Phase == DungeonRunPhase.InProgress
                && run.TownReturnAnchor.IsValid
                && run.TownReturnAnchor.TownId == 13
                && run.TownReturnAnchor.AreaId == 2
                && run.TownReturnAnchor.X == 378
                && run.TownReturnAnchor.Y == 278,
                ref failures);
            Check("new run invalidates the preceding dungeon selection identity",
                player.CurrentDungeonSelection == null
                && !player.IsCurrentDungeonSelection(selection),
                ref failures);

            var markerRun = new DungeonRun(1002, 0);
            Check("clear-map quest sync marker deduplicates by dungeon and map",
                markerRun.TryMarkClearMapQuestSynced(0, 33060)
                && !markerRun.TryMarkClearMapQuestSynced(0, 33060)
                && markerRun.TryMarkClearMapQuestSynced(0, 33061)
                && markerRun.TryMarkClearMapQuestSynced(1002, 33060),
                ref failures);
            markerRun.UnmarkClearMapQuestSynced(0, 33060);
            Check("failed clear-map quest sync can release its run marker",
                markerRun.TryMarkClearMapQuestSynced(0, 33060),
                ref failures);

            CheckP0StateAndEffectSemantics(session, ref failures);

            // 5. 跨局字段不随 run 重建
            player.HellPartyGorgeousChallengeEnabled = true;
            DungeonRunLifecycle.EndRunOnTeardown(session, "selftest");
            Check("teardown clears run",
                player.CurrentRun == null, ref failures);
            player.CurTownId = 1;
            player.CurAreaId = 1;
            player.CurPosX = 1;
            player.CurPosY = 1;
            player.CurDirection = 1;
            player.CurAreaState = 1;
            DungeonRunLifecycle.ApplyTownReturnAnchor(
                player,
                run.TownReturnAnchor,
                listenerGamePort: 0);
            Check("all return paths project the captured dungeon-gate anchor",
                player.CurTownId == 13
                && player.CurAreaId == 2
                && player.CurPosX == 378
                && player.CurPosY == 278
                && player.CurDirection == 5
                && player.CurAreaState == 3,
                ref failures);
            Check("cross-run fields survive teardown",
                player.HellPartyGorgeousChallengeEnabled,
                ref failures);

            // 5. 无局时 End 幂等不抛
            var idempotentOk = true;
            try
            {
                DungeonRunLifecycle.EndRunOnTeardown(session, "selftest-again");
                DungeonRunLifecycle.EndRunToTownAsync(session).GetAwaiter().GetResult();
            }
            catch { idempotentOk = false; }
            Check("End without run is idempotent", idempotentOk, ref failures);

            player.CharacterId = 1001;
            LinkedDungeonEntryAuthorizationStore.Grant(
                player,
                sourceDungeonId: 76,
                targetDungeonId: 301,
                difficulty: 2);
            player.CharacterId = 0;
            DungeonRunLifecycle.EndRunToTownAsync(session).GetAwaiter().GetResult();
            Check("town transition preserves one-shot linked authorization",
                LinkedDungeonEntryAuthorizationStore.HasPending(player),
                ref failures);
            DungeonRunLifecycle.EndRunOnTeardown(session, "linked-auth-selftest");
            Check("disconnect or character teardown clears linked authorization",
                !LinkedDungeonEntryAuthorizationStore.HasPending(player),
                ref failures);

            // 6. 翻牌定时器句柄: 取消置空 + 换局时旧句柄必被取消
            DungeonRunLifecycle.BeginRun(session, 1002, 0);
            var firstRun = player.CurrentRun;
            var handle = ClockService.Instance.ScheduleOneShot(
                "selftest:auto-flip:" + Guid.NewGuid().ToString("N"),
                DateTime.UtcNow.AddHours(1),
                _ => { });
            var autoFlipTicket = firstRun.Timers.Begin(
                DungeonRunTimerKeys.SettlementCardAutoFlow);
            firstRun.Timers.Attach(autoFlipTicket, handle);
            DungeonRunLifecycle.CancelAutoFlip(session);
            Check("CancelAutoFlip invalidates its registry ticket and cancels the handle",
                !firstRun.Timers.IsCurrent(autoFlipTicket)
                && firstRun.Timers.GetGeneration(
                    DungeonRunTimerKeys.SettlementCardAutoFlow)
                    == autoFlipTicket.Generation + 1
                && !handle.Cancel(),
                ref failures);

            var deathHandle = ClockService.Instance.ScheduleOneShot(
                "selftest:death-respawn:" + Guid.NewGuid().ToString("N"),
                DateTime.UtcNow.AddHours(1),
                _ => { });
            firstRun.IsWaitingDeathRespawn = true;
            firstRun.DeathRespawnAvailableAt = DateTime.UtcNow.AddHours(1);
            var deathTicket = firstRun.Timers.Begin(
                DungeonRunTimerKeys.CombatDeathRespawn);
            firstRun.Timers.Attach(deathTicket, deathHandle);
            DungeonRunLifecycle.CancelDeathRespawn(session);
            Check("CancelDeathRespawn invalidates its registry ticket and state",
                !firstRun.Timers.IsCurrent(deathTicket)
                && firstRun.Timers.GetGeneration(
                    DungeonRunTimerKeys.CombatDeathRespawn)
                    == deathTicket.Generation + 1
                && !firstRun.IsWaitingDeathRespawn
                && firstRun.DeathRespawnAvailableAt == DateTime.MinValue
                && !deathHandle.Cancel(),
                ref failures);

            var staleHandle = ClockService.Instance.ScheduleOneShot(
                "selftest:auto-flip:" + Guid.NewGuid().ToString("N"),
                DateTime.UtcNow.AddHours(1),
                _ => { });
            var staleDeathHandle = ClockService.Instance.ScheduleOneShot(
                "selftest:death-respawn:" + Guid.NewGuid().ToString("N"),
                DateTime.UtcNow.AddHours(1),
                _ => { });
            var staleAutoFlipTicket = firstRun.Timers.Begin(
                DungeonRunTimerKeys.SettlementCardAutoFlow);
            firstRun.Timers.Attach(staleAutoFlipTicket, staleHandle);
            firstRun.IsWaitingDeathRespawn = true;
            firstRun.DeathRespawnAvailableAt = DateTime.UtcNow.AddHours(1);
            var staleDeathTicket = firstRun.Timers.Begin(
                DungeonRunTimerKeys.CombatDeathRespawn);
            firstRun.Timers.Attach(staleDeathTicket, staleDeathHandle);
            var staleIdentity = firstRun.CaptureIdentity();
            var staleGeneration = firstRun.RunGeneration;
            DungeonRunLifecycle.BeginRun(session, 1003, 0);
            Check("BeginRun cancels the previous run timer and swaps the run",
                !staleHandle.Cancel()
                && !staleDeathHandle.Cancel()
                && !firstRun.Timers.IsCurrent(staleAutoFlipTicket)
                && !firstRun.Timers.IsCurrent(staleDeathTicket)
                && !ReferenceEquals(player.CurrentRun, firstRun)
                && player.CurrentRun.DungeonId == 1003,
                ref failures);
            Check("new run generation permanently invalidates old continuations",
                player.CurrentRun.RunGeneration > staleGeneration
                && !player.IsCurrentDungeonRun(staleIdentity)
                && firstRun.RunState == DungeonRunState.Ended
                && !firstRun.TryActivate(),
                ref failures);
            var replacementRun = player.CurrentRun;
            Check("stale end request cannot detach a replacement run",
                !DungeonRunLifecycle.TryEndRunToTownAsync(session, staleIdentity)
                    .GetAwaiter()
                    .GetResult()
                && ReferenceEquals(player.CurrentRun, replacementRun),
                ref failures);
            Check("stale pet continuation cannot finish town cleanup",
                !PetCreatureRuntimeService.CanCompleteEndedRun(
                    session,
                    staleIdentity),
                ref failures);

            // 7. 塔局: 挂 Tower 载荷, 返城随局消失
            var tower = new Game.DeathTower.DeathTowerSession(
                DeathTowerSelfTestFactory.CreateConfig(
                    11000,
                    new[] { 1, 2, 3 },
                    50));
            DungeonRunLifecycle.BeginTowerRun(session, 11000, tower);
            tower.BeginStage(123, new[]
            {
                new Game.DeathTower.StageTowerItem
                {
                    SourceMonsterUniqueId = 7,
                    ItemUniqueId = 9,
                    ItemId = 6515,
                    DropRate = 10000,
                    StackCount = 1,
                },
            });
            tower.GenerateDropsForMonster(7);
            tower.TryPickupGroundItem(9, out _);
            Check("BeginTowerRun mounts tower payload",
                player.IsInDeathTower
                && ReferenceEquals(player.DeathTowerState, tower)
                && player.CurrentRun.DungeonId == 11000
                && tower.InventoryItems.Count == 1,
                ref failures);

            DungeonRunLifecycle.EndRunToTownAsync(session).GetAwaiter().GetResult();
            Check("EndRunToTown clears run and tower",
                player.CurrentRun == null && !player.IsInDeathTower, ref failures);

            var replacementTower = CreateTowerWithPickedItem();
            DungeonRunLifecycle.BeginTowerRun(session, 11000, replacementTower);
            DungeonRunLifecycle.BeginRun(session, 1002, 0);
            Check("starting another dungeon discards tower inventory with the old run",
                player.CurrentRun != null
                    && player.CurrentRun.Tower == null
                    && player.CurrentRun.DungeonId == 1002,
                ref failures);

            var teardownTower = CreateTowerWithPickedItem();
            DungeonRunLifecycle.BeginTowerRun(session, 11000, teardownTower);
            DungeonRunLifecycle.EndRunOnTeardown(session, "tower-selftest");
            Check("disconnect or character teardown discards tower inventory",
                player.CurrentRun == null && !player.IsInDeathTower,
                ref failures);

            var staleTower = CreateTowerWithPickedItem();
            var freshTower = new Game.DeathTower.DeathTowerSession(staleTower.Config);
            DungeonRunLifecycle.BeginTowerRun(session, 11000, staleTower);
            DungeonRunLifecycle.BeginTowerRun(session, 11000, freshTower);
            Check("starting a new tower run replaces the old temporary inventory",
                ReferenceEquals(player.DeathTowerState, freshTower)
                    && freshTower.InventoryItems.Count == 0,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static Game.DeathTower.DeathTowerSession CreateTowerWithPickedItem()
        {
            var tower = new Game.DeathTower.DeathTowerSession(
                DeathTowerSelfTestFactory.CreateConfig(
                    11000,
                    new[] { 1 },
                    50));
            tower.BeginStage(456, new[]
            {
                new Game.DeathTower.StageTowerItem
                {
                    SourceMonsterUniqueId = 10,
                    ItemUniqueId = 11,
                    ItemId = 6515,
                    DropRate = 10000,
                    StackCount = 1,
                },
            });
            tower.GenerateDropsForMonster(10);
            if (!tower.TryPickupGroundItem(11, out _))
                throw new InvalidOperationException("tower lifecycle fixture pickup failed");
            return tower;
        }

        private static void CheckP0StateAndEffectSemantics(
            EnhancedClientSession session,
            ref int failures)
        {
            const int workerCount = 32;
            var run = new DungeonRun(1002, 0);
            var facts = new ConcurrentBag<DungeonClearedFact>();
            var createdCount = 0;
            Parallel.For(0, workerCount, index =>
            {
                var source = DungeonEventEnvelope.Create(
                    run,
                    sourcePlayerId: 1,
                    cause: $"parallel-clear-{index}");
                var fact = run.Instance.GetOrCreateClearedFact(
                    new DungeonClearIntent(source, source.Cause, 0),
                    out var created);
                if (created)
                    Interlocked.Increment(ref createdCount);
                facts.Add(fact);
            });

            var clearFact = run.Instance.ClearedFact;
            Check("concurrent clear intents create one shared DungeonCleared fact",
                createdCount == 1
                && clearFact != null
                && facts.Count == workerCount
                && facts.All(fact => ReferenceEquals(fact, clearFact)),
                ref failures);

            var clearTransitions = 0;
            Parallel.For(0, workerCount, _ =>
            {
                if (run.TryBeginClearCommit(clearFact))
                    Interlocked.Increment(ref clearTransitions);
            });
            Check("concurrent clear transition has one first caller and resumable token",
                clearTransitions == 1
                && run.CanResumeClearCommit(clearFact)
                && run.TryCompleteClearCommit(clearFact)
                && !run.TryCompleteClearCommit(clearFact),
                ref failures);

            var settlementTransitions = 0;
            Parallel.For(0, workerCount, _ =>
            {
                if (run.TryBeginSettlementPreparation())
                    Interlocked.Increment(ref settlementTransitions);
            });
            Check("duplicate settlement preparation is a no-op with one executor state",
                settlementTransitions == 1
                && run.CanResumeSettlementPreparation()
                && run.TryMarkResultShown()
                && !run.TryMarkResultShown()
                && run.TryCompleteSettlement(),
                ref failures);

            var effectId = new DungeonEffectId(
                clearFact.SourceEventId,
                "parallel-effect",
                DungeonEffectScope.Instance,
                run.PartyDungeonInstanceId);
            var reservations = new ConcurrentBag<DungeonEffectReservation>();
            Parallel.For(0, workerCount, _ =>
            {
                if (run.Instance.Effects.TryReserve(effectId, out var reservation))
                    reservations.Add(reservation);
            });
            var onlyReservation = reservations.SingleOrDefault();
            Check("parallel effect reservation elects exactly one executor",
                reservations.Count == 1
                && onlyReservation.IsValid
                && run.Instance.Effects.TryCommit(onlyReservation)
                && !run.Instance.Effects.TryReserve(effectId, out _),
                ref failures);

            var retryEffectId = new DungeonEffectId(
                Guid.NewGuid(),
                "persistent-retry",
                DungeonEffectScope.Persistent,
                run.RunId);
            var firstReserved = run.Effects.TryReserve(
                retryEffectId,
                out var failedReservation);
            var failed = run.Effects.TryFail(failedReservation);
            var retried = run.Effects.TryReserve(
                retryEffectId,
                out var retryReservation);
            Check("failed effect releases its lease and retries with the same stable id",
                firstReserved
                && failed
                && retried
                && !run.Effects.TryCommit(failedReservation)
                && run.Effects.TryCommit(retryReservation)
                && run.Effects.GetState(retryEffectId) == DungeonEffectState.Committed,
                ref failures);

            var sharedInstance = new DungeonInstance(1003, 1);
            var leaderRun = new DungeonRun(
                sharedInstance,
                DungeonIdentityGenerator.NextRunId(),
                runGeneration: 1,
                DungeonRunState.Active);
            var memberRun = new DungeonRun(
                sharedInstance,
                DungeonIdentityGenerator.NextRunId(),
                runGeneration: 1,
                DungeonRunState.Active);
            var actorMaze = new GameWorld.Dungeon.MazeSumInfo
            {
                X = 2,
                Y = 0,
                Index = 17714,
                Monsters = new List<GameWorld.Dungeon.MonsterSumInfo>
                {
                    new GameWorld.Dungeon.MonsterSumInfo
                    {
                        Code = 56763,
                        Level = 88,
                        Type = 0,
                    },
                },
                SpecialPassiveObjects = new List<PvfLib.SpecialPassiveObjectInfo>
                {
                    new PvfLib.SpecialPassiveObjectInfo { ObjectCode = 9001 },
                    new PvfLib.SpecialPassiveObjectInfo { ObjectCode = 9001 },
                },
            };
            var actorRoomKey = new RoomKey(2, 0, -1);
            var actorRoom = sharedInstance.GetOrCreateRoom(
                actorRoomKey,
                roomId => new DungeonInstanceRoom(
                    roomId,
                    actorRoomKey,
                    actorMaze,
                    seed: 1234,
                    firstActorSequenceId: 43210),
                out var actorRoomCreated);
            var sameActorRoom = sharedInstance.GetOrCreateRoom(
                actorRoomKey,
                roomId => new DungeonInstanceRoom(
                    roomId,
                    actorRoomKey,
                    actorMaze,
                    seed: 5678,
                    firstActorSequenceId: 12345),
                out var sameActorRoomCreated);
            var actorStartMap = Network.Builders.DungeonNotificationBuilder.BuildStartMap(
                actorRoom.Maze,
                actorRoom.FirstActorSequenceId,
                randomSeed: (int)actorRoom.Seed);
            Check("shared room freezes one actor sequence range for every participant",
                actorRoomCreated
                && !sameActorRoomCreated
                && ReferenceEquals(actorRoom, sameActorRoom)
                && actorRoom.FirstActorSequenceId == 43210
                && BitConverter.ToUInt16(actorStartMap, 23) == 43210,
                ref failures);
            leaderRun.SetCurrentRoom(actorRoom);
            var firstMapOwnedDeath = actorRoom
                .TryRecordNextMapOwnedPassiveObjectDeath(
                    DungeonEventEnvelope.Create(
                        leaderRun,
                        sourcePlayerId: 1,
                        cause: "first map-owned passive object"),
                    actorCode: 9001,
                    out var firstMapOwnedActorDefined);
            var secondMapOwnedDeath = actorRoom
                .TryRecordNextMapOwnedPassiveObjectDeath(
                    DungeonEventEnvelope.Create(
                        leaderRun,
                        sourcePlayerId: 1,
                        cause: "second map-owned passive object"),
                    actorCode: 9001,
                    out var secondMapOwnedActorDefined);
            var exhaustedMapOwnedDeath = actorRoom
                .TryRecordNextMapOwnedPassiveObjectDeath(
                    DungeonEventEnvelope.Create(
                        leaderRun,
                        sourcePlayerId: 1,
                        cause: "exhausted map-owned passive object"),
                    actorCode: 9001,
                    out var exhaustedMapOwnedActorDefined);
            Check("MAP-owned passive objects keep an instance ledger without wire sequences",
                firstMapOwnedActorDefined
                && firstMapOwnedDeath.Accepted
                && firstMapOwnedDeath.Created
                && firstMapOwnedDeath.Fact.SequenceId == 0
                && secondMapOwnedActorDefined
                && secondMapOwnedDeath.Accepted
                && secondMapOwnedDeath.Created
                && secondMapOwnedDeath.Fact.SequenceId == 0
                && exhaustedMapOwnedActorDefined
                && !exhaustedMapOwnedDeath.Accepted,
                ref failures);
            var bossPosition = new[] { 5, 4 };
            var ridableObjects = new[]
            {
                new RidableObjectSpawnEntry { ObjectIndex = 11, MapX = 2, MapY = 1 },
            };
            var clearConditionEntry = new PvfLib.ClearConditionEntry
            {
                Type = 2,
                TargetId = 123,
                Count = 1,
            };
            var selection = new DungeonSelectionSnapshot
            {
                MazeIndex = 7,
                MazeStartMapId = 33060,
                MazeStartX = 2,
                MazeStartY = 1,
                BossMapPosition = bossPosition,
                RidableObjects = ridableObjects,
                ClearConditionTemplate = new ClearConditionState(
                    new List<PvfLib.ClearConditionEntry> { clearConditionEntry }),
            };
            sharedInstance.TryFreezeSelection(selection);
            bossPosition[0] = 99;
            ridableObjects[0].ObjectIndex = 99;
            clearConditionEntry.Count = 99;
            var exposedBossPosition = sharedInstance.Selection.BossMapPosition;
            exposedBossPosition[0] = 88;
            sharedInstance.Selection.ClearConditionTemplate.Check(2, 123);
            sharedInstance.Selection.ApplyTo(leaderRun);
            sharedInstance.Selection.ApplyTo(memberRun);
            leaderRun.QuestSnapshot = QuestRunSnapshot.Capture(
                new List<ActiveQuest>
                {
                    new ActiveQuest { Slot = 0, QuestId = 100, TriggerValue = 1 },
                });
            memberRun.QuestSnapshot = QuestRunSnapshot.Capture(
                new List<ActiveQuest>
                {
                    new ActiveQuest { Slot = 0, QuestId = 200, TriggerValue = 1 },
                });

            var worldEffectId = new DungeonEffectId(
                Guid.NewGuid(),
                "open-world-door",
                DungeonEffectScope.Instance,
                sharedInstance.PartyDungeonInstanceId);
            var worldFirst = sharedInstance.Effects.TryReserve(
                worldEffectId,
                out var worldReservation);
            var worldCommitted = sharedInstance.Effects.TryCommit(worldReservation);
            var playerEventId = Guid.NewGuid();
            var leaderEffectId = new DungeonEffectId(
                playerEventId,
                "quest-progress",
                DungeonEffectScope.Player,
                leaderRun.RunId);
            var memberEffectId = new DungeonEffectId(
                playerEventId,
                "quest-progress",
                DungeonEffectScope.Player,
                memberRun.RunId);
            var leaderReserved = leaderRun.Effects.TryReserve(
                leaderEffectId,
                out var leaderReservation);
            var memberReserved = memberRun.Effects.TryReserve(
                memberEffectId,
                out var memberReservation);
            Check("shared map stays identical while participant quest snapshots remain personal",
                leaderRun.PartyDungeonInstanceId == memberRun.PartyDungeonInstanceId
                && leaderRun.MazeIndex == memberRun.MazeIndex
                && leaderRun.MazeStartMapId == memberRun.MazeStartMapId
                && leaderRun.BossMapPos.SequenceEqual(memberRun.BossMapPos)
                && leaderRun.BossMapPos.SequenceEqual(new[] { 5, 4 })
                && leaderRun.RidableObjects.Count == 1
                && leaderRun.RidableObjects[0].ObjectIndex == 11
                && leaderRun.ClearCondition.TotalRequired == 1
                && memberRun.ClearCondition.TotalRequired == 1
                && leaderRun.QuestSnapshot.Contains(100)
                && !leaderRun.QuestSnapshot.Contains(200)
                && memberRun.QuestSnapshot.Contains(200)
                && !memberRun.QuestSnapshot.Contains(100),
                ref failures);
            Check("world effect commits once and personal effect commits once per participant",
                worldFirst
                && worldCommitted
                && !sharedInstance.Effects.TryReserve(worldEffectId, out _)
                && leaderReserved
                && memberReserved
                && leaderRun.Effects.TryCommit(leaderReservation)
                && memberRun.Effects.TryCommit(memberReservation),
                ref failures);

            var questRun = new DungeonRun(1002, 0);
            var firstQuestRoom = new DungeonInstanceRoom(
                1001,
                new RoomKey(1, 1, 0),
                default,
                1);
            var nextQuestRoom = new DungeonInstanceRoom(
                1002,
                new RoomKey(2, 1, 0),
                default,
                2);
            questRun.SetCurrentRoom(firstQuestRoom);
            var questSource = DungeonEventEnvelope.Create(
                questRun,
                sourcePlayerId: 1,
                cause: "quest set-trigger selftest");
            Check("quest completion accepts only its captured run and room",
                DungeonSettlementHandler.IsQuestCompletionSourceCurrent(
                    questRun,
                    questSource),
                ref failures);
            questRun.SetCurrentRoom(nextQuestRoom);
            Check("quest completion from a previous room cannot clear the current room",
                !DungeonSettlementHandler.IsQuestCompletionSourceCurrent(
                    questRun,
                    questSource),
                ref failures);
            var replacementQuestRun = new DungeonRun(1002, 0);
            replacementQuestRun.SetCurrentRoom(firstQuestRoom);
            Check("quest completion from a previous run cannot clear a replacement run",
                !DungeonSettlementHandler.IsQuestCompletionSourceCurrent(
                    replacementQuestRun,
                    questSource),
                ref failures);

            var checkpointRun = session.Player.CurrentRun;
            var checkpointIdentity = checkpointRun.CaptureIdentity();
            var persistedExecutions = 0;
            var downstreamExecutions = 0;
            var firstPersist = DungeonSettlementHandler
                .ExecuteSettlementEffectAsync(
                    session,
                    checkpointRun,
                    checkpointIdentity,
                    "selftest-persistent-checkpoint",
                    () =>
                    {
                        Interlocked.Increment(ref persistedExecutions);
                        return Task.CompletedTask;
                    })
                .GetAwaiter()
                .GetResult();
            var downstreamFailed = false;
            try
            {
                DungeonSettlementHandler.ExecuteSettlementEffectAsync(
                        session,
                        checkpointRun,
                        checkpointIdentity,
                        "selftest-downstream-checkpoint",
                        () =>
                        {
                            Interlocked.Increment(ref downstreamExecutions);
                            throw new InvalidOperationException(
                                "injected downstream failure");
                        })
                    .GetAwaiter()
                    .GetResult();
            }
            catch (InvalidOperationException)
            {
                downstreamFailed = true;
            }

            var replayPersist = DungeonSettlementHandler
                .ExecuteSettlementEffectAsync(
                    session,
                    checkpointRun,
                    checkpointIdentity,
                    "selftest-persistent-checkpoint",
                    () =>
                    {
                        Interlocked.Increment(ref persistedExecutions);
                        return Task.CompletedTask;
                    })
                .GetAwaiter()
                .GetResult();
            var retryDownstream = DungeonSettlementHandler
                .ExecuteSettlementEffectAsync(
                    session,
                    checkpointRun,
                    checkpointIdentity,
                    "selftest-downstream-checkpoint",
                    () =>
                    {
                        Interlocked.Increment(ref downstreamExecutions);
                        return Task.CompletedTask;
                    })
                .GetAwaiter()
                .GetResult();
            Check("settlement retry skips committed persistence and resumes failed downstream effect",
                firstPersist
                && downstreamFailed
                && replayPersist
                && retryDownstream
                && persistedExecutions == 1
                && downstreamExecutions == 2,
                ref failures);
        }

        private static void CheckTowerSettlementPolicy(ref int failures)
        {
            var paidPolicy = typeof(DungeonSettlementHandler).GetMethod(
                "ShouldGeneratePaidCardRewards",
                BindingFlags.Static | BindingFlags.NonPublic);
            Check("tower settlement has a dedicated paid-card policy",
                paidPolicy != null, ref failures);
            if (paidPolicy != null)
            {
                Check("tower of despair does not generate an invisible paid-card reward",
                    !(bool)paidPolicy.Invoke(null, new object[] { 11008 }), ref failures);
                Check("ordinary dungeons retain paid-card rewards",
                    (bool)paidPolicy.Invoke(null, new object[] { 1002 }), ref failures);
            }

            var cardFlowPolicy = typeof(DungeonSettlementHandler).GetMethod(
                "ShouldScheduleCardRewardFlow",
                BindingFlags.Static | BindingFlags.NonPublic);
            Check("tower settlement exposes the standard card-flow policy",
                cardFlowPolicy != null, ref failures);
            if (cardFlowPolicy != null)
            {
                Check("tower of despair skips the delayed card layout and auto-flip",
                    !(bool)cardFlowPolicy.Invoke(null, new object[] { 11008 }), ref failures);
                Check("ordinary dungeons retain delayed card layout and auto-flip",
                    (bool)cardFlowPolicy.Invoke(null, new object[] { 1002 }), ref failures);
            }

            var rewardFactory = typeof(DungeonSettlementHandler).GetMethod(
                "BuildTowerOfDespairRewardCandidates",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(int),
                    typeof(Func<ClearRewardGenerator.CardReward>)
                },
                null);
            Check("tower settlement exposes the original ten-slot reward policy",
                rewardFactory != null, ref failures);
            if (rewardFactory != null)
            {
                var nextItemId = 2600000;
                var randomFactoryCallCount = 0;
                Func<ClearRewardGenerator.CardReward> randomReward = () =>
                    {
                        randomFactoryCallCount++;
                        return new ClearRewardGenerator.CardReward
                        {
                            ItemId = ++nextItemId,
                            StackCount = 1,
                        };
                    };
                var floor16 = (IReadOnlyList<ClearRewardGenerator.CardReward>)
                    rewardFactory.Invoke(null, new object[] { 16, randomReward });
                var floor16FactoryCalls = randomFactoryCallCount;
                randomFactoryCallCount = 0;
                var floor10 = (IReadOnlyList<ClearRewardGenerator.CardReward>)
                    rewardFactory.Invoke(null, new object[] { 10, randomReward });
                var floor10FactoryCalls = randomFactoryCallCount;
                randomFactoryCallCount = 0;
                var floor100 = (IReadOnlyList<ClearRewardGenerator.CardReward>)
                    rewardFactory.Invoke(null, new object[] { 100, randomReward });
                var floor100FactoryCalls = randomFactoryCallCount;

                Check("ordinary despair floor rolls five item rewards",
                    floor16FactoryCalls == 5
                    && floor16.Count == 5,
                    ref failures);
                Check("player-mirror despair floor rolls nine items plus the synthesizer",
                    floor10FactoryCalls == 9
                    && floor10.Count == 10
                    && floor10[9].ItemId == 1252
                    && floor10[9].StackCount == 1,
                    ref failures);
                Check("floor 100 rolls five items plus the completion medal",
                    floor100FactoryCalls == 5
                    && floor100.Count == 6
                    && floor100[5].ItemId == 3314
                    && floor100[5].StackCount == 1,
                    ref failures);

                var fallbackFactoryCalls = 0;
                Func<ClearRewardGenerator.CardReward> goldFallback = () =>
                {
                    fallbackFactoryCalls++;
                    return new ClearRewardGenerator.CardReward
                    {
                        IsGold = true,
                        GoldAmount = 123,
                    };
                };
                var fallbackRewards =
                    (IReadOnlyList<ClearRewardGenerator.CardReward>)
                    rewardFactory.Invoke(
                        null,
                        new object[] { 16, goldFallback });
                Check("invalid item fallbacks remain empty instead of becoming gold",
                    fallbackFactoryCalls == 5
                    && fallbackRewards.Count == 0,
                    ref failures);
            }

            var builder = typeof(DungeonSettlementHandler).GetMethod(
                "TryBuildTowerOfDespairClearRewardWithTime",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(int), typeof(uint),
                    typeof(IReadOnlyList<ClearRewardGenerator.CardReward>),
                    typeof(byte[]).MakeByRefType()
                },
                null);
            Check("tower settlement derives the displayed floor from the cleared dungeon",
                builder != null, ref failures);
            if (builder == null)
                return;

            var rewards = new List<ClearRewardGenerator.CardReward>();
            for (var i = 0; i < 5; i++)
            {
                rewards.Add(new ClearRewardGenerator.CardReward
                {
                    ItemId = 2600001 + i,
                    StackCount = i + 1,
                });
            }
            var args = new object[] { 11013, 15750u, rewards, null };
            var built = (bool)builder.Invoke(null, args);
            var body = args[3] as byte[];
            Check("tower clear packet keeps the original fixed ten-slot wire layout",
                built
                && body != null
                && body.Length == 87
                && BitConverter.ToUInt32(body, 0) == 15750u
                && BitConverter.ToUInt16(body, 4) == 6
                && body[6] == 10
                && BitConverter.ToInt32(body, 7) == 2600001
                && BitConverter.ToInt32(body, 11) == 1
                && BitConverter.ToInt32(body, 39) == 2600005
                && BitConverter.ToInt32(body, 43) == 5
                && BitConverter.ToInt32(body, 47) == -1
                && BitConverter.ToInt32(body, 51) == 0
                && BitConverter.ToInt32(body, 79) == -1
                && BitConverter.ToInt32(body, 83) == 0,
                ref failures);
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
