using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Accounts;
using DfoServer.Game.DailyReset;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.BloodAltar;
using DfoServer.GameWorld;
using DfoServer.Game.Inventory;
using DfoServer.Game.Party;
using DfoServer.Game.Quests;
using DfoServer.Game.ReviveCoin;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class DungeonCombatPartySelfTest
    {
        private const int KillerCharacterId = 48001;
        private const int MemberCharacterId = 48002;
        private const ushort MonsterSequence = 10;
        private const ushort OrdinaryKillQuestId = 20722;
        private const int OrdinaryKillDungeonId = 3536;
        private const int OrdinaryKillMonsterCode = 100003;
        private const ushort AnyMonsterQuestId = 4303;
        private const int AnyMonsterDungeonId = 144;
        private const int AnyMonsterCode = 65301;
        private const ushort EliteMonsterQuestId = 4;
        private const ushort BossMonsterQuestId = 6;
        private const ushort RescueSilmaQuestId = 1791;
        private const int RescueSilmaDungeonId = 149;
        private const int RescueSilmaApcCode = 6510;
        private const ushort NightAssaultQuestId = 2188;
        private const int NightAssaultDungeonId = 83;
        private const int NightAssaultMonsterCode = 61438;
        private const ushort ConditionalBossQuestId = 13504;
        private const int ConditionalBossDungeonId = 2010;
        private const int ConditionalBossMonsterCode = 69264;
        private const ushort BloodAltarQuestId = 5651;
        private const int BloodAltarDungeonId = 11006;
        private const int BloodAltarMapId = 16351;
        private const int BloodAltarMonsterCode = 56004;
        private const int BloodAltarQuestItemId = 4363;
        private const ushort MasaccioCaptureQuestId = 2257;
        private const int MasaccioCaptureDungeonId = 87;
        private const int MasaccioCaptureMonsterCode = 56010;
        private const int MasaccioCaptureItemId = 3315;
        private const int MasaccioScannerItemId = 6056;

        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_COMBAT_PARTY selftest ===");
            var failures = 0;
            using (var fixture = Fixture.Create())
            {
                var tower = fixture.PrepareTowerKill();
                var killerLevel = fixture.Killer.Session.Player.Level;
                var killerExp = fixture.Killer.Session.Player.Exp;
                var memberRun = fixture.Member.Session.Player.CurrentRun;
                var memberLevel = fixture.Member.Session.Player.Level;
                var memberExp = fixture.Member.Session.Player.Exp;
                var memberPhase = memberRun.Phase;

                fixture.KillMonster();

                var killerPackets = fixture.Killer.ReadAvailableTypes();
                var memberPackets = fixture.Member.ReadAvailableTypes();
                Check("tower killer keeps normal EXP/level progression and receives 0x0025/0x0026",
                    fixture.Killer.Session.Player.Exp > killerExp
                    && fixture.Killer.Session.Player.Level >= killerLevel
                    && killerPackets.Contains(0x0025)
                    && killerPackets.Contains(0x0026),
                    ref failures);
                Check("tower APC kill keeps its guaranteed tower drop",
                    tower.GroundItems.Count == 1,
                    ref failures);
                Check("ordinary party member receives no tower kill EXP or combat notifications",
                    fixture.Member.Session.Player.Level == memberLevel
                    && fixture.Member.Session.Player.Exp == memberExp
                    && !memberPackets.Contains(0x0025)
                    && !memberPackets.Contains(0x0026),
                    ref failures);
                Check("tower kill does not mutate or clear the ordinary member run",
                    memberRun.RoomKilledSeqIds.Count == 0
                    && memberRun.Phase == memberPhase,
                    ref failures);

                fixture.PrepareOrdinaryPartyKill();
                memberRun = fixture.Member.Session.Player.CurrentRun;
                memberExp = fixture.Member.Session.Player.Exp;

                fixture.KillMonster();

                memberPackets = fixture.Member.ReadAvailableTypes();
                Check("ordinary party kill still grants member EXP and relays 0x0025/0x0026",
                    fixture.Member.Session.Player.Exp > memberExp
                    && memberPackets.Contains(0x0025)
                    && memberPackets.Contains(0x0026),
                    ref failures);
                Check("ordinary party kill still propagates the room kill sequence",
                    memberRun.RoomKilledSeqIds.SetEquals(new[] { MonsterSequence }),
                    ref failures);

                var ordinaryKillerRun = fixture.Killer.Session.Player.CurrentRun;
                var killerAfterFirstKill = fixture.Killer.Session.Player.Exp;
                var memberAfterFirstKill = fixture.Member.Session.Player.Exp;
                var killerTotalExp = ordinaryKillerRun.TotalExp;
                var memberTotalExp = memberRun.TotalExp;
                var killerTotalGold = ordinaryKillerRun.TotalGold;
                var memberTotalGold = memberRun.TotalGold;
                var killerDropCount = ordinaryKillerRun.Drops.Count;
                var memberDropCount = memberRun.Drops.Count;
                fixture.KillMonster();
                Check("duplicate party kill does not repeat participant rewards",
                    fixture.Killer.Session.Player.Exp == killerAfterFirstKill
                    && fixture.Member.Session.Player.Exp == memberAfterFirstKill
                    && ordinaryKillerRun.TotalExp == killerTotalExp
                    && memberRun.TotalExp == memberTotalExp
                    && ordinaryKillerRun.TotalGold == killerTotalGold
                    && memberRun.TotalGold == memberTotalGold
                    && ordinaryKillerRun.Drops.Count == killerDropCount
                    && memberRun.Drops.Count == memberDropCount,
                    ref failures);

                fixture.PrepareFriendlyApcPartyKill();
                var friendlyKillerRun = fixture.Killer.Session.Player.CurrentRun;
                var friendlyMemberRun = fixture.Member.Session.Player.CurrentRun;
                killerExp = fixture.Killer.Session.Player.Exp;
                memberExp = fixture.Member.Session.Player.Exp;
                fixture.KillMonster();
                killerPackets = fixture.Killer.ReadAvailableTypes();
                memberPackets = fixture.Member.ReadAvailableTypes();
                Check("friendly APC death is projected without participant combat rewards",
                    fixture.Killer.Session.Player.Exp == killerExp
                    && fixture.Member.Session.Player.Exp == memberExp
                    && friendlyKillerRun.TotalExp == 0
                    && friendlyMemberRun.TotalExp == 0
                    && friendlyKillerRun.TotalGold == 0
                    && friendlyMemberRun.TotalGold == 0
                    && friendlyKillerRun.Drops.Count == 0
                    && friendlyMemberRun.Drops.Count == 0
                    && !killerPackets.Contains(0x0025)
                    && !memberPackets.Contains(0x0025)
                    && killerPackets.Contains(0x0026)
                    && memberPackets.Contains(0x0026),
                    ref failures);
                Check("actor reward eligibility preserves hostile and legacy APC paths",
                    !DungeonActorRewardEligibility.AllowsParticipantCombatRewards(
                        new GameWorld.Dungeon.MonsterSumInfo
                        {
                            Type = (byte)PvfLib.ApcAIType.Normal,
                            Faction = PvfLib.ApcFaction.Neutral,
                        })
                    && DungeonActorRewardEligibility.AllowsParticipantCombatRewards(
                        new GameWorld.Dungeon.MonsterSumInfo
                        {
                            Type = (byte)PvfLib.ApcAIType.Normal,
                            Faction = PvfLib.ApcFaction.Monster,
                        })
                    && DungeonActorRewardEligibility.AllowsParticipantCombatRewards(
                        new GameWorld.Dungeon.MonsterSumInfo
                        {
                            Type = (byte)PvfLib.ApcAIType.Normal,
                        }),
                    ref failures);

                fixture.PrepareTrainingPartyKill();
                var killerRun = fixture.Killer.Session.Player.CurrentRun;
                memberRun = fixture.Member.Session.Player.CurrentRun;
                killerExp = fixture.Killer.Session.Player.Exp;
                memberExp = fixture.Member.Session.Player.Exp;

                fixture.KillMonster();

                killerPackets = fixture.Killer.ReadAvailableTypes();
                memberPackets = fixture.Member.ReadAvailableTypes();
                Check("interactive training kill keeps death projection but grants no party EXP",
                    fixture.Killer.Session.Player.Exp == killerExp
                    && fixture.Member.Session.Player.Exp == memberExp
                    && !killerPackets.Contains(0x0025)
                    && !memberPackets.Contains(0x0025)
                    && killerPackets.Contains(0x0026)
                    && memberPackets.Contains(0x0026),
                    ref failures);
                Check("interactive training kill creates no drops, gold, or clear settlement",
                    killerRun.TotalExp == 0
                    && killerRun.TotalGold == 0
                    && killerRun.Drops.Count == 0
                    && memberRun.TotalExp == 0
                    && memberRun.TotalGold == 0
                    && memberRun.Drops.Count == 0
                    && killerRun.RunState == DungeonRunState.Active
                    && memberRun.RunState == DungeonRunState.Active,
                    ref failures);
                Check("interactive training kill still records the shared room death fact",
                    killerRun.RoomKilledSeqIds.SetEquals(new[] { MonsterSequence })
                    && memberRun.RoomKilledSeqIds.SetEquals(new[] { MonsterSequence }),
                    ref failures);

                fixture.PrepareTimeCrackPartyKill();
                fixture.KillMonster();
                var killerMechanismPackets = fixture.Killer.ReadAvailableTypes();
                var memberMechanismPackets = fixture.Member.ReadAvailableTypes();
                Check("party MonsterKilled enters the same special mechanism path",
                    killerMechanismPackets.Contains(0x022D)
                    && memberMechanismPackets.Contains(0x022D)
                    && fixture.Killer.Session.Player.CurrentRun.SpecialDungeon.TimeCrackGauge == 30
                    && fixture.Member.Session.Player.CurrentRun.SpecialDungeon.TimeCrackGauge == 30,
                    ref failures);
                fixture.KillMonster();
                Check("duplicate party kill does not repeat per-participant mechanism effects",
                    fixture.Killer.Session.Player.CurrentRun.SpecialDungeon.TimeCrackGauge == 30
                    && fixture.Member.Session.Player.CurrentRun.SpecialDungeon.TimeCrackGauge == 30,
                    ref failures);

                fixture.PrepareDifferentInstancePartyKill();
                memberRun = fixture.Member.Session.Player.CurrentRun;
                memberExp = fixture.Member.Session.Player.Exp;
                fixture.KillMonster();
                memberPackets = fixture.Member.ReadAvailableTypes();
                Check("same-party players in different dungeon instances receive no kill relay",
                    fixture.Member.Session.Player.Exp == memberExp
                    && memberRun.RoomKilledSeqIds.Count == 0
                    && !memberPackets.Contains(0x0025)
                    && !memberPackets.Contains(0x0026),
                    ref failures);

                fixture.PrepareOrdinaryQuestKill();
                fixture.KillMonster();
                Check("ordinary canonical monster kill advances the frozen active quest",
                    fixture.LoadKillerQuestTrigger(OrdinaryKillQuestId) == 2,
                    ref failures);
                fixture.KillMonster();
                Check("duplicate ordinary monster report does not repeat quest progress",
                    fixture.LoadKillerQuestTrigger(OrdinaryKillQuestId) == 2,
                    ref failures);

                fixture.PrepareCaptureQuestKill();
                var captureRun = fixture.Killer.Session.Player.CurrentRun;
                fixture.CaptureMonster();
                var captureSlot = FindGroundItemSlot(
                    captureRun,
                    MasaccioCaptureItemId);
                Check(
                    "capture-authorized monster creates one configured ground item",
                    captureSlot > 0
                        && CountGroundItem(captureRun, MasaccioCaptureItemId) == 1
                        && fixture.CountKillerItem(MasaccioCaptureItemId) == 0
                        && fixture.LoadKillerQuestTrigger(
                            MasaccioCaptureQuestId) == 1,
                    ref failures);
                Check(
                    "capture item is isolated from the party member",
                    CountGroundItem(
                        fixture.Member.Session.Player.CurrentRun,
                        MasaccioCaptureItemId) == 0
                        && fixture.CountMemberItem(MasaccioCaptureItemId) == 0,
                    ref failures);
                fixture.CaptureMonster();
                Check(
                    "duplicate capture death does not register another ground item",
                    CountGroundItem(captureRun, MasaccioCaptureItemId) == 1,
                    ref failures);
                fixture.PickupGroundItem((ushort)captureSlot);
                Check(
                    "captured item pickup updates inventory and seeking progress",
                    CountGroundItem(captureRun, MasaccioCaptureItemId) == 0
                        && fixture.CountKillerItem(MasaccioCaptureItemId) == 1
                        && fixture.LoadKillerQuestTrigger(
                            MasaccioCaptureQuestId) == 0,
                    ref failures);

                fixture.PrepareCaptureQuestKill(activeQuest: false);
                var noQuestCaptureRun = fixture.Killer.Session.Player.CurrentRun;
                fixture.CaptureMonster();
                Check(
                    "capture without an active seeking quest is a no-op",
                    CountGroundItem(
                        noQuestCaptureRun,
                        MasaccioCaptureItemId) == 0,
                    ref failures);

                fixture.PrepareCaptureQuestKill(replaceActivation: true);
                var staleActivationCaptureRun =
                    fixture.Killer.Session.Player.CurrentRun;
                fixture.CaptureMonster();
                Check(
                    "capture with a stale quest activation is a no-op",
                    CountGroundItem(
                        staleActivationCaptureRun,
                        MasaccioCaptureItemId) == 0,
                    ref failures);

                fixture.PrepareNightAssaultQuestKill();
                fixture.KillMonster();
                Check("Night Assault quest 2188 advances from its canonical PVF actor",
                    fixture.LoadKillerQuestTrigger(NightAssaultQuestId) == 14,
                    ref failures);
                fixture.KillMonster();
                Check("duplicate Night Assault actor death does not repeat quest progress",
                    fixture.LoadKillerQuestTrigger(NightAssaultQuestId) == 14,
                    ref failures);

                fixture.PrepareAnyMonsterQuestKill(monsterType: 0);
                fixture.KillMonster();
                Check("any-monster quest advances through the canonical kill bridge",
                    fixture.LoadKillerQuestTrigger(AnyMonsterQuestId) == 29,
                    ref failures);
                fixture.KillMonster();
                Check("any-monster quest does not repeat one canonical death fact",
                    fixture.LoadKillerQuestTrigger(AnyMonsterQuestId) == 29,
                    ref failures);

                fixture.PrepareAnyMonsterQuestKill(monsterType: 5);
                fixture.KillMonster();
                Check("any-monster quest excludes APC actor deaths",
                    fixture.LoadKillerQuestTrigger(AnyMonsterQuestId) == 30,
                    ref failures);

                fixture.PrepareAnyMonsterQuestKill(monsterType: 1);
                fixture.KillMonster();
                Check("any-monster quest excludes elite actor deaths",
                    fixture.LoadKillerQuestTrigger(AnyMonsterQuestId) == 30,
                    ref failures);

                fixture.PrepareEliteQuestKill(monsterType: 0);
                fixture.KillMonster();
                Check("elite-monster quest excludes ordinary actor deaths",
                    fixture.LoadKillerQuestTrigger(EliteMonsterQuestId) == 5,
                    ref failures);

                fixture.PrepareEliteQuestKill(monsterType: 1);
                fixture.KillMonster();
                Check("elite-monster quest advances through the canonical kill bridge",
                    fixture.LoadKillerQuestTrigger(EliteMonsterQuestId) == 4,
                    ref failures);
                fixture.KillMonster();
                Check("duplicate elite death does not repeat quest progress",
                    fixture.LoadKillerQuestTrigger(EliteMonsterQuestId) == 4,
                    ref failures);

                fixture.PrepareBossQuestKill(monsterType: 0);
                fixture.KillMonster();
                Check("boss-monster quest excludes ordinary actor deaths",
                    fixture.LoadKillerQuestTrigger(BossMonsterQuestId) == 5,
                    ref failures);

                fixture.PrepareBossQuestKill(monsterType: 1);
                fixture.KillMonster();
                Check("boss-monster quest excludes elite actor deaths",
                    fixture.LoadKillerQuestTrigger(BossMonsterQuestId) == 5,
                    ref failures);

                fixture.PrepareBossQuestKill(monsterType: 3);
                fixture.KillMonster();
                Check("boss-monster quest advances through the canonical kill bridge",
                    fixture.LoadKillerQuestTrigger(BossMonsterQuestId) == 4,
                    ref failures);
                fixture.KillMonster();
                Check("duplicate boss death does not repeat quest progress",
                    fixture.LoadKillerQuestTrigger(BossMonsterQuestId) == 4,
                    ref failures);

                var rescueSilmaBossSequence =
                    fixture.PrepareRescueSilmaApcBossQuest();
                fixture.ConfirmBossDeath((ushort)(rescueSilmaBossSequence + 1));
                Check("BOSS_DIE_CHECK rejects a sequence outside the current room actor",
                    fixture.LoadKillerQuestTrigger(RescueSilmaQuestId) == 1,
                    ref failures);
                for (var index = 0; index < 6; index++)
                    fixture.KillMonster((ushort)(MonsterSequence + index));
                Check("endpoint clear waits for a frozen hostile APC boss",
                    fixture.Killer.Session.Player.CurrentRun.Phase
                        < DungeonRunPhase.Cleared
                    && fixture.LoadKillerQuestTrigger(RescueSilmaQuestId) == 1,
                    ref failures);
                fixture.ConfirmBossDeath(rescueSilmaBossSequence);
                Check("BOSS_DIE_CHECK routes a normal APC boss death through the quest bridge",
                    fixture.LoadKillerQuestTrigger(RescueSilmaQuestId) == 0,
                    ref failures);
                Check("hostile APC boss death releases endpoint settlement",
                    fixture.Killer.Session.Player.CurrentRun.Phase
                        >= DungeonRunPhase.Cleared,
                    ref failures);
                fixture.ConfirmBossDeath(rescueSilmaBossSequence);
                Check("duplicate normal APC BOSS_DIE_CHECK is a canonical death no-op",
                    fixture.CountKillerProgressEvents("hunt-enemy") == 1,
                    ref failures);

                var bloodAltarSequence = fixture.PrepareBloodAltarQuestDrop();
                fixture.KillMonster(bloodAltarSequence);
                Check("blood altar dynamic actor grants its configured quest material",
                    fixture.LoadKillerQuestTrigger(BloodAltarQuestId) == 0
                    && fixture.CountKillerItem(BloodAltarQuestItemId) == 1,
                    ref failures);
                var bloodAltarRun = fixture.Killer.Session.Player.CurrentRun;
                Check("blood altar quest drop does not enable ordinary monster rewards",
                    bloodAltarRun.TotalExp == 0
                    && bloodAltarRun.TotalGold == 0
                    && bloodAltarRun.Drops.Count == 0,
                    ref failures);

                fixture.PrepareConditionalBossQuest();
                fixture.ConfirmConditionalBossDeath();
                Check("BOSS_DIE_CHECK confirms the conditional boss kill before clear",
                    fixture.LoadKillerQuestTrigger(ConditionalBossQuestId) == 0,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static int CountGroundItem(DungeonRun run, int itemId)
        {
            if (run == null || itemId <= 0)
                return 0;

            var count = 0;
            lock (run.SyncRoot)
            {
                foreach (var drop in run.Drops.Values)
                {
                    if (drop.IsGold
                        || drop.TemplateId != unchecked((uint)itemId))
                    {
                        continue;
                    }

                    var stack = drop.StackCount > int.MaxValue
                        ? int.MaxValue
                        : (int)drop.StackCount;
                    count = count > int.MaxValue - Math.Max(1, stack)
                        ? int.MaxValue
                        : count + Math.Max(1, stack);
                }
            }
            return count;
        }

        private static ushort FindGroundItemSlot(DungeonRun run, int itemId)
        {
            if (run == null || itemId <= 0)
                return 0;

            lock (run.SyncRoot)
            {
                foreach (var pair in run.Drops)
                {
                    var drop = pair.Value;
                    if (!drop.IsGold
                        && drop.TemplateId == unchecked((uint)itemId))
                    {
                        return pair.Key;
                    }
                }
            }
            return 0;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _dbPath;
            private readonly string _connectionString;
            private readonly string _previousDatabasePath;
            private readonly ConnectedSession _killer;
            private readonly ConnectedSession _member;
            private readonly DungeonHandler _handler;

            private Fixture(
                string dbPath,
                string connectionString,
                string previousDatabasePath,
                ConnectedSession killer,
                ConnectedSession member,
                DungeonHandler handler)
            {
                _dbPath = dbPath;
                _connectionString = connectionString;
                _previousDatabasePath = previousDatabasePath;
                _killer = killer;
                _member = member;
                _handler = handler;
            }

            public ConnectedSession Killer => _killer;
            public ConnectedSession Member => _member;

            public static Fixture Create()
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
                Directory.CreateDirectory(tempDir);
                var dbPath = Path.Combine(tempDir, $"dungeon-combat-party-{Guid.NewGuid():N}.db");
                var previousDatabasePath = Environment.GetEnvironmentVariable("INVENTORY_DATABASE_PATH");
                Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", dbPath);
                SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
                SeedAccountAndCharacter(dbPath, KillerCharacterId, "combat-killer");
                SeedAccountAndCharacter(dbPath, MemberCharacterId, "combat-member");

                var connectionString = SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
                var dailyReset = new DailyResetService(dbPath, ServerPaths.SchemaFilePath);
                var characters = new Game.Characters.SqliteCharacterRepository(dbPath, ServerPaths.SchemaFilePath);
                var selectData = new Game.SelectCharacter.SqliteSelectCharacterDataSource(
                    dbPath,
                    ServerPaths.SchemaFilePath,
                    characters,
                    null,
                    SystemRentalTimeProvider.Instance);
                var refresh = new Network.Handlers.InventoryRefreshSender(selectData, characters);
                var questDrops = new Game.Quests.QuestDropService(
                    refresh,
                    connectionString);
                var parties = new PartyManager();
                var sessions = new SessionDirectory();
                var killer = ConnectedSession.Create(KillerCharacterId, "combat-killer");
                var member = ConnectedSession.Create(MemberCharacterId, "combat-member");
                killer.Session.GameSession = new GameSession(
                    killer.Session,
                    connectionString);
                member.Session.GameSession = new GameSession(
                    member.Session,
                    connectionString);
                InventoryContext.Register(
                    killer.Session.SessionId,
                    new InventoryService(KillerCharacterId, KillerCharacterId));
                InventoryContext.Register(
                    member.Session.SessionId,
                    new InventoryService(MemberCharacterId, MemberCharacterId));
                sessions.Register(KillerCharacterId, killer.Session);
                sessions.Register(MemberCharacterId, member.Session);
                var created = parties.CreateParty(ToPartyMember(killer));
                if (!created.Ok || !parties.Join(created.Party.PartyId, ToPartyMember(member)).Ok)
                    throw new InvalidOperationException("Unable to create combat self-test party.");

                var handler = new DungeonHandler(
                    new ReviveCoinService(dailyReset),
                    characters,
                    selectData,
                    SystemRentalTimeProvider.Instance,
                    connectionString,
                    refresh,
                    parties,
                    sessions,
                    questDrops,
                    new AccountExperienceProgressService(
                        characters,
                        dbPath,
                        ServerPaths.SchemaFilePath));
                return new Fixture(
                    dbPath,
                    connectionString,
                    previousDatabasePath,
                    killer,
                    member,
                    handler);
            }

            public DeathTowerSession PrepareTowerKill()
            {
                _killer.ReadAvailableTypes();
                _member.ReadAvailableTypes();
                _killer.Session.Player.Level = 50;
                _killer.Session.Player.Exp = 0;
                _member.Session.Player.Level = 50;
                _member.Session.Player.Exp = 0;
                var config = DeathTowerSelfTestFactory.CreateConfig(
                    11000,
                    new[] { 33060 },
                    50,
                    maxClearItemCount: 10);
                var tower = new DeathTowerSession(config);
                tower.BeginStage(0x12345678, new[]
                {
                    new StageTowerItem
                    {
                        SourceListIndex = 0,
                        SourceMonsterUniqueId = MonsterSequence,
                        ItemUniqueId = 51,
                        ItemId = 6515,
                        DropRate = 10000,
                        StackCount = 1,
                    },
                });
                _killer.Session.Player.CurrentRun = CreateRun(tower, monsterType: 5, monsterCode: 10504);
                _member.Session.Player.CurrentRun = CreateRun(null, monsterType: 5, monsterCode: 10504);
                return tower;
            }

            public void PrepareOrdinaryPartyKill()
            {
                _killer.ReadAvailableTypes();
                _member.ReadAvailableTypes();
                _killer.Session.Player.Level = 50;
                _killer.Session.Player.Exp = 0;
                _member.Session.Player.Level = 50;
                _member.Session.Player.Exp = 0;
                var runs = CreateSharedRuns(monsterType: 0, monsterCode: 1001);
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;
            }

            public void PrepareFriendlyApcPartyKill()
            {
                _killer.ReadAvailableTypes();
                _member.ReadAvailableTypes();
                _killer.Session.Player.Level = 50;
                _killer.Session.Player.Exp = 0;
                _member.Session.Player.Level = 50;
                _member.Session.Player.Exp = 0;
                var actors = new List<GameWorld.Dungeon.MonsterSumInfo>
                {
                    new GameWorld.Dungeon.MonsterSumInfo
                    {
                        Code = 65001,
                        Level = 50,
                        Type = (byte)PvfLib.ApcAIType.Normal,
                        Faction = PvfLib.ApcFaction.Character,
                        IsBlocking = false,
                        PacketIndex = MonsterSequence,
                    },
                    new GameWorld.Dungeon.MonsterSumInfo
                    {
                        Code = 1001,
                        Level = 50,
                        Type = 0,
                        IsBlocking = true,
                        PacketIndex = MonsterSequence + 1,
                    },
                };
                var runs = CreateSharedRuns(
                    monsterType: 0,
                    monsterCode: 0,
                    roomMonsters: actors);
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;
            }

            public void PrepareTimeCrackPartyKill()
            {
                var definition = new SpecialDungeonDefinitionBuilder
                {
                    TimeCrackSandGaugeMax = 100,
                    TimeCrackSandGaugeGainOnKill = 10,
                    TimeCrackSandGaugeGainOnChampion = 30,
                };

                var runs = CreateSharedRuns(monsterType: 1, monsterCode: 1001);
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;
                _killer.Session.Player.CurrentRun.SpecialDungeon = new SpecialDungeonRuntime(
                    definition.Build(
                        _killer.Session.Player.CurrentRun.DungeonId,
                        SpecialDungeonKind.TimeCrack));
                _member.Session.Player.CurrentRun.SpecialDungeon = new SpecialDungeonRuntime(
                    definition.Build(
                        _member.Session.Player.CurrentRun.DungeonId,
                        SpecialDungeonKind.TimeCrack));
            }

            public void PrepareTrainingPartyKill()
            {
                _killer.ReadAvailableTypes();
                _member.ReadAvailableTypes();
                _killer.Session.Player.Level = 50;
                _killer.Session.Player.Exp = 0;
                _member.Session.Player.Level = 50;
                _member.Session.Player.Exp = 0;
                var runs = CreateSharedRuns(
                    monsterType: 0,
                    monsterCode: 1001,
                    rewardPolicy: DungeonRewardPolicy.InteractiveTraining);
                runs.Killer.BossMapPos = new[] { 1, 1 };
                runs.Member.BossMapPos = new[] { 1, 1 };
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;
            }

            public void PrepareDifferentInstancePartyKill()
            {
                _killer.ReadAvailableTypes();
                _member.ReadAvailableTypes();
                _killer.Session.Player.CurrentRun = CreateRun(
                    null,
                    monsterType: 0,
                    monsterCode: 1001);
                _member.Session.Player.CurrentRun = CreateRun(
                    null,
                    monsterType: 0,
                    monsterCode: 1001);
            }

            public void PrepareOrdinaryQuestKill()
            {
                var active = SaveKillerActiveQuest(
                    OrdinaryKillQuestId,
                    triggerValue: 3);
                var runs = CreateSharedRuns(
                    monsterType: 0,
                    monsterCode: OrdinaryKillMonsterCode,
                    dungeonId: OrdinaryKillDungeonId,
                    difficulty: 2);
                runs.Killer.QuestSnapshot = QuestRunSnapshot.Capture(active);
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;
            }

            public void PrepareCaptureQuestKill(
                bool activeQuest = true,
                bool replaceActivation = false)
            {
                _killer.ReadAvailableTypes();
                _member.ReadAvailableTypes();
                List<ActiveQuest> killerActive;
                List<ActiveQuest> memberActive;
                if (activeQuest)
                {
                    killerActive = SaveActiveQuest(
                        KillerCharacterId,
                        MasaccioCaptureQuestId,
                        triggerValue: 1);
                    memberActive = SaveActiveQuest(
                        MemberCharacterId,
                        MasaccioCaptureQuestId,
                        triggerValue: 1);

                    if (!InventoryRewardGrantService.TryCreateAndInsert(
                            InventoryContext.Get(KillerCharacterId),
                            MasaccioScannerItemId,
                            ItemCreateReason.QuestReward,
                            1,
                            out var killerTool)
                        || !killerTool.Success
                        || !InventoryRewardGrantService.TryCreateAndInsert(
                            InventoryContext.Get(MemberCharacterId),
                            MasaccioScannerItemId,
                            ItemCreateReason.QuestReward,
                            1,
                            out var memberTool)
                        || !memberTool.Success)
                    {
                        throw new InvalidOperationException(
                            "Unable to seed SC-13 capture tools.");
                    }
                }
                else
                {
                    QuestService.SaveActiveQuests(
                        _connectionString,
                        KillerCharacterId,
                        new List<ActiveQuest>());
                    QuestService.SaveActiveQuests(
                        _connectionString,
                        MemberCharacterId,
                        new List<ActiveQuest>());
                    killerActive = QuestService.LoadActiveQuests(
                        _connectionString,
                        KillerCharacterId);
                    memberActive = QuestService.LoadActiveQuests(
                        _connectionString,
                        MemberCharacterId);
                }

                var monsters = new List<GameWorld.Dungeon.MonsterSumInfo>
                {
                    new GameWorld.Dungeon.MonsterSumInfo
                    {
                        Code = MasaccioCaptureMonsterCode,
                        CaptureItems = MonsterCaptureDefinitionCatalog.GetItems(
                            MasaccioCaptureMonsterCode),
                        Level = 65,
                        Type = 0,
                        IsBlocking = true,
                        PacketIndex = MonsterSequence,
                    },
                };
                var runs = CreateSharedRuns(
                    monsterType: 0,
                    monsterCode: 0,
                    dungeonId: MasaccioCaptureDungeonId,
                    difficulty: 0,
                    mapId: 10087,
                    roomMonsters: monsters);
                runs.Killer.QuestSnapshot = QuestRunSnapshot.Capture(killerActive);
                runs.Member.QuestSnapshot = QuestRunSnapshot.Capture(memberActive);
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;

                if (replaceActivation)
                {
                    SaveActiveQuest(
                        KillerCharacterId,
                        MasaccioCaptureQuestId,
                        triggerValue: 1);
                }
            }

            public void PrepareNightAssaultQuestKill()
            {
                var active = SaveKillerActiveQuest(
                    NightAssaultQuestId,
                    triggerValue: 15);
                var runs = CreateSharedRuns(
                    monsterType: 0,
                    monsterCode: NightAssaultMonsterCode,
                    dungeonId: NightAssaultDungeonId,
                    difficulty: 0,
                    mapId: 21336);
                runs.Killer.QuestSnapshot = QuestRunSnapshot.Capture(active);
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;
            }

            public void PrepareAnyMonsterQuestKill(byte monsterType)
            {
                var active = SaveKillerActiveQuest(
                    AnyMonsterQuestId,
                    triggerValue: 30);
                var runs = CreateSharedRuns(
                    monsterType,
                    AnyMonsterCode,
                    dungeonId: AnyMonsterDungeonId,
                    difficulty: 0);
                runs.Killer.QuestSnapshot = QuestRunSnapshot.Capture(active);
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;
            }

            public void PrepareEliteQuestKill(byte monsterType)
            {
                var active = SaveKillerActiveQuest(
                    EliteMonsterQuestId,
                    triggerValue: 5);
                var runs = CreateSharedRuns(
                    monsterType,
                    AnyMonsterCode,
                    dungeonId: AnyMonsterDungeonId,
                    difficulty: 0);
                runs.Killer.QuestSnapshot = QuestRunSnapshot.Capture(active);
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;
            }

            public void PrepareBossQuestKill(byte monsterType)
            {
                var active = SaveKillerActiveQuest(
                    BossMonsterQuestId,
                    triggerValue: 5);
                var runs = CreateSharedRuns(
                    monsterType,
                    AnyMonsterCode,
                    dungeonId: AnyMonsterDungeonId,
                    difficulty: 0);
                runs.Killer.QuestSnapshot = QuestRunSnapshot.Capture(active);
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;
            }

            public ushort PrepareRescueSilmaApcBossQuest()
            {
                var active = SaveKillerActiveQuest(
                    RescueSilmaQuestId,
                    triggerValue: 1);
                var monsters = new List<GameWorld.Dungeon.MonsterSumInfo>();
                for (var index = 0; index < 6; index++)
                {
                    monsters.Add(new GameWorld.Dungeon.MonsterSumInfo
                    {
                        Code = 68005,
                        Level = 18,
                        Type = 0,
                        IsBlocking = true,
                        PacketIndex = (ushort)(MonsterSequence + index),
                    });
                }
                monsters.Add(new GameWorld.Dungeon.MonsterSumInfo
                {
                    Code = RescueSilmaApcCode,
                    Level = 18,
                    Type = 8,
                    Faction = PvfLib.ApcFaction.Monster,
                    IsBlocking = false,
                    PacketIndex = (ushort)(MonsterSequence + 6),
                });
                var runs = CreateSharedRuns(
                    monsterType: 0,
                    monsterCode: 0,
                    dungeonId: RescueSilmaDungeonId,
                    difficulty: 0,
                    mapId: 13429,
                    roomMonsters: monsters);
                runs.Killer.BossMapPos = new[] { 1, 1 };
                runs.Member.BossMapPos = new[] { 1, 1 };
                runs.Killer.QuestSnapshot = QuestRunSnapshot.Capture(active);
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;
                return (ushort)(MonsterSequence + 6);
            }

            public ushort PrepareBloodAltarQuestDrop()
            {
                var active = SaveKillerActiveQuest(
                    BloodAltarQuestId,
                    triggerValue: 1);
                var runs = CreateSharedRuns(
                    monsterType: 0,
                    monsterCode: 0,
                    dungeonId: BloodAltarDungeonId,
                    mapId: BloodAltarMapId);
                runs.Killer.QuestSnapshot = QuestRunSnapshot.Capture(active);
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;

                var rewards = BloodAltarRewardDefinitionCatalog.Current;
                var definition = new BloodAltarDungeonDefinition(
                    BloodAltarDungeonId,
                    BloodAltarDungeonKind.Endless,
                    maxRounds: 2,
                    basisLevel: 60,
                    rewards);
                var runtime = new BloodAltarDungeonRuntime(definition);
                if (!runs.Killer.Instance.Mechanisms.TryAttachBloodAltar(runtime))
                    throw new InvalidOperationException("Unable to attach blood altar fixture runtime.");

                var map = new BloodAltarMapDefinition(
                    BloodAltarMapId,
                    new[]
                    {
                        new BloodAltarMonsterDefinition(
                            BloodAltarMonsterCode,
                            templateType: 0,
                            x: 640,
                            y: 300,
                            z: 0,
                            durationMilliseconds: 0,
                            spawnIntervalMilliseconds: 0,
                            baseSpawnCount: 1,
                            spawnCountIncrement: 0,
                            batchCount: 1),
                    },
                    new[]
                    {
                        new BloodAltarRoundDefinition(
                            number: 0,
                            new[]
                            {
                                new BloodAltarPhaseDefinition(
                                    round: 0,
                                    monsterTemplateIndex: 0,
                                    delayMilliseconds: 0,
                                    scale: 1f,
                                    flag: 0,
                                    concurrentPhaseCount: 1,
                                    difficulty: 0),
                            }),
                    });
                if (!runtime.TryBindMap(
                        map,
                        runs.Killer.CaptureParticipantRoomIdentity().Room,
                        out _)
                    || !runtime.TryBeginNextRound(DateTime.UtcNow, out var schedule))
                {
                    throw new InvalidOperationException("Unable to start blood altar fixture round.");
                }

                var application = new BloodAltarDungeonApplicationService();
                if (!application.TryMaterializeWave(
                        runs.Killer,
                        schedule.Generation,
                        waveIndex: 0,
                        out var wave,
                        out _,
                        out var failureReason)
                    || wave.Monsters.Count != 1)
                {
                    throw new InvalidOperationException(
                        "Unable to materialize blood altar fixture wave: "
                        + failureReason);
                }
                return wave.Monsters[0].SequenceId;
            }

            public void PrepareConditionalBossQuest()
            {
                var active = SaveKillerActiveQuest(
                    ConditionalBossQuestId,
                    triggerValue: 1);
                var runs = CreateSharedRuns(
                    monsterType: 3,
                    monsterCode: ConditionalBossMonsterCode,
                    dungeonId: ConditionalBossDungeonId,
                    difficulty: 1,
                    mapId: 17114);
                runs.Killer.QuestSnapshot = QuestRunSnapshot.Capture(active);
                runs.Killer.BossEntranceConditionTargets.Add(
                    new BossEntranceConditionTargetState
                    {
                        MonsterCode = 56611,
                    });
                runs.Killer.BossEntranceConditionalSummonCodes.Add(
                    ConditionalBossMonsterCode);
                runs.Killer.BossEntranceConditionComplete = true;
                runs.Killer.ConditionalBossSpawned = true;
                runs.Killer.ConditionalBossCode = ConditionalBossMonsterCode;
                _killer.Session.Player.CurrentRun = runs.Killer;
                _member.Session.Player.CurrentRun = runs.Member;
            }

            public void ConfirmConditionalBossDeath()
            {
                var body = new byte[4];
                BitConverter.GetBytes((ushort)KillerCharacterId).CopyTo(body, 0);
                BitConverter.GetBytes(SpecialDungeonNotifier.BossSummonRuntimeKey)
                    .CopyTo(body, 2);
                _handler.Handle_BOSS_DIE_CHECK(
                        _killer.Session,
                        new GamePacketHeader(),
                        body)
                    .GetAwaiter()
                    .GetResult();
            }

            public void ConfirmBossDeath(ushort bossSequence)
            {
                var body = new byte[4];
                BitConverter.GetBytes(_killer.Session.Player.UserId).CopyTo(body, 0);
                BitConverter.GetBytes(bossSequence).CopyTo(body, 2);
                _handler.Handle_BOSS_DIE_CHECK(
                        _killer.Session,
                        new GamePacketHeader(),
                        body)
                    .GetAwaiter()
                    .GetResult();
            }

            public uint LoadKillerQuestTrigger(ushort questId)
            {
                var quest = QuestActiveListRules.FindByQuestId(
                    QuestService.LoadActiveQuests(
                        _connectionString,
                        KillerCharacterId),
                    questId);
                return quest?.TriggerValue ?? uint.MaxValue;
            }

            public int CountKillerItem(int itemId)
                => InventoryContext.Get(KillerCharacterId)?.CountMainItem(itemId)
                    ?? 0;

            public int CountKillerProgressEvents(string eventKind)
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT COUNT(*)
FROM quest_progress_event_inbox
WHERE character_id=@characterId AND event_kind=@eventKind;";
                        command.Parameters.AddWithValue(
                            "@characterId",
                            KillerCharacterId);
                        command.Parameters.AddWithValue(
                            "@eventKind",
                            eventKind);
                        return Convert.ToInt32(command.ExecuteScalar());
                    }
                }
            }

            public void KillMonster(ushort sequenceId = MonsterSequence)
            {
                var body = new byte[4];
                BitConverter.GetBytes(sequenceId).CopyTo(body, 0);
                BitConverter.GetBytes((ushort)KillerCharacterId).CopyTo(body, 2);
                _handler.Handle_ENUM_CMDPACKET_DIE_MONSTER(
                        _killer.Session,
                        new GamePacketHeader(),
                        body)
                    .GetAwaiter()
                    .GetResult();
            }

            public void CaptureMonster(ushort sequenceId = MonsterSequence)
            {
                var body = new byte[28];
                BitConverter.GetBytes(sequenceId).CopyTo(body, 0);
                BitConverter.GetBytes((ushort)KillerCharacterId).CopyTo(body, 2);
                body[20] = 0;
                body[26] = 1;
                _handler.Handle_ENUM_CMDPACKET_DIE_MONSTER(
                        _killer.Session,
                        new GamePacketHeader(),
                        body)
                    .GetAwaiter()
                    .GetResult();
            }

            public void PickupGroundItem(ushort sceneSlot)
            {
                var body = new byte[2];
                BitConverter.GetBytes(sceneSlot).CopyTo(body, 0);
                _handler.Handle_ENUM_CMDPACKET_GET_ITEM(
                        _killer.Session,
                        new GamePacketHeader(),
                        body)
                    .GetAwaiter()
                    .GetResult();
            }

            private static DungeonRun CreateRun(
                DeathTowerSession tower,
                byte monsterType,
                int monsterCode)
            {
                var run = new DungeonRun(11000, 0)
                {
                    Tower = tower,
                    RoomKey = new RoomKey(1, 1, 33060),
                    RoomStartSequence = MonsterSequence,
                    RoomMonsters = new List<GameWorld.Dungeon.MonsterSumInfo>
                    {
                        new GameWorld.Dungeon.MonsterSumInfo
                        {
                            Code = monsterCode,
                            Level = 50,
                            Type = monsterType,
                            IsBlocking = true,
                            TemplateOrder = 0,
                            PacketIndex = MonsterSequence,
                        },
                    },
                    Seed = tower?.StageSeed ?? 0x87654321,
                    RoomLcg = tower?.StageLcg ?? new DnfLcg(0x87654321),
                };
                return run;
            }

            private static (DungeonRun Killer, DungeonRun Member) CreateSharedRuns(
                byte monsterType,
                int monsterCode,
                DungeonRewardPolicy rewardPolicy = null,
                int dungeonId = 11000,
                int difficulty = 0,
                int mapId = 33060,
                IReadOnlyCollection<GameWorld.Dungeon.MonsterSumInfo>
                    roomMonsters = null)
            {
                var instance = new DungeonInstance(
                    checked((short)dungeonId),
                    checked((byte)difficulty),
                    rewardPolicy ?? DungeonRewardPolicy.Standard);
                var roomKey = new RoomKey(1, 1, mapId);
                var monsters = roomMonsters == null
                    ? new List<GameWorld.Dungeon.MonsterSumInfo>()
                    : new List<GameWorld.Dungeon.MonsterSumInfo>(roomMonsters);
                if (roomMonsters == null && monsterCode > 0)
                {
                    monsters.Add(new GameWorld.Dungeon.MonsterSumInfo
                    {
                        Code = monsterCode,
                        Level = 50,
                        Type = monsterType,
                        IsBlocking = true,
                        TemplateOrder = 0,
                        PacketIndex = MonsterSequence,
                    });
                }
                var maze = new GameWorld.Dungeon.MazeSumInfo
                {
                    Index = mapId,
                    X = 1,
                    Y = 1,
                    Monsters = monsters,
                };
                var room = instance.GetOrCreateRoom(
                    roomKey,
                    roomId => new DungeonInstanceRoom(
                        roomId,
                        roomKey,
                        maze,
                        0x87654321,
                        MonsterSequence),
                    out _);

                DungeonRun CreateParticipant(long generation)
                {
                    var run = new DungeonRun(
                        instance,
                        DungeonIdentityGenerator.NextRunId(),
                        generation,
                        DungeonRunState.Active)
                    {
                        RoomKey = roomKey,
                        RoomStartSequence = MonsterSequence,
                        RoomMonsters = monsters,
                        Seed = room.Seed,
                        RoomLcg = new DnfLcg(room.Seed),
                    };
                    run.SetCurrentRoom(room);
                    var roomState = new RoomState
                    {
                        InstanceRoom = room,
                        Maze = maze,
                        FirstSeqId = MonsterSequence,
                        MonsterCount = checked((ushort)monsters.Count),
                        KilledSeqIds = run.RoomKilledSeqIds,
                        Seed = room.Seed,
                        Lcg = run.RoomLcg,
                    };
                    roomState.TryActivate();
                    run.RoomStates[roomKey] = roomState;
                    return run;
                }

                return (CreateParticipant(1), CreateParticipant(1));
            }

            private List<ActiveQuest> SaveActiveQuest(
                int characterId,
                ushort questId,
                uint triggerValue)
            {
                QuestService.SaveActiveQuests(
                    _connectionString,
                    characterId,
                    new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            Slot = 0,
                            QuestId = questId,
                            TriggerValue = triggerValue,
                        },
                    });
                return QuestService.LoadActiveQuests(
                    _connectionString,
                    characterId);
            }

            private List<ActiveQuest> SaveKillerActiveQuest(
                ushort questId,
                uint triggerValue)
            {
                return SaveActiveQuest(
                    KillerCharacterId,
                    questId,
                    triggerValue);
            }

            public int CountMemberItem(int itemId)
                => InventoryContext.Get(MemberCharacterId)?.CountMainItem(itemId)
                    ?? 0;

            private static PartyMember ToPartyMember(ConnectedSession connected)
            {
                return new PartyMember
                {
                    UserId = (ushort)connected.Session.Player.CharacterId,
                    CharacterId = connected.Session.Player.CharacterId,
                    SessionId = connected.Session.SessionId,
                    Name = connected.Name,
                    Level = connected.Session.Player.Level,
                    Job = connected.Session.Player.Job,
                };
            }

            private static void SeedAccountAndCharacter(string dbPath, int characterId, string name)
            {
                using (var connection = new SqliteConnection(
                    SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@id, @name, '');
INSERT INTO characters (character_id, account_id, name, job, grow_type, level, exp)
VALUES (@id, @id, @name, 4, 4, 50, 0);
INSERT INTO character_subtype1_fields (character_id)
VALUES (@id);";
                        command.Parameters.AddWithValue("@id", characterId);
                        command.Parameters.AddWithValue("@name", name);
                        command.ExecuteNonQuery();
                    }
                }
            }

            public void Dispose()
            {
                InventoryContext.Unregister(
                    _killer.Session.SessionId,
                    KillerCharacterId);
                InventoryContext.Unregister(
                    _member.Session.SessionId,
                    MemberCharacterId);
                _killer.Dispose();
                _member.Dispose();
                Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", _previousDatabasePath);
                try { File.Delete(_dbPath); } catch { }
            }
        }

        public sealed class ConnectedSession : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _peer;
            private readonly TcpClient _server;

            private ConnectedSession(
                string name,
                TcpListener listener,
                TcpClient peer,
                TcpClient server,
                EnhancedClientSession session)
            {
                Name = name;
                _listener = listener;
                _peer = peer;
                _server = server;
                Session = session;
            }

            public string Name { get; }
            public EnhancedClientSession Session { get; }

            public static ConnectedSession Create(int characterId, string name)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var peer = new TcpClient();
                var connect = peer.ConnectAsync(IPAddress.Loopback, port);
                var server = listener.AcceptTcpClient();
                connect.GetAwaiter().GetResult();
                var session = new EnhancedClientSession(server, new GamePacketHeader());
                session.Player.CharacterId = characterId;
                session.Player.UserId = (ushort)characterId;
                session.Player.Name = System.Text.Encoding.UTF8.GetBytes(name);
                session.Player.Level = 50;
                session.Player.Job = 4;
                session.Player.GrowType = 4;
                session.Account = new AccountRecord { AccountId = characterId, MId = name };
                return new ConnectedSession(name, listener, peer, server, session);
            }

            public List<ushort> ReadAvailableTypes()
            {
                var result = new List<ushort>();
                var available = _peer.Available;
                if (available <= 0)
                    return result;
                var bytes = new byte[available];
                var offset = 0;
                while (offset < bytes.Length)
                    offset += _peer.GetStream().Read(bytes, offset, bytes.Length - offset);
                offset = 0;
                while (offset + 15 <= bytes.Length)
                {
                    var length = BitConverter.ToInt32(bytes, offset + 3);
                    if (length < 15 || offset + length > bytes.Length)
                        break;
                    result.Add(BitConverter.ToUInt16(bytes, offset + 1));
                    offset += length;
                }
                return result;
            }

            public void Dispose()
            {
                _server.Dispose();
                _peer.Dispose();
                _listener.Stop();
            }
        }
    }
}
