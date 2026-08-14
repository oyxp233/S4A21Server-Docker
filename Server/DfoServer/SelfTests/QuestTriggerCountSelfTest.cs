using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class QuestTriggerCountSelfTest
    {
        private const int CharacterId = 284001;
        private const int AccountId = 284001;
        private const ushort RescueSilmaQuestId = 1791;
        private const ushort AnnoyingAntQuestId = 1821;
        private const ushort SadBellQuestId = 1835;
        private const ushort SurvivorQuestId = 1836;
        private const ushort HelpVoiceQuestId = 2021;
        private const ushort SeekAndMeetQuestId = 2043;
        private const ushort NightAssaultQuestId = 2188;
        private const ushort DragonObstacleQuestId = 20722;
        private const ushort FitzLieutenantQuestId = 2547;
        private const ushort AnyMonsterQuestId = 4303;
        private const ushort EliteMonsterQuestId = 4;
        private const ushort BossMonsterQuestId = 6;
        private const ushort SeekingPurchaseQuestId = 10;
        private const ushort SeekingSingleItemQuestId = 13092;
        private const ushort SeekingGoldQuestId = 2266;
        private const int SeekingSingleItemId = 10088630;
        private const ushort SyntheticQuestId = 65000;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_TRIGGER_COUNT selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "quest-trigger-count.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var schemaPath = ServerPaths.SchemaFilePath;
            var characterRepository = new SqliteCharacterRepository(dbPath, schemaPath);
            SeedAccount(dbPath);
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-trigger-count-test"),
                Job = 0,
                GrowType = 0,
                Level = 50,
            });

            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            var questService = new QuestService(connStr);
            var failures = 0;
            var inventorySessionId = Guid.NewGuid();
            var inventory = new InventoryService(CharacterId, AccountId);
            var inventoryLease = InventoryContext.Register(
                inventorySessionId,
                inventory);

            CheckQuestSlotLayout(ref failures);

            Check("1791 hunt-enemy single target starts at 1",
                GameWorld.QuestData.GetInitTrigger(RescueSilmaQuestId) == 1,
                ref failures);
            Check("2021 hunt-enemy single target starts at 1",
                GameWorld.QuestData.GetInitTrigger(HelpVoiceQuestId) == 1,
                ref failures);
            Check("1836 hunt-enemy two targets pack both channels",
                GameWorld.QuestData.GetInitTrigger(SurvivorQuestId) == 513,
                ref failures);
            Check("1821 hunt-monster keeps four-field packing",
                GameWorld.QuestData.GetInitTrigger(AnnoyingAntQuestId) == 517,
                ref failures);
            Check("2547 packs both hunt-monster target counts",
                GameWorld.QuestData.GetInitTrigger(FitzLieutenantQuestId)
                    == 20540,
                ref failures);

            var dragonTargets =
                GameWorld.QuestData.GetHuntMonsterTargets(
                    DragonObstacleQuestId);
            Check("20722 parses dungeon, minimum difficulty and monster",
                dragonTargets.Count == 1
                    && dragonTargets[0].DungeonId == 3536
                    && dragonTargets[0].MinimumDifficulty == 2
                    && dragonTargets[0].MonsterCode == 100003
                    && dragonTargets[0].RequiredCount == 3,
                ref failures);
            var fitzTargets = GameWorld.QuestData.GetHuntMonsterTargets(
                FitzLieutenantQuestId);
            Check("2547 resolves both configured monster channels",
                fitzTargets.Count == 2
                    && fitzTargets[0].DungeonId == 101
                    && fitzTargets[0].MonsterCode == 63046
                    && fitzTargets[0].RequiredCount == 60
                    && fitzTargets[0].ChannelIndex == 0
                    && fitzTargets[1].DungeonId == 101
                    && fitzTargets[1].MonsterCode == 63047
                    && fitzTargets[1].RequiredCount == 40
                    && fitzTargets[1].ChannelIndex == 1,
                ref failures);
            var nightAssaultTargets = GameWorld.QuestData.GetHuntMonsterTargets(
                NightAssaultQuestId);
            Check("2188 resolves the configured Night Assault monster target",
                nightAssaultTargets.Count == 1
                    && nightAssaultTargets[0].DungeonId == 83
                    && nightAssaultTargets[0].MinimumDifficulty == -1
                    && nightAssaultTargets[0].MonsterCode == 61438
                    && nightAssaultTargets[0].RequiredCount == 15
                    && nightAssaultTargets[0].ChannelIndex == 0,
                ref failures);
            var anyMonsterTargets = GameWorld.QuestData.GetHuntMonsterTargets(
                AnyMonsterQuestId);
            Check("4303 preserves the PVF any-monster wildcard",
                anyMonsterTargets.Count == 1
                    && anyMonsterTargets[0].DungeonId == -1
                    && anyMonsterTargets[0].MinimumDifficulty == -1
                    && anyMonsterTargets[0].MonsterCode == -1
                    && anyMonsterTargets[0].RequiredCount == 30
                    && anyMonsterTargets[0].ChannelIndex == 0,
                ref failures);
            Check("4303 wildcard does not materialize a synthetic quest actor",
                GameWorld.QuestData.GetUnfinishedDungeonActorTargets(
                    AnyMonsterQuestId,
                    trigger: 30,
                    dungeonId: 144,
                    difficulty: 0).Count == 0,
                ref failures);
            var eliteTargets = GameWorld.QuestData.GetHuntMonsterTargets(
                EliteMonsterQuestId);
            Check("4 preserves the PVF elite-monster wildcard",
                eliteTargets.Count == 1
                    && eliteTargets[0].DungeonId == -1
                    && eliteTargets[0].MinimumDifficulty == -1
                    && eliteTargets[0].MonsterCode == -2
                    && eliteTargets[0].Kind
                        == GameWorld.HuntMonsterTargetKind.AnyEliteMonster
                    && eliteTargets[0].RequiredCount == 5,
                ref failures);
            Check("4 elite wildcard does not materialize a synthetic quest actor",
                GameWorld.QuestData.GetUnfinishedDungeonActorTargets(
                    EliteMonsterQuestId,
                    trigger: 5,
                    dungeonId: 144,
                    difficulty: 0).Count == 0,
                ref failures);
            var bossTargets = GameWorld.QuestData.GetHuntMonsterTargets(
                BossMonsterQuestId);
            Check("6 preserves the PVF boss-monster wildcard",
                bossTargets.Count == 1
                    && bossTargets[0].DungeonId == -1
                    && bossTargets[0].MinimumDifficulty == -1
                    && bossTargets[0].MonsterCode == -3
                    && bossTargets[0].Kind
                        == GameWorld.HuntMonsterTargetKind.AnyBossMonster
                    && bossTargets[0].RequiredCount == 5,
                ref failures);
            Check("6 boss wildcard does not materialize a synthetic quest actor",
                GameWorld.QuestData.GetUnfinishedDungeonActorTargets(
                    BossMonsterQuestId,
                    trigger: 5,
                    dungeonId: 144,
                    difficulty: 0).Count == 0,
                ref failures);

            Check("hunt-enemy is not treated as a seeking item quest",
                GameWorld.QuestData.GetSeekingConsumeItems(SurvivorQuestId).Count == 0,
                ref failures);
            Check("hunt-monster is not treated as a seeking item quest",
                GameWorld.QuestData.GetSeekingConsumeItems(AnnoyingAntQuestId).Count == 0,
                ref failures);
            Check("use-item quest is not treated as a seeking item quest",
                GameWorld.QuestData.GetSeekingConsumeItems(SadBellQuestId).Count == 0,
                ref failures);
            Check("seek-and-meet npc quest still exposes its item requirement",
                GameWorld.QuestData.GetSeekingConsumeItems(SeekAndMeetQuestId).Count == 1,
                ref failures);

            MarkQuestCleared(connStr, SadBellQuestId);
            var acceptSurvivor = questService.HandleAcceptQuest(CharacterId,
                BuildQuestBody(SurvivorQuestId), AccountId);
            Check("accepting 1836 succeeds", IsSuccessAck(acceptSurvivor), ref failures);
            Check("accepting 1836 stores packed hunt-enemy counts",
                TryReadAcceptTrigger(acceptSurvivor, out var acceptTrigger) && acceptTrigger == 513,
                ref failures);

            QuestService.SaveActiveQuests(
                connStr,
                CharacterId,
                new System.Collections.Generic.List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = DragonObstacleQuestId,
                        TriggerValue = 3,
                    },
                });
            var belowDifficulty = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 1,
                monsterCode: 100003);
            Check("20722 ignores kills below required difficulty",
                belowDifficulty.Count == 0
                    && LoadTrigger(connStr, DragonObstacleQuestId) == 3,
                ref failures);

            var firstKill = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 2,
                monsterCode: 100003);
            Check("20722 matching kill decrements one trigger",
                firstKill.Count == 1
                    && firstKill[0].PreviousTriggerValue == 3
                    && firstKill[0].TriggerValue == 2
                    && LoadTrigger(connStr, DragonObstacleQuestId) == 2,
                ref failures);

            var wrongMonster = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 4,
                monsterCode: 100004);
            Check("20722 ignores another monster",
                wrongMonster.Count == 0
                    && LoadTrigger(connStr, DragonObstacleQuestId) == 2,
                ref failures);

            questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 4,
                monsterCode: 100003);
            var finalKill = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 4,
                monsterCode: 100003);
            Check("20722 completes after three qualified kills",
                finalKill.Count == 1
                    && finalKill[0].PreviousTriggerValue == 1
                    && finalKill[0].TriggerValue == 0
                    && LoadTrigger(connStr, DragonObstacleQuestId) == 0,
                ref failures);

            var run = new DungeonRun(3536, 2);
            run.MarkServerDrivenQuestTrigger(
                DragonObstacleQuestId,
                channelIndex: 0);
            Check("server-driven hunt trigger rejects another channel",
                !run.TryConsumeServerDrivenQuestTrigger(
                    DragonObstacleQuestId,
                    triggerType: 0x20),
                ref failures);
            Check("server-driven hunt trigger consumes its client channel",
                run.TryConsumeServerDrivenQuestTrigger(
                    DragonObstacleQuestId,
                    triggerType: 0x10)
                    && !run.TryConsumeServerDrivenQuestTrigger(
                        DragonObstacleQuestId,
                        triggerType: 0x10),
                ref failures);

            run.MarkServerDrivenQuestTrigger(
                DragonObstacleQuestId,
                channelIndex: 0);
            run.MarkServerDrivenQuestTrigger(
                DragonObstacleQuestId,
                channelIndex: 1);
            Check("combined hunt echo consumes both matching channels",
                run.TryConsumeServerDrivenQuestTrigger(
                    DragonObstacleQuestId,
                    triggerType: 0x30)
                    && !run.HasPendingServerDrivenQuestTriggers(),
                ref failures);

            CheckTransactionalProgress(
                connStr,
                questService,
                ref failures);
            failures += CheckHuntMonsterClientProjectionAsync(
                    connStr,
                    inventorySessionId)
                .GetAwaiter()
                .GetResult();
            failures += CheckNpcShopSeekingProjectionAsync(
                    connStr,
                    dbPath,
                    inventoryLease,
                    inventory)
                .GetAwaiter()
                .GetResult();
            failures += CheckMailboxAndDisjointSeekingProjectionAsync(
                    connStr,
                    dbPath,
                    inventoryLease,
                    inventory)
                .GetAwaiter()
                .GetResult();
            failures += CheckPhysicalSeekingMutationProjectionAsync(
                    connStr,
                    inventoryLease,
                    inventory)
                .GetAwaiter()
                .GetResult();
            failures += CheckVirtualGoldSeekingProjectionAsync(
                    connStr,
                    questService,
                    inventoryLease,
                    inventory)
                .GetAwaiter()
                .GetResult();

            InventoryContext.Unregister(inventorySessionId, CharacterId);
            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckTransactionalProgress(
            string connStr,
            QuestService questService,
            ref int failures)
        {
            SaveActiveQuest(connStr, DragonObstacleQuestId, 3);
            var snapshot = QuestRunSnapshot.Capture(
                QuestService.LoadActiveQuests(connStr, CharacterId));
            var replayEventId = Guid.NewGuid();
            var first = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 2,
                monsterCode: 100003,
                sourceEventId: replayEventId,
                eligibleQuestIds: snapshot.QuestIds);
            var replay = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 2,
                monsterCode: 100003,
                sourceEventId: replayEventId,
                eligibleQuestIds: snapshot.QuestIds);
            Check("same dungeon EventId replays hunt progress as a no-op",
                first.Count == 1
                    && replay.Count == 0
                    && LoadTrigger(connStr, DragonObstacleQuestId) == 2
                    && CountProgressEvents(connStr, replayEventId) == 1,
                ref failures);

            SaveActiveQuest(connStr, AnyMonsterQuestId, 30);
            var anyMonsterSnapshot = QuestRunSnapshot.Capture(
                QuestService.LoadActiveQuests(connStr, CharacterId));
            var anyMonsterEventId = Guid.NewGuid();
            var anyMonsterFirst = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 144,
                difficulty: 0,
                monsterCode: 65301,
                sourceEventId: anyMonsterEventId,
                eligibleQuestIds: anyMonsterSnapshot.QuestIds,
                eligibleQuestActivations: anyMonsterSnapshot.Activations);
            var anyMonsterReplay = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 144,
                difficulty: 0,
                monsterCode: 65301,
                sourceEventId: anyMonsterEventId,
                eligibleQuestIds: anyMonsterSnapshot.QuestIds,
                eligibleQuestActivations: anyMonsterSnapshot.Activations);
            var anotherMonster = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 4,
                monsterCode: 100003,
                sourceEventId: Guid.NewGuid(),
                eligibleQuestIds: anyMonsterSnapshot.QuestIds,
                eligibleQuestActivations: anyMonsterSnapshot.Activations);
            Check("4303 counts any canonical monster and deduplicates one death fact",
                anyMonsterFirst.Count == 1
                    && anyMonsterFirst[0].PreviousTriggerValue == 30
                    && anyMonsterFirst[0].TriggerValue == 29
                    && anyMonsterReplay.Count == 0
                    && anotherMonster.Count == 1
                    && anotherMonster[0].PreviousTriggerValue == 29
                    && anotherMonster[0].TriggerValue == 28
                    && LoadTrigger(connStr, AnyMonsterQuestId) == 28
                    && CountProgressEvents(connStr, anyMonsterEventId) == 1,
                ref failures);

            var anyMonsterEliteEventId = Guid.NewGuid();
            var anyMonsterElite = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 144,
                difficulty: 0,
                monsterCode: 65301,
                sourceEventId: anyMonsterEliteEventId,
                eligibleQuestIds: anyMonsterSnapshot.QuestIds,
                eligibleQuestActivations: anyMonsterSnapshot.Activations,
                monsterType: 1);
            Check("4303 ordinary wildcard excludes elite deaths",
                anyMonsterElite.Count == 0
                    && LoadTrigger(connStr, AnyMonsterQuestId) == 28
                    && CountProgressEvents(connStr, anyMonsterEliteEventId) == 0,
                ref failures);

            SaveActiveQuest(connStr, EliteMonsterQuestId, 5);
            var eliteSnapshot = QuestRunSnapshot.Capture(
                QuestService.LoadActiveQuests(connStr, CharacterId));
            var eliteOrdinaryEventId = Guid.NewGuid();
            var eliteOrdinary = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 144,
                difficulty: 0,
                monsterCode: 65301,
                sourceEventId: eliteOrdinaryEventId,
                eligibleQuestIds: eliteSnapshot.QuestIds,
                eligibleQuestActivations: eliteSnapshot.Activations,
                monsterType: 0);
            var eliteEventId = Guid.NewGuid();
            var eliteFirst = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 144,
                difficulty: 0,
                monsterCode: 65301,
                sourceEventId: eliteEventId,
                eligibleQuestIds: eliteSnapshot.QuestIds,
                eligibleQuestActivations: eliteSnapshot.Activations,
                monsterType: 1);
            var eliteReplay = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 144,
                difficulty: 0,
                monsterCode: 65301,
                sourceEventId: eliteEventId,
                eligibleQuestIds: eliteSnapshot.QuestIds,
                eligibleQuestActivations: eliteSnapshot.Activations,
                monsterType: 1);
            Check("4 counts only canonical elite deaths and deduplicates one fact",
                eliteOrdinary.Count == 0
                    && CountProgressEvents(connStr, eliteOrdinaryEventId) == 0
                    && eliteFirst.Count == 1
                    && eliteFirst[0].PreviousTriggerValue == 5
                    && eliteFirst[0].TriggerValue == 4
                    && eliteReplay.Count == 0
                    && LoadTrigger(connStr, EliteMonsterQuestId) == 4
                    && CountProgressEvents(connStr, eliteEventId) == 1,
                ref failures);

            SaveActiveQuest(connStr, BossMonsterQuestId, 5);
            var bossSnapshot = QuestRunSnapshot.Capture(
                QuestService.LoadActiveQuests(connStr, CharacterId));
            var bossOrdinaryEventId = Guid.NewGuid();
            var bossOrdinary = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 144,
                difficulty: 0,
                monsterCode: 65301,
                sourceEventId: bossOrdinaryEventId,
                eligibleQuestIds: bossSnapshot.QuestIds,
                eligibleQuestActivations: bossSnapshot.Activations,
                monsterType: 0);
            var bossEliteEventId = Guid.NewGuid();
            var bossElite = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 144,
                difficulty: 0,
                monsterCode: 65301,
                sourceEventId: bossEliteEventId,
                eligibleQuestIds: bossSnapshot.QuestIds,
                eligibleQuestActivations: bossSnapshot.Activations,
                monsterType: 1);
            var bossEventId = Guid.NewGuid();
            var bossFirst = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 144,
                difficulty: 0,
                monsterCode: 65301,
                sourceEventId: bossEventId,
                eligibleQuestIds: bossSnapshot.QuestIds,
                eligibleQuestActivations: bossSnapshot.Activations,
                monsterType: 3);
            var bossReplay = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 144,
                difficulty: 0,
                monsterCode: 65301,
                sourceEventId: bossEventId,
                eligibleQuestIds: bossSnapshot.QuestIds,
                eligibleQuestActivations: bossSnapshot.Activations,
                monsterType: 3);
            Check("6 counts only canonical boss deaths and deduplicates one fact",
                bossOrdinary.Count == 0
                    && bossElite.Count == 0
                    && CountProgressEvents(connStr, bossOrdinaryEventId) == 0
                    && CountProgressEvents(connStr, bossEliteEventId) == 0
                    && bossFirst.Count == 1
                    && bossFirst[0].PreviousTriggerValue == 5
                    && bossFirst[0].TriggerValue == 4
                    && bossReplay.Count == 0
                    && LoadTrigger(connStr, BossMonsterQuestId) == 4
                    && CountProgressEvents(connStr, bossEventId) == 1,
                ref failures);

            SaveActiveQuest(connStr, SyntheticQuestId, 1);
            var clearChanged = QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId: 0,
                mapId: 33060,
                (questId, dungeonId, mapId) =>
                    questId == SyntheticQuestId,
                replayEventId,
                new[] { SyntheticQuestId });
            Check("same fact id keeps distinct hunt and clear-map inbox kinds",
                clearChanged == 1
                && LoadTrigger(connStr, SyntheticQuestId) == 0
                && CountProgressEvents(connStr, replayEventId) == 2,
                ref failures);

            SaveActiveQuest(connStr, SyntheticQuestId, 1);
            var failedEventId = Guid.NewGuid();
            var failedClosed = QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId: 0,
                mapId: 33061,
                (questId, dungeonId, mapId) =>
                    throw new InvalidOperationException(
                        "selftest invalid quest definition"),
                failedEventId,
                new[] { SyntheticQuestId });
            var retryChanged = QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId: 0,
                mapId: 33061,
                (questId, dungeonId, mapId) =>
                    questId == SyntheticQuestId,
                failedEventId,
                new[] { SyntheticQuestId });
            Check("invalid objective rolls back progress and inbox before safe retry",
                failedClosed == 0
                && retryChanged == 1
                && LoadTrigger(connStr, SyntheticQuestId) == 0
                && CountProgressEvents(connStr, failedEventId) == 1,
                ref failures);

            SaveActiveQuest(connStr, SyntheticQuestId, 1);
            var invalidDefinition = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 2,
                monsterCode: 100003,
                sourceEventId: Guid.NewGuid(),
                eligibleQuestIds: new[] { SyntheticQuestId });
            Check("missing hunt objective cannot submit task completion",
                invalidDefinition.Count == 0
                && LoadTrigger(connStr, SyntheticQuestId) == 1,
                ref failures);

            Check("reward resolver distinguishes a missing definition from a valid quest",
                !GameWorld.QuestData.ResolveReward(SyntheticQuestId).IsValid
                && GameWorld.QuestData.ResolveReward(DragonObstacleQuestId).IsValid,
                ref failures);
            SaveActiveQuest(connStr, SyntheticQuestId, 0);
            var invalidRewardFinish = QuestSelfTestCommandAdapter.HandleFinish(
                questService,
                CharacterId,
                QuestSelfTestCommandAdapter.BuildFinishBody(SyntheticQuestId));
            Check("invalid reward definition cannot commit quest completion",
                !invalidRewardFinish.Success
                && LoadTrigger(connStr, SyntheticQuestId) == 0
                && !questService.IsQuestCleared(CharacterId, SyntheticQuestId),
                ref failures);

            QuestService.SaveActiveQuests(
                connStr,
                CharacterId,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = DragonObstacleQuestId,
                        TriggerValue = 3,
                    },
                    new ActiveQuest
                    {
                        Slot = 1,
                        QuestId = SyntheticQuestId,
                        TriggerValue = 1,
                    },
                });
            var frozenSnapshot = QuestRunSnapshot.Capture(
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = DragonObstacleQuestId,
                        TriggerValue = 3,
                    },
                });
            var filteredChanged = QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId: 0,
                mapId: 33062,
                (questId, dungeonId, mapId) => true,
                Guid.NewGuid(),
                frozenSnapshot.QuestIds);
            Check("run snapshot filters quests accepted outside the frozen set",
                filteredChanged == 1
                && LoadTrigger(connStr, DragonObstacleQuestId) == 0
                && LoadTrigger(connStr, SyntheticQuestId) == 1,
                ref failures);

            SaveActiveQuest(connStr, DragonObstacleQuestId, 3);
            var duplicateRejected = false;
            try
            {
                using (var connection = new SqliteConnection(connStr))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO character_active_quests
    (character_id, slot, quest_id, trigger_value, version)
VALUES (@cid, 1, @qid, 3, 0);";
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.Parameters.AddWithValue(
                            "@qid",
                            DragonObstacleQuestId);
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                duplicateRejected = true;
            }
            Check("active quest identity is unique independently of client slot",
                duplicateRejected,
                ref failures);

            SaveActiveQuest(connStr, DragonObstacleQuestId, 3);
            var start = new ManualResetEventSlim(false);
            QuestSetTriggerResult clientResult = null;
            IReadOnlyList<QuestSetTriggerResult> serverResult = null;
            Exception clientError = null;
            Exception serverError = null;
            var concurrentEventId = Guid.NewGuid();
            var clientTask = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    clientResult = questService.HandleSetTrigger(
                        CharacterId,
                        BuildSetTriggerBody(DragonObstacleQuestId));
                }
                catch (Exception ex)
                {
                    clientError = ex;
                }
            });
            var serverTask = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    serverResult = questService.SyncHuntMonsterQuestProgress(
                        CharacterId,
                        dungeonId: 3536,
                        difficulty: 2,
                        monsterCode: 100003,
                        sourceEventId: concurrentEventId,
                        eligibleQuestIds: new[] { DragonObstacleQuestId });
                }
                catch (Exception ex)
                {
                    serverError = ex;
                }
            });
            start.Set();
            Task.WaitAll(clientTask, serverTask);
            var concurrentQuest = LoadActiveQuest(
                connStr,
                DragonObstacleQuestId);
            Check("server-owned client echo and server kill serialize without lost progress",
                clientError == null
                && serverError == null
                && clientResult != null
                && clientResult.Success
                && serverResult != null
                && serverResult.Count == 1
                && concurrentQuest != null
                && concurrentQuest.TriggerValue == 2
                && concurrentQuest.Version == 1
                && CountProgressEvents(connStr, concurrentEventId) == 1,
                ref failures);
        }

        private static void CheckQuestSlotLayout(ref int failures)
        {
            var sparse = QuestSlotLayout.ProjectFixedSlots(
                new List<ActiveQuest>
                {
                    new ActiveQuest { Slot = 0, QuestId = 100, TriggerValue = 1 },
                    new ActiveQuest { Slot = 29, QuestId = 200, TriggerValue = 2 },
                });
            Check("A14 active quest layout contains thirty fixed slots",
                sparse.Length == 30,
                ref failures);
            Check("fixed quest projection preserves sparse slot identity",
                sparse[0]?.QuestId == 100
                    && sparse[1] == null
                    && sparse[29]?.QuestId == 200,
                ref failures);

            var full = new List<ActiveQuest>();
            for (int slot = 0; slot < QuestSlotLayout.ActiveSlotCount; slot++)
            {
                full.Add(new ActiveQuest
                {
                    Slot = slot,
                    QuestId = (ushort)(30000 + slot),
                });
            }

            Check("all thirty occupied slots have no free active quest slot",
                QuestService.FindFreeSlot(full) == -1,
                ref failures);
            Check("full-list race uses an A14 handled accept error",
                BitConverter.ToString(QuestAckBuilder.BuildAccept(
                    QuestAcceptResult.Fail(
                        QuestSlotLayout.ActiveListFullFallbackError)))
                    == "00-17",
                ref failures);
        }

        private static async Task<int> CheckHuntMonsterClientProjectionAsync(
            string connStr,
            Guid sessionId)
        {
            var failures = 0;
            SaveActiveQuest(connStr, FitzLieutenantQuestId, 20540);
            var sender = new RecordingSender();
            var run = new DungeonRun(101, 0);
            sender.Player.CurrentRun = run;
            var clock = new ClockService();
            var manager = new QuestManager(
                sender,
                connStr,
                TimeSpan.FromMilliseconds(40),
                clock);

            await manager.SyncHuntMonsterQuestProgressAsync(
                dungeonId: 101,
                difficulty: 0,
                monsterCode: 63046,
                sourceEventId: Guid.NewGuid(),
                eligibleQuestIds: new[] { FitzLieutenantQuestId },
                sourceRunIdentity: run.CaptureIdentity());
            await manager.SyncHuntMonsterQuestProgressAsync(
                dungeonId: 101,
                difficulty: 0,
                monsterCode: 63047,
                sourceEventId: Guid.NewGuid(),
                eligibleQuestIds: new[] { FitzLieutenantQuestId },
                sourceRunIdentity: run.CaptureIdentity());
            Check("2547 persists both channels before client echo",
                LoadTrigger(connStr, FitzLieutenantQuestId) == 20027,
                ref failures);
            Check("server hunt progress does not immediately rebuild 0x023F",
                sender.CountCalls("NOTI:023F") == 0,
                ref failures);

            await manager.HandleSetTriggerAsync(
                0x0021,
                BuildWireSetTriggerBody(
                    FitzLieutenantQuestId,
                    triggerType: 0x20),
                sessionId);
            await manager.HandleSetTriggerAsync(
                0x0021,
                BuildWireSetTriggerBody(
                    FitzLieutenantQuestId,
                    triggerType: 0x10),
                sessionId);
            clock.CheckOnce(DateTime.UtcNow.AddSeconds(1));
            await Task.Delay(10);
            Check("out-of-order 2547 echoes ACK without double decrement",
                sender.CountCalls("ACK:0021") == 2
                    && LoadTrigger(connStr, FitzLieutenantQuestId) == 20027,
                ref failures);
            Check("matching client echo cancels the full-list fallback",
                sender.CountCalls("NOTI:023F") == 0,
                ref failures);

            SaveActiveQuest(connStr, DragonObstacleQuestId, 3);
            var fallbackSender = new RecordingSender();
            var fallbackRun = new DungeonRun(3536, 2);
            fallbackSender.Player.CurrentRun = fallbackRun;
            var fallbackClock = new ClockService();
            var fallbackManager = new QuestManager(
                fallbackSender,
                connStr,
                TimeSpan.FromMilliseconds(40),
                fallbackClock);
            await fallbackManager.SyncHuntMonsterQuestProgressAsync(
                dungeonId: 3536,
                difficulty: 2,
                monsterCode: 100003,
                sourceEventId: Guid.NewGuid(),
                eligibleQuestIds: new[] { DragonObstacleQuestId },
                sourceRunIdentity: fallbackRun.CaptureIdentity());
            await fallbackManager.SyncHuntMonsterQuestProgressAsync(
                dungeonId: 3536,
                difficulty: 2,
                monsterCode: 100003,
                sourceEventId: Guid.NewGuid(),
                eligibleQuestIds: new[] { DragonObstacleQuestId },
                sourceRunIdentity: fallbackRun.CaptureIdentity());
            fallbackClock.CheckOnce(DateTime.UtcNow.AddSeconds(1));
            await Task.Delay(10);
            Check("missing client echo receives one coalesced 0x023F fallback",
                fallbackSender.CountCalls("NOTI:023F") == 1
                    && LoadTrigger(connStr, DragonObstacleQuestId) == 1,
                ref failures);
            fallbackClock.CheckOnce(DateTime.UtcNow.AddSeconds(2));
            await Task.Delay(10);
            Check("hunt projection fallback is one-shot",
                fallbackSender.CountCalls("NOTI:023F") == 1,
                ref failures);

            SaveActiveQuest(connStr, DragonObstacleQuestId, 3);
            var staleSender = new RecordingSender();
            var staleRun = new DungeonRun(3536, 2);
            staleSender.Player.CurrentRun = staleRun;
            var staleClock = new ClockService();
            var staleManager = new QuestManager(
                staleSender,
                connStr,
                TimeSpan.FromMilliseconds(40),
                staleClock);
            await staleManager.SyncHuntMonsterQuestProgressAsync(
                dungeonId: 3536,
                difficulty: 2,
                monsterCode: 100003,
                sourceEventId: Guid.NewGuid(),
                eligibleQuestIds: new[] { DragonObstacleQuestId },
                sourceRunIdentity: staleRun.CaptureIdentity());
            staleSender.Player.CurrentRun = new DungeonRun(3536, 2);
            staleClock.CheckOnce(DateTime.UtcNow.AddSeconds(1));
            await Task.Delay(10);
            Check("previous run hunt fallback cannot refresh a replacement run",
                staleSender.CountCalls("NOTI:023F") == 0,
                ref failures);
            return failures;
        }

        private static async Task<int> CheckNpcShopSeekingProjectionAsync(
            string connStr,
            string dbPath,
            InventoryLease inventoryLease,
            InventoryService inventory)
        {
            var failures = 0;
            var seekingItems = GameWorld.QuestData.GetSeekingConsumeItems(
                SeekingPurchaseQuestId);
            Check("quest 10 exposes its five seeking item requirements",
                seekingItems.Count == 5,
                ref failures);

            const int purchasedItemId = 3034;
            var purchasedCount = 0;
            var prepared = true;
            foreach (var item in seekingItems)
            {
                if (item.ItemId == purchasedItemId)
                {
                    purchasedCount = item.Count;
                    continue;
                }

                prepared &= TrySetHeldItemCount(
                    inventory,
                    item.ItemId,
                    item.Count);
            }

            var metadata = ItemMetadataResolver.Resolve(purchasedItemId);
            prepared &= metadata != null
                && metadata.NeedMaterialId > 0
                && metadata.NeedMaterialCount > 0
                && purchasedCount > 0;
            if (prepared)
            {
                prepared &= InventoryRewardGrantService.TryCreateAndInsert(
                    inventory,
                    metadata.NeedMaterialId,
                    ItemCreateReason.NpcShopPurchase,
                    checked(metadata.NeedMaterialCount * purchasedCount),
                    out _);
            }
            inventory.SetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                100000000);
            Check("NPC seeking fixture prepares every non-purchased requirement",
                prepared,
                ref failures);

            SaveActiveQuest(
                connStr,
                SeekingPurchaseQuestId,
                (uint)Math.Max(1, purchasedCount));
            var previousDatabasePath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");
            InventoryMutationResult purchase = null;
            var purchased = false;
            try
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    dbPath);
                purchased = InventoryShopRuntimeService.TryBuyNpcItem(
                    inventory,
                    purchasedItemId,
                    purchasedCount,
                    out purchase);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);
            }
            Check("NPC shop buys the final seeking requirement",
                purchased
                    && purchase != null
                    && inventory.CountMainItem(purchasedItemId)
                        == purchasedCount,
                ref failures);

            var sender = new RecordingSender();
            var manager = new QuestManager(sender, connStr);
            await manager.SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                inventoryLease,
                purchase);
            Check("NPC purchase immediately completes the seeking quest",
                LoadTrigger(connStr, SeekingPurchaseQuestId) == 0,
                ref failures);
            Check("NPC purchase projects one typed trigger transition",
                sender.CountCalls("ACK:0021") == 1
                    && sender.CountCalls("NOTI:023F") == 0,
                ref failures);

            InventoryService.TryResolveMainVirtualSlotByItemId(
                purchasedItemId,
                out var purchasedSlot,
                out _);
            var discarded = InventoryDeleteService.TryDeleteForClient(
                inventory,
                InventoryListType.Main,
                purchasedSlot,
                purchasedCount,
                out var discardMutation);
            manager.RecalibrateItemSeekingQuestProgressAfterInventoryMutationsWithoutNotification(
                inventoryLease,
                new[]
                {
                    discardMutation,
                    new InventoryMutationResult { ItemTemplateId = 1004 },
                });
            Check("discarding the final seeking item recalibrates a completed quest",
                discarded
                    && discardMutation != null
                    && discardMutation.ItemTemplateId == purchasedItemId
                    && LoadTrigger(connStr, SeekingPurchaseQuestId)
                        == (uint)purchasedCount
                    && sender.CountCalls("ACK:0021") == 1,
                ref failures);
            Check("extended delete mutations do not rebuild the active quest list",
                sender.CountCalls("NOTI:023F") == 0,
                ref failures);
            Check("discarded seeking progress remains incomplete after reload",
                QuestService.FindByQuestId(
                        QuestService.LoadActiveQuests(connStr, CharacterId),
                        SeekingPurchaseQuestId)
                    ?.TriggerValue == (uint)purchasedCount,
                ref failures);

            var restored = TrySetHeldItemCount(
                inventory,
                purchasedItemId,
                purchasedCount);
            await manager.SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                inventoryLease,
                new InventoryMutationResult { ItemTemplateId = purchasedItemId });
            Check("restoring the final seeking item completes the same active quest",
                restored
                    && LoadTrigger(connStr, SeekingPurchaseQuestId) == 0
                    && sender.CountCalls("ACK:0021") == 2,
                ref failures);

            var consumed = inventory.TryConsumeMainItem(
                purchasedItemId,
                1,
                out _);
            await manager.SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                inventoryLease,
                new InventoryMutationResult
                {
                    ItemTemplateId = 1004,
                    CostItemTemplateId = purchasedItemId,
                });
            Check("NPC purchase cost item reduction recalibrates seeking progress",
                consumed
                && LoadTrigger(connStr, SeekingPurchaseQuestId) == 1
                    && sender.CountCalls("ACK:0021") == 3,
                ref failures);

            await manager.SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                inventoryLease,
                new InventoryMutationResult
                {
                    ItemTemplateId = 1004,
                });
            Check("unrelated NPC purchase does not refresh seeking quests",
                LoadTrigger(connStr, SeekingPurchaseQuestId) == 1
                    && sender.CountCalls("ACK:0021") == 3
                    && sender.CountCalls("NOTI:023F") == 0,
                ref failures);

            inventory.SetMainVirtualCount(purchasedSlot, purchasedCount);
            var staleLease = new InventoryLease(
                Guid.NewGuid(),
                CharacterId,
                inventory,
                inventoryLease.Version + 1);
            manager.RecalibrateItemSeekingQuestProgressAfterInventoryMutationWithoutNotification(
                staleLease,
                new InventoryMutationResult
                {
                    ItemTemplateId = purchasedItemId,
                });
            Check("stale inventory lease cannot project seeking progress",
                LoadTrigger(connStr, SeekingPurchaseQuestId) == 1
                    && sender.CountCalls("ACK:0021") == 3,
                ref failures);
            return failures;
        }

        private static async Task<int> CheckPhysicalSeekingMutationProjectionAsync(
            string connStr,
            InventoryLease inventoryLease,
            InventoryService inventory)
        {
            var failures = 0;
            var seekingItems = GameWorld.QuestData.GetSeekingConsumeItems(
                SeekingSingleItemQuestId);
            Check("13092 exposes its single physical seeking item",
                seekingItems.Count == 1
                    && seekingItems[0].ItemId == SeekingSingleItemId
                    && seekingItems[0].Count == 1,
                ref failures);

            SaveActiveQuest(connStr, SeekingSingleItemQuestId, 0);
            var sender = new RecordingSender();
            var manager = new QuestManager(sender, connStr);
            var granted = InventoryRewardGrantService.TryCreateAndInsert(
                inventory,
                SeekingSingleItemId,
                ItemCreateReason.QuestReward,
                1,
                out var grant);
            Check("13092 fixture creates the physical seeking item",
                granted
                    && grant != null
                    && grant.Success
                    && grant.Kind == InventoryRewardGrantKind.InventoryItem
                    && inventory.CountMainItem(SeekingSingleItemId) == 1,
                ref failures);

            InventoryMutationResult deleteMutation = null;
            var deleted = grant != null
                && InventoryDeleteService.TryDeleteForClient(
                    inventory,
                    grant.ListType,
                    grant.SlotIndex,
                    1,
                    out deleteMutation);
            manager.RecalibrateItemSeekingQuestProgressAfterInventoryMutationWithoutNotification(
                inventoryLease,
                deleteMutation);
            Check("ordinary physical delete reopens completed seeking quest",
                deleted
                    && deleteMutation != null
                    && deleteMutation.ItemTemplateId == SeekingSingleItemId
                    && inventory.CountMainItem(SeekingSingleItemId) == 0
                    && LoadTrigger(connStr, SeekingSingleItemQuestId) == 1
                    && sender.CountCalls("ACK:0021") == 0
                    && sender.CountCalls("NOTI:023F") == 0,
                ref failures);

            var restored = InventoryRewardGrantService.TryCreateAndInsert(
                inventory,
                SeekingSingleItemId,
                ItemCreateReason.QuestReward,
                1,
                out var restoredGrant);
            await manager.SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                inventoryLease,
                new InventoryMutationResult
                {
                    ItemTemplateId = SeekingSingleItemId,
                });
            Check("restoring physical seeking item completes quest 13092",
                restored
                    && restoredGrant != null
                    && LoadTrigger(connStr, SeekingSingleItemQuestId) == 0
                    && sender.CountCalls("ACK:0021") == 1,
                ref failures);

            var sellCode = InventoryShopRuntimeService.TrySellItem(
                inventory,
                restoredGrant.ListType,
                restoredGrant.SlotIndex,
                1,
                out var sellMutation);
            manager.RecalibrateItemSeekingQuestProgressAfterInventoryMutationWithoutNotification(
                inventoryLease,
                sellMutation);
            Check("selling physical seeking item reopens completed quest",
                sellCode == 0
                    && sellMutation != null
                    && sellMutation.ItemTemplateId == SeekingSingleItemId
                    && inventory.CountMainItem(SeekingSingleItemId) == 0
                    && LoadTrigger(connStr, SeekingSingleItemQuestId) == 1
                    && sender.CountCalls("ACK:0021") == 1,
                ref failures);

            var restoredForUse = InventoryRewardGrantService.TryCreateAndInsert(
                inventory,
                SeekingSingleItemId,
                ItemCreateReason.QuestReward,
                1,
                out var useGrant);
            await manager.SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                inventoryLease,
                new InventoryMutationResult
                {
                    ItemTemplateId = SeekingSingleItemId,
                });
            InventoryMutationResult useMutation = null;
            var used = restoredForUse
                && useGrant != null
                && InventoryDeleteService.TryUseStackableForClient(
                    inventory,
                    useGrant.ListType,
                    useGrant.SlotIndex,
                    SeekingSingleItemId,
                    out useMutation);
            manager.RecalibrateItemSeekingQuestProgressAfterInventoryMutationWithoutNotification(
                inventoryLease,
                useMutation);
            Check("using the final physical seeking item reopens the completed quest",
                used
                    && useMutation != null
                    && useMutation.ItemTemplateId == SeekingSingleItemId
                    && inventory.CountMainItem(SeekingSingleItemId) == 0
                    && LoadTrigger(connStr, SeekingSingleItemQuestId) == 1
                    && sender.CountCalls("ACK:0021") == 2,
                ref failures);

            var restoredForStaleLease = InventoryRewardGrantService.TryCreateAndInsert(
                inventory,
                SeekingSingleItemId,
                ItemCreateReason.QuestReward,
                1,
                out _);
            var staleLease = new InventoryLease(
                Guid.NewGuid(),
                CharacterId,
                inventory,
                inventoryLease.Version + 1);
            var staleMatched = manager
                .RecalibrateItemSeekingQuestProgressAfterInventoryMutationWithoutNotification(
                    staleLease,
                    new InventoryMutationResult
                    {
                        ItemTemplateId = SeekingSingleItemId,
                    });
            Check("stale lease cannot recalibrate a physical seeking item",
                restoredForStaleLease
                    && !staleMatched
                    && LoadTrigger(connStr, SeekingSingleItemQuestId) == 1
                    && sender.CountCalls("ACK:0021") == 2
                    && sender.CountCalls("NOTI:023F") == 0,
                ref failures);
            return failures;
        }

        private static async Task<int> CheckVirtualGoldSeekingProjectionAsync(
            string connStr,
            QuestService questService,
            InventoryLease inventoryLease,
            InventoryService inventory)
        {
            const int initialGold = 192030;
            const int transferGold = 1000000;
            var failures = 0;
            var seekingItems = GameWorld.QuestData.GetSeekingConsumeItems(
                SeekingGoldQuestId);
            Check("2266 preserves its virtual gold seeking requirement",
                seekingItems.Count == 1
                    && seekingItems[0].ItemId == 0
                    && seekingItems[0].Count == transferGold,
                ref failures);

            inventory.SetMainVirtualCount(0, initialGold);
            SaveActiveQuest(connStr, SeekingGoldQuestId, 511);
            var insufficientRefresh = questService.HandleSetTrigger(
                CharacterId,
                BuildSetTriggerBody(SeekingGoldQuestId));
            Check("server recompute keeps an insufficient gold quest incomplete",
                insufficientRefresh.Success
                    && insufficientRefresh.PreviousTriggerValue == 511
                    && insufficientRefresh.TriggerValue == 511
                    && LoadTrigger(connStr, SeekingGoldQuestId) == 511,
                ref failures);

            inventory.SetMainVirtualCount(0, initialGold + transferGold);
            var authoritativeRefreshBody = BuildSetTriggerBody(SeekingGoldQuestId);
            authoritativeRefreshBody[3] = 1;
            var sufficientRefresh = questService.HandleSetTrigger(
                CharacterId,
                authoritativeRefreshBody);
            Check("client SET_TRIGGER recomputes gold from the owned lease",
                sufficientRefresh.Success
                    && sufficientRefresh.PreviousTriggerValue == 511
                    && sufficientRefresh.TriggerValue == 0
                    && LoadTrigger(connStr, SeekingGoldQuestId) == 0,
                ref failures);

            inventory.SetMainVirtualCount(0, initialGold);
            inventory.AccountCargo.Money = transferGold;
            SaveActiveQuest(connStr, SeekingGoldQuestId, 511);
            var sender = new RecordingSender();
            var manager = new QuestManager(sender, connStr);
            var withdrew = InventoryCargoRuntimeService.TryWithdrawCargoGold(
                inventory,
                transferGold,
                out var withdrawnCharacterGold,
                out var withdrawnCargoGold,
                out var withdrawMutation);
            await manager.SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                inventoryLease,
                withdrawMutation);
            Check("cargo withdrawal completes the gold seeking quest",
                withdrew
                    && withdrawMutation?.MainVirtualCountChanged == true
                    && withdrawnCharacterGold == initialGold + transferGold
                    && withdrawnCargoGold == 0
                    && LoadTrigger(connStr, SeekingGoldQuestId) == 0
                    && sender.CountCalls("ACK:0021") == 1
                    && sender.CountCalls("NOTI:023F") == 0,
                ref failures);

            await manager.SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                inventoryLease,
                withdrawMutation);
            Check("replayed gold mutation does not rebuild the quest list",
                LoadTrigger(connStr, SeekingGoldQuestId) == 0
                    && sender.CountCalls("ACK:0021") == 1,
                ref failures);

            var deposited = InventoryCargoRuntimeService.TryDepositCargoGold(
                inventory,
                transferGold,
                out var depositedCharacterGold,
                out var depositedCargoGold,
                out var depositMutation);
            await manager.SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                inventoryLease,
                depositMutation);
            Check("cargo deposit reopens the gold seeking quest",
                deposited
                    && depositMutation?.MainVirtualCountChanged == true
                    && depositedCharacterGold == initialGold
                    && depositedCargoGold == transferGold
                    && LoadTrigger(connStr, SeekingGoldQuestId) == 511
                    && sender.CountCalls("ACK:0021") == 2,
                ref failures);

            var rejected = InventoryCargoRuntimeService.TryWithdrawCargoGold(
                inventory,
                transferGold + 1,
                out _,
                out _,
                out var rejectedMutation);
            await manager.SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                inventoryLease,
                rejectedMutation);
            Check("failed cargo transfer has no quest projection",
                !rejected
                    && rejectedMutation == null
                    && LoadTrigger(connStr, SeekingGoldQuestId) == 511
                    && sender.CountCalls("ACK:0021") == 2,
                ref failures);

            inventory.SetMainVirtualCount(0, initialGold + transferGold);
            SaveActiveQuest(connStr, SeekingGoldQuestId, 0);
            var beforeFinishGold = inventory.CountMainItem(0);
            var finish = QuestSelfTestCommandAdapter.HandleFinish(
                questService,
                CharacterId,
                QuestSelfTestCommandAdapter.BuildFinishBody(SeekingGoldQuestId));
            var afterFinishGold = inventory.CountMainItem(0);
            Check("finishing 2266 atomically consumes one million gold",
                finish.Success
                    && finish.ConsumedEntries.Exists(entry =>
                        entry.SlotIndex == 0
                        && entry.ConsumedCount == transferGold)
                    && afterFinishGold
                        == beforeFinishGold - transferGold + finish.Gold,
                ref failures);

            var beforeReplayGold = inventory.CountMainItem(0);
            var repeatedFinish = QuestSelfTestCommandAdapter.HandleFinish(
                questService,
                CharacterId,
                QuestSelfTestCommandAdapter.BuildFinishBody(SeekingGoldQuestId));
            Check("repeated 2266 completion cannot consume gold twice",
                !repeatedFinish.Success
                    && inventory.CountMainItem(0) == beforeReplayGold,
                ref failures);
            return failures;
        }

        private static async Task<int> CheckMailboxAndDisjointSeekingProjectionAsync(
            string connStr,
            string dbPath,
            InventoryLease inventoryLease,
            InventoryService inventory)
        {
            var failures = 0;
            var held = inventory.CountMainItem(SeekingSingleItemId);
            if (held > 0)
                inventory.TryConsumeMainItem(SeekingSingleItemId, held, out _);

            SaveActiveQuest(connStr, SeekingSingleItemQuestId, 1);
            var mailbox = new MailboxRepository(dbPath, ServerPaths.SchemaFilePath);
            var sent = mailbox.SendSystemMail(new MailboxSendRequest
            {
                SenderCharacterId = CharacterId,
                SenderAccountId = AccountId,
                SenderName = "DNFadmin",
                ReceiverCharacterId = CharacterId,
                ReceiverAccountId = AccountId,
                ReceiverName = "quest-trigger-count-test",
                Text = "seeking-mail-fixture",
                MailType = 1,
                IdempotencyKey = "selftest:seeking-mail-fixture",
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = 0,
                        ItemId = SeekingSingleItemId,
                        ItemCount = 1,
                    },
                },
            });
            var claim = sent.Success
                ? mailbox.ClaimMail(CharacterId, sent.MessageId, inventoryLease)
                : MailboxClaimResult.Fail(MailboxSendError.InvalidRequest);
            var mailSender = new RecordingSender();
            var mailManager = new QuestManager(mailSender, connStr);
            if (claim.Success)
            {
                await mailManager.SyncItemSeekingQuestProgressAfterInventoryMutationsAsync(
                    inventoryLease,
                    claim.InventoryMutations);
            }
            Check("mail attachment mutation projects typed completion trigger",
                sent.Success
                    && claim.Success
                    && claim.InventoryMutations.Count == 1
                    && claim.InventoryMutations[0].ItemTemplateId == SeekingSingleItemId
                    && LoadTrigger(connStr, SeekingSingleItemQuestId) == 0
                    && mailSender.CountCalls("ACK:0021") == 1
                    && mailSender.CountCalls("NOTI:023F") == 0,
                ref failures);

            held = inventory.CountMainItem(SeekingSingleItemId);
            if (held > 0)
                inventory.TryConsumeMainItem(SeekingSingleItemId, held, out _);
            SaveActiveQuest(connStr, SeekingSingleItemQuestId, 1);
            const short sourceSlot = 37;
            const int sourceEquipmentId = 33000;
            inventory.SetItem(InventoryListType.Main, sourceSlot, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = sourceEquipmentId,
                Uid = 284037,
            });
            var disjointed = InventoryDisjointService.TryDisjointItem(
                inventory,
                new DisjointItemRequest
                {
                    ItemSpace = InventoryListType.Main,
                    TargetSlotIndex = sourceSlot,
                    DisjointItemSlotIndex = -1,
                },
                (ItemCore _,
                    ItemMetadata __,
                    out List<DisjointMaterialResult> materials,
                    out byte errorCode) =>
                {
                    materials = new List<DisjointMaterialResult>
                    {
                        new DisjointMaterialResult
                        {
                            ItemTemplateId = SeekingSingleItemId,
                            Count = 1,
                            SlotIndex = -1,
                        },
                    };
                    errorCode = 0;
                    return true;
                },
                out var disjointResult);
            var disjointSender = new RecordingSender();
            var disjointManager = new QuestManager(disjointSender, connStr);
            if (disjointed)
            {
                await disjointManager.SyncItemSeekingQuestProgressAfterInventoryMutationsAsync(
                    inventoryLease,
                    disjointResult.InventoryMutations);
            }
            Check("disjoint fixture completes its inventory operation",
                disjointed,
                ref failures);
            Check("disjoint mutation records the consumed source",
                disjointed
                    && disjointResult.InventoryMutations.Exists(mutation =>
                        mutation.ItemTemplateId == sourceEquipmentId
                        && mutation.AppliedCount == 1),
                ref failures);
            Check("disjoint mutation records the granted material",
                disjointed
                    && disjointResult.InventoryMutations.Exists(mutation =>
                        mutation.ItemTemplateId == SeekingSingleItemId
                        && mutation.AppliedCount == 1),
                ref failures);
            Check("disjoint inventory contains the granted material",
                disjointed
                    && inventory.CountMainItem(SeekingSingleItemId) >= 1,
                ref failures);
            Check("disjoint material mutation completes seeking trigger",
                disjointed
                    && LoadTrigger(connStr, SeekingSingleItemQuestId) == 0,
                ref failures);
            Check("disjoint material mutation projects typed completion trigger",
                disjointed
                    && disjointSender.CountCalls("ACK:0021") == 1
                    && disjointSender.CountCalls("NOTI:023F") == 0,
                ref failures);

            held = inventory.CountMainItem(SeekingSingleItemId);
            if (held > 0)
                inventory.TryConsumeMainItem(SeekingSingleItemId, held, out _);
            return failures;
        }

        private static bool TrySetHeldItemCount(
            InventoryService inventory,
            int itemId,
            int count)
        {
            if (inventory == null || itemId < 0 || count <= 0)
                return false;

            if (InventoryService.TryResolveMainVirtualSlotByItemId(
                    itemId,
                    out var slotIndex,
                    out _))
            {
                return inventory.SetMainVirtualCount(slotIndex, count);
            }

            return InventoryRewardGrantService.TryCreateAndInsert(
                inventory,
                itemId,
                ItemCreateReason.QuestReward,
                count,
                out var result)
                && result != null
                && result.Success;
        }

        private static byte[] BuildQuestBody(ushort questId)
        {
            var body = new byte[2];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            return body;
        }

        private static byte[] BuildSetTriggerBody(ushort questId)
        {
            return new[]
            {
                (byte)(questId & 0xFF),
                (byte)(questId >> 8),
                (byte)0,
                (byte)0,
            };
        }

        private static byte[] BuildWireSetTriggerBody(
            ushort questId,
            byte triggerType)
        {
            return new[]
            {
                (byte)0x21,
                (byte)0x00,
                (byte)(questId & 0xFF),
                (byte)(questId >> 8),
                triggerType,
                (byte)0x00,
            };
        }

        private static void SaveActiveQuest(
            string connStr,
            ushort questId,
            uint triggerValue)
        {
            QuestService.SaveActiveQuests(
                connStr,
                CharacterId,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = questId,
                        TriggerValue = triggerValue,
                    },
                });
        }

        private static ActiveQuest LoadActiveQuest(
            string connStr,
            ushort questId)
        {
            return QuestService.FindByQuestId(
                QuestService.LoadActiveQuests(connStr, CharacterId),
                questId);
        }

        private static int CountProgressEvents(
            string connStr,
            Guid eventId)
        {
            using (var connection = new SqliteConnection(connStr))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM quest_progress_event_inbox
WHERE character_id=@cid AND event_id=@eventId;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@eventId",
                        eventId.ToString("N"));
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static bool IsSuccessAck(QuestAcceptResult result)
        {
            return result != null && result.Success;
        }

        private static bool TryReadAcceptTrigger(QuestAcceptResult result, out uint trigger)
        {
            trigger = result != null ? result.InitTrigger : 0;
            return result != null && result.Success;
        }

        private static void SeedAccount(string dbPath)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@mid", "quest-trigger-count-test");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void MarkQuestCleared(string connStr, int questId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR REPLACE INTO character_quest_completions (character_id, quest_id, completion_value)
VALUES (@cid, @qid, 1);";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.Parameters.AddWithValue("@qid", questId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static uint LoadTrigger(string connStr, ushort questId)
        {
            var active = QuestService.LoadActiveQuests(connStr, CharacterId);
            var quest = QuestService.FindByQuestId(active, questId);
            return quest?.TriggerValue ?? uint.MaxValue;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class RecordingSender : ISessionPacketSender
        {
            private readonly object _sync = new object();
            private readonly List<string> _calls = new List<string>();

            public PlayerContext Player { get; } = new PlayerContext
            {
                CharacterId = QuestTriggerCountSelfTest.CharacterId,
                Level = 50,
            };

            public int CharacterId => QuestTriggerCountSelfTest.CharacterId;
            public int AccountId => QuestTriggerCountSelfTest.AccountId;

            public Task SendPacketAsync(byte[] rawPacket) => Task.CompletedTask;

            public Task SendNotiAsync(ushort notiType, byte[] body)
            {
                lock (_sync)
                    _calls.Add($"NOTI:{notiType:X4}");
                return Task.CompletedTask;
            }

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                lock (_sync)
                    _calls.Add($"ACK:{cmdType:X4}");
                return Task.CompletedTask;
            }

            internal int CountCalls(string expected)
            {
                lock (_sync)
                {
                    var count = 0;
                    foreach (var call in _calls)
                    {
                        if (string.Equals(call, expected, StringComparison.Ordinal))
                            count++;
                    }
                    return count;
                }
            }
        }
    }
}
