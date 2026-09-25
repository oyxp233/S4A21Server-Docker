using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.Tournament;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    public static class A21SpecialDungeonProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_SPECIAL_DUNGEON_PROTOCOL selftest ===");
            var failures = 0;

            VerifyHuntOnlyBossEntrance(ref failures);
            VerifyTournamentPayloads(ref failures);
            VerifyBloodAltarPayloads(ref failures);
            VerifyBossDieCheckGate(ref failures);
            VerifyElevatorClock(ref failures);
            VerifyElevatorClearProjection(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_SPECIAL_DUNGEON_PROTOCOL selftest passed."
                    : $"A21_SPECIAL_DUNGEON_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyTournamentPayloads(ref int failures)
        {
            var candidates = new List<TournamentActorDefinition>();
            for (var index = 0; index < 15; index++)
            {
                candidates.Add(new TournamentActorDefinition(
                    partyCount: 1,
                    TournamentActorKind.Monster,
                    code: 56000 + index,
                    strength: 100 + index,
                    name: string.Empty,
                    level: 70,
                    actorType: 0));
            }

            var definition = new TournamentDungeonDefinition(
                dungeonId: 120,
                mapId: 17100,
                basicLevel: 70,
                partyLimit: 1,
                coinLimit: 3,
                roundFatigue: 0,
                clearRewardGoldRate: 1f,
                experienceByRound: null,
                resultCards: null,
                rewardItemRates: Array.Empty<TournamentRewardItemRateDefinition>(),
                candidates,
                startAreas: Array.Empty<TournamentStartAreaDefinition>(),
                entryItems: Array.Empty<TournamentEntryItemDefinition>());
            if (!TournamentDungeonRuntimeFactory.TryCreate(
                    definition,
                    partyCount: 1,
                    _ => 0,
                    out var runtime,
                    out var failureReason))
            {
                Check(
                    $"tournament runtime can be created: {failureReason}",
                    false,
                    ref failures);
                return;
            }

            var info = TournamentPacketBuilder.BuildTournamentInfo(
                runtime,
                difficulty: 2,
                firstMonsterSequence: 0x2711);
            Check(
                "TOURNAMENT_INFO uses the captured 260-byte body",
                info.Length == 260,
                ref failures);
            Check(
                "TOURNAMENT_INFO starts with u32 dungeon id, difficulty and party limit",
                ReadUInt32(info, 0) == 120
                && info[4] == 2
                && info[5] == 1,
                ref failures);
            Check(
                "TOURNAMENT_INFO path actor sequence remains aligned after the bracket",
                info[224] == 1
                && ReadUInt16(info, 225) == 0x2711,
                ref failures);

            const uint seed = 0xDD23D90E;
            var map = TournamentPacketBuilder.BuildTournamentMapInfo(
                x: 0,
                y: 0,
                seed,
                mapId: 17100,
                revisit: false);
            Check(
                "TOURNAMENT_MAP_INFO uses the captured 15-byte body",
                map.Length == 15,
                ref failures);
            Check(
                "TOURNAMENT_MAP_INFO writes u32 map id and explicit tail fields",
                ReadUInt32(map, 2) == seed
                && map[6] == 0
                && map[7] == 1
                && ReadUInt32(map, 8) == 17100
                && map[12] == 0
                && map[13] == 0
                && map[14] == 0,
                ref failures);
        }

        private static void VerifyBloodAltarPayloads(ref int failures)
        {
            var endlessInfo = BloodAltarPacketBuilder.BuildInfo(
                11006,
                BloodAltarDungeonKind.Endless);
            var ultimateInfo = BloodAltarPacketBuilder.BuildInfo(
                11007,
                BloodAltarDungeonKind.Ultimate);
            Check(
                "BLOOD_INFO keeps the captured 12-byte body for both altar kinds",
                endlessInfo.Length == 12 && ultimateInfo.Length == 12,
                ref failures);
            Check(
                "BLOOD_INFO maps the PVF altar kind to the captured mode word",
                ReadUInt32(endlessInfo, 0) == 11006
                && ReadUInt16(endlessInfo, 4) == 0
                && ReadUInt16(endlessInfo, 6) == 2
                && ReadUInt32(endlessInfo, 8) == 0
                && ReadUInt32(ultimateInfo, 0) == 11007
                && ReadUInt16(ultimateInfo, 4) == 0
                && ReadUInt16(ultimateInfo, 6) == 0
                && ReadUInt32(ultimateInfo, 8) == 0,
                ref failures);

            var firstMap = BloodAltarPacketBuilder.BuildStartMap(
                x: 0,
                y: 0,
                seed: 0x028080C0,
                mapId: 16348);
            var movedMap = BloodAltarPacketBuilder.BuildStartMap(
                x: 1,
                y: 0,
                seed: 0x0002084D,
                mapId: 16353);
            Check(
                "START_BLOOD_MAP remains 15 bytes on entry and map transitions",
                firstMap.Length == 15 && movedMap.Length == 15,
                ref failures);
            Check(
                "START_BLOOD_MAP writes the captured mode, u32 map id and zero tails",
                movedMap[0] == 1
                && movedMap[1] == 0
                && ReadUInt32(movedMap, 2) == 0x0002084D
                && movedMap[6] == 0
                && movedMap[7] == 1
                && ReadUInt32(movedMap, 8) == 16353
                && movedMap[12] == 0
                && movedMap[13] == 0
                && movedMap[14] == 0,
                ref failures);
        }

        private static ushort ReadUInt16(byte[] data, int offset)
            => BitConverter.ToUInt16(data, offset);

        private static uint ReadUInt32(byte[] data, int offset)
            => BitConverter.ToUInt32(data, offset);

        private static void VerifyHuntOnlyBossEntrance(ref int failures)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH")))
                return;

            var application = new SpecialDungeonMechanismApplicationService();
            using var tcpClient = new TcpClient();
            var session = new EnhancedClientSession(tcpClient, new GamePacketHeader());
            session.Player.CharacterId = 10041;
            foreach (short dungeonId in new short[] { 35, 37 })
            {
                var dungeon = Dungeon.GetDungeonFile(dungeonId);
                var expectedCount = dungeonId == 35 ? 4 : 1;
                for (var mazeIndex = 0; mazeIndex < dungeon.Mazes.Count; mazeIndex++)
                {
                    var maze = dungeon.Mazes[mazeIndex];
                    var run = new DungeonRun(dungeonId, 0)
                    {
                        MazeIndex = mazeIndex,
                        BossMapPos = maze.BossMap,
                    };
                    session.Player.CurrentRun = run;
                    SpecialDungeonRunCoordinator.ConfigureSelection(
                        run, maze, maze.BossMap, Array.Empty<DfoServer.Game.Quests.ActiveQuest>());
                    var label = $"dungeon={dungeonId} maze={mazeIndex}";
                    var targets = run.BossEntranceConditionTargets;
                    Check($"{label} assigns all hunt-only targets without a summoned boss",
                        targets.Count == expectedCount
                        && run.Mechanisms.HasBossEntranceCondition
                        && !run.HasBossEntranceConditionalSummon, ref failures);
                    if (targets.Count != expectedCount)
                        continue;

                    var member = new DungeonRun(run.Instance, run.RunId + 1, 1,
                        DungeonRunState.Active);
                    SpecialDungeonRunCoordinator.CloneSelectionState(run, member);
                    Check($"{label} party selection retains the same target rooms",
                        member.BossEntranceConditionTargets.Select(t => (t.MonsterCode, t.X, t.Y))
                            .SequenceEqual(targets.Select(t => (t.MonsterCode, t.X, t.Y))),
                        ref failures);

                    run.RoomKey = new RoomKey(255, 255, -1);
                    Check($"{label} a kill outside the assigned room keeps the gate closed",
                        application.ApplyMonsterKilled(run, targets[0].MonsterCode, 0).Count == 0
                        && !run.BossEntranceConditionComplete, ref failures);

                    for (var index = 0; index < targets.Count; index++)
                    {
                        var target = targets[index];
                        var room = Dungeon.GetDungeonMapMonsterSummaryInformation(
                            dungeonId, target.X, target.Y, mazeIndex);
                        SpecialDungeonRunCoordinator.AppendStartMapActors(session, run, room);
                        Check($"{label} target {target.MonsterCode} enters START_MAP as a blocking actor",
                            room.Monsters.Count(m => m.Code == target.MonsterCode
                                && m.IsBlocking && m.Flag0 == 0) == 1, ref failures);

                        run.RoomKey = new RoomKey(target.X, target.Y, -1);
                        var effects = application.ApplyMonsterKilled(run, target.MonsterCode, 0);
                        var finalTarget = index == targets.Count - 1;
                        Check($"{label} target {index + 1}/{targets.Count} opens the gate only at completion",
                            target.Completed
                            && run.BossEntranceConditionComplete == finalTarget
                            && effects.Count(e => e.Kind == SpecialDungeonEffectKind.PassGate)
                                == (finalTarget ? 1 : 0), ref failures);
                        Check($"{label} repeated target death preserves the completed result",
                            application.ApplyMonsterKilled(run, target.MonsterCode, 0).Count == 0,
                            ref failures);
                    }
                    Check($"{label} hunt-only completion retains ordinary boss handling",
                        !run.HasBossEntranceConditionalSummon && !run.ConditionalBossSpawned,
                        ref failures);
                }
            }
            var body = SpecialDungeonNotificationBuilder.BuildCompleteConditionPassGateTrigger();
            Check("A21 gate condition notification retains the consumed i32 and u8 body",
                body.Length == 5 && BitConverter.ToInt32(body, 0) == 0 && body[4] == 0,
                ref failures);
        }

        private static void VerifyBossDieCheckGate(ref int failures)
        {
            using (var tcpClient = new TcpClient())
            {
                var session = new EnhancedClientSession(
                    tcpClient,
                    new GamePacketHeader());
                session.Player.CharacterId = 10040;

                var run = new DungeonRun(
                    new DungeonInstance(2010, 0),
                    runId: 900,
                    runGeneration: 1,
                    DungeonRunState.Active)
                {
                    Phase = DungeonRunPhase.InProgress,
                    BossEntranceConditionTargets =
                        new List<BossEntranceConditionTargetState>
                        {
                            new BossEntranceConditionTargetState
                            {
                                MonsterCode = 50001,
                                Completed = true,
                            },
                        },
                    BossEntranceConditionalSummonCodes =
                        new List<int> { 69264 },
                    BossEntranceConditionComplete = true,
                };
                session.Player.CurrentRun = run;

                var request = new BossDieCheckRequest(
                    userId: 7,
                    bossSequence: SpecialDungeonNotifier.BossSummonRuntimeKey);

                var reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    request);
                Check(
                    "boss die check rejects before the conditional boss is spawned",
                    !reported.ShouldClearDungeon,
                    ref failures);

                Check(
                    "conditional boss spawn is registered once at instance scope",
                    run.Instance.Mechanisms.TryRegisterConditionalBossSpawn(69264)
                    && !run.Instance.Mechanisms.TryRegisterConditionalBossSpawn(69265),
                    ref failures);

                reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    request);
                Check(
                    "boss die check clears for a non-summoner from the shared spawn fact",
                    reported.ShouldClearDungeon && reported.BossCode == 69264,
                    ref failures);

                // A stale participant-local projection must not override the
                // instance-level BossCode selected by the accepted summon.
                run.ConditionalBossSpawned = true;
                run.ConditionalBossCode = 69265;
                reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    request);
                Check(
                    "participant-local boss code cannot override the shared spawn code",
                    reported.ShouldClearDungeon && reported.BossCode == 69264,
                    ref failures);

                run.BossEntranceConditionComplete = false;
                reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    request);
                Check(
                    "boss die check rejects while the entrance condition " +
                    "is incomplete",
                    !reported.ShouldClearDungeon,
                    ref failures);

                run.BossEntranceConditionComplete = true;
                reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    new BossDieCheckRequest(userId: 7, bossSequence: 0x1234));
                Check(
                    "boss die check rejects a non-summon boss sequence",
                    !reported.ShouldClearDungeon,
                    ref failures);

                run.Instance.Mechanisms.ResetConditionalBossSpawn();
                run.BossEntranceConditionalSummonCodes.Clear();
                reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    request);
                Check(
                    "boss die check rejects after the shared spawn fact is reset",
                    !reported.ShouldClearDungeon,
                    ref failures);
            }
        }

        private static void VerifyElevatorClock(ref int failures)
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            const long tick = 1000;
            var elevator = new ElevatorRoomRuntime();
            Check("elevator waits for loading completion",
                elevator.Capture(start) == null, ref failures);
            Check("loading release starts the shared 15 second clock",
                elevator.TryStart(start, tick, out var ticket, out var deadline)
                    && deadline == start.AddSeconds(15), ref failures);
            Check("another participant or reconnect preserves the original clock",
                !elevator.TryStart(start.AddSeconds(10), tick + 10000, out _, out _),
                ref failures);
            Check("running elevator keeps its exits closed",
                !elevator.AllowsExit(new RoomKey(2, 5, -1), 3, 5), ref failures);

            for (var stage = 1; stage <= 4; stage++)
            {
                var previous = ticket;
                var advanced = elevator.TryAdvance(ticket, start.AddSeconds(stage * 15),
                    out var state, out ticket, out deadline);
                var body = SpecialDungeonNotificationBuilder.BuildElevatorState(state);
                Check($"stage {stage} uses the original two-byte running notification",
                    advanced && body[0] == stage && body[1] == 0
                        && (stage == 4 ? !ticket.IsValid : deadline == start.AddSeconds((stage + 1) * 15)),
                    ref failures);
                Check($"stage {stage} retires its previous timer ticket",
                    !elevator.TryAdvance(previous, start.AddMinutes(2), out _, out _, out _),
                    ref failures);
            }
            Check("60 seconds finishes warnings and waits for the actual kill",
                elevator.Capture(start.AddMinutes(2))?.Stop == ElevatorStopKind.Running,
                ref failures);
            elevator.Complete(tick + 60000);
            var crash = elevator.Capture(start.AddMinutes(2)).Value;
            Check("a kill at 60 seconds selects crash and the left route",
                crash.Stage == 4 && crash.Stop == ElevatorStopKind.Crash
                    && elevator.AllowsExit(new RoomKey(2, 5, -1), 1, 5)
                    && !elevator.AllowsExit(new RoomKey(2, 5, -1), 3, 5), ref failures);
            elevator.Complete(tick + 1000);
            Check("duplicate clear preserves the frozen crash result",
                elevator.Capture(start)?.Stop == ElevatorStopKind.Crash, ref failures);

            var timely = new ElevatorRoomRuntime();
            timely.TryStart(start, tick, out var timelyTicket, out _);
            // Simulate delayed delivery of a kill that actually occurred before 60s.
            timely.TryAdvance(timelyTicket, start.AddSeconds(61), out _, out _, out _);
            timely.Complete(tick + 59999);
            var normal = timely.Capture(start.AddMinutes(2)).Value;
            Check("canonical kill time decides the boundary even after delayed processing",
                normal.Stage == 3 && normal.Stop == ElevatorStopKind.Normal
                    && timely.AllowsExit(new RoomKey(2, 5, -1), 3, 5)
                    && !timely.AllowsExit(new RoomKey(2, 5, -1), 1, 5), ref failures);

            var early = new ElevatorRoomRuntime();
            early.TryStart(start, tick, out var earlyTicket, out _);
            early.Complete(tick + 14000);
            Check("early clear cancels pending warnings",
                !early.TryAdvance(earlyTicket, start.AddSeconds(15), out _, out _, out _)
                    && early.Capture(start)?.Stage == 0, ref failures);

            var resumed = new ElevatorRoomRuntime();
            resumed.TryStart(start, tick, out var resumeTicket, out _);
            Check("re-entry derives the current warning stage from the original start",
                resumed.Capture(start.AddSeconds(38))?.Stage == 2, ref failures);
            Check("a late callback advances directly to the current stage",
                resumed.TryAdvance(resumeTicket, start.AddSeconds(47),
                    out var resumedState, out var nextTicket, out var nextDeadline)
                    && resumedState.Stage == 3 && nextDeadline == start.AddSeconds(60),
                ref failures);
            resumed.Close();
            Check("closing a room invalidates pending callbacks and restart attempts",
                !resumed.TryAdvance(nextTicket, start.AddSeconds(60), out _, out _, out _)
                    && !resumed.TryStart(start, tick, out _, out _)
                    && resumed.Capture(start) == null, ref failures);
        }

        private static void VerifyElevatorClearProjection(ref int failures)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH")))
                return;

            var maze = Dungeon.GetDungeonMapMonsterSummaryInformation(53, 2, 5, 0);
            var run = new DungeonRun(53, 0) { RoomKey = new RoomKey(2, 5, -1) };
            var shared = run.Instance.GetOrCreateRoom(run.RoomKey,
                id => new DungeonInstanceRoom(id, run.RoomKey, maze, 1), out _);
            run.SetCurrentRoom(shared);
            var room = new RoomState { Maze = maze, InstanceRoom = shared };
            run.RoomStates.Add(run.RoomKey, room);
            room.TryActivate();
            var application = new SpecialDungeonMechanismApplicationService();
            Check("active elevator waits for the room clear fact",
                application.BuildRoomClearState(room).Count == 0, ref failures);
            var start = DateTime.UtcNow;
            var tick = Environment.TickCount64;
            shared.Elevator.TryStart(start, tick, out _, out _);
            var source = new DungeonEventEnvelope(Guid.NewGuid(), run.CaptureIdentity(),
                shared.RoomInstanceId, 1, 1, 1, maze.Monsters[0].Code,
                "elevator clear test", tick + 61000);
            ushort lastSequence = 0;
            foreach (var actor in maze.Monsters)
            {
                lastSequence++;
                shared.TryRecordActorDeath(source, lastSequence, actor.Code, actor.Type);
            }
            var committed = shared.TryCommitClearFromActorDeaths(
                actor => actor.IsBlocking, source, lastSequence);
            room.TryClear();
            var effects = application.BuildRoomClearState(room);
            Check("canonical room clear freezes the PVF elevator outcome",
                committed.IsCleared && effects.Count == 1
                    && effects[0].Kind == SpecialDungeonEffectKind.ElevatorState
                    && effects[0].MapId == 16408
                    && effects[0].Elevator.Stop == ElevatorStopKind.Crash,
                ref failures);

            using var tcpClient = new TcpClient();
            var session = new EnhancedClientSession(tcpClient, new GamePacketHeader());
            byte[] packet = null;
            new SpecialDungeonNotificationSender().SendAsync(session, effects[0], data =>
            {
                packet = data;
                return System.Threading.Tasks.Task.FromResult(true);
            }).GetAwaiter().GetResult();
            Check("elevator notification matches the A21 two-byte terminal-state consumer",
                packet.Length == 17 && packet[0] == 0
                    && ReadUInt16(packet, 1) == (ushort)NotiPacketTypeA21.ELEVATOR_CLEAR_TIME_CHECK
                    && packet[15] == 4 && packet[16] == 2,
                ref failures);

            Check("revisiting a cleared elevator preserves the original crash route",
                application.BuildStartMapState(run)[0].Elevator.Stop == ElevatorStopKind.Crash,
                ref failures);
            var sameRoom = run.Instance.GetOrCreateRoom(run.RoomKey,
                _ => throw new InvalidOperationException("room must be shared"), out var created);
            Check("party participants share one elevator runtime",
                !created && ReferenceEquals(sameRoom.Elevator, shared.Elevator), ref failures);
            run.Instance.TryBeginEnding();
            Check("instance end closes the elevator state",
                shared.Elevator.Capture(start) == null, ref failures);

            var ordinary = new RoomState
            {
                Maze = new Dungeon.MazeSumInfo { PassiveObjectCodes = new[] { 1111, 826 } },
            };
            ordinary.TryActivate();
            ordinary.TryClear();
            Check("cleared rooms with other passive objects keep their ordinary projection",
                application.BuildRoomClearState(ordinary).Count == 0, ref failures);
            var ordinaryMaze = maze;
            ordinaryMaze.HasElevatorControl = false;
            Check("ordinary room templates create no elevator runtime",
                new DungeonInstanceRoom(1, run.RoomKey, ordinaryMaze, 1).Elevator == null,
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
