using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class QuestDungeonDropSelfTest
    {
        private const ushort NightmareDimensionQuestId = 2071;
        private const int NightmareSourceDungeonId = 171;
        private const int NightmareRatMonsterCode = 65636;
        private const int NightmareRatTailItemId = 10099749;
        private const ushort BlackChurchIntrusionQuestId = 2415;
        private const int BlackChurchDungeonId = 73;
        private const int BlackChurchAiCharacterCode = 21604;
        private const int BlackChurchQuestItemId = 4755;
        private const ushort ThievesCityQuestId = 2066;
        private const int ThievesCityQuestItemId = 10089306;
        private const ushort MasaccioCaptureQuestId = 2257;
        private const int MasaccioCaptureMonsterCode = 56010;
        private const int MasaccioCaptureItemId = 3315;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_DUNGEON_DROP selftest ===");
            var failures = 0;

            VerifyConfiguredDrops(ref failures);
            VerifyMonsterCaptureConfiguration(ref failures);
            VerifyItemMetadataWarmupAndCache(ref failures);
            VerifyUnifiedItemAcquisition(ref failures);
            VerifyNotificationBatcher(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyMonsterCaptureConfiguration(ref int failures)
        {
            var synthetic = MonsterFile.Parse(
                "[catch item]\n"
                + "3315 1 100\n"
                + "[/catch item]\n");
            Check(
                "MOB parser reads catch item as item/count/rate",
                synthetic.CatchItems.Count == 1
                    && synthetic.CatchItems[0].ItemId == MasaccioCaptureItemId
                    && synthetic.CatchItems[0].Count == 1
                    && synthetic.CatchItems[0].DropRate == 100,
                ref failures);

            var requestBody = new byte[28];
            requestBody[20] = 0;
            requestBody[26] = 1;
            var captureRequest = DieMonsterRequest.Parse(requestBody);
            Check(
                "DIE_MONSTER reads capture flag after zero attack records",
                captureRequest.IsCapture && !captureRequest.IsPassiveObject,
                ref failures);

            requestBody = new byte[48];
            requestBody[20] = 2;
            requestBody[46] = 1;
            requestBody[47] = 1;
            captureRequest = DieMonsterRequest.Parse(requestBody);
            Check(
                "DIE_MONSTER keeps capture and passive flags after variable attack records",
                captureRequest.IsCapture && captureRequest.IsPassiveObject,
                ref failures);

            var definition = MonsterCaptureDefinitionCatalog.GetItems(
                MasaccioCaptureMonsterCode);
            Check(
                "real Masaccio MOB exposes its capture definition",
                definition.Count == 1
                    && definition[0].ItemId == MasaccioCaptureItemId
                    && definition[0].Count == 1
                    && definition[0].DropRate == 100,
                ref failures);

            var seeking = QuestData.GetSeekingConsumeItems(
                MasaccioCaptureQuestId);
            var hasRecordCapsule = false;
            foreach (var item in seeking)
            {
                if (item.ItemId == MasaccioCaptureItemId
                    && item.Count == 1)
                {
                    hasRecordCapsule = true;
                    break;
                }
            }
            Check(
                "real quest 2257 seeks the captured record capsule",
                hasRecordCapsule,
                ref failures);

            var candidates = QuestDropProvider.CheckMonsterCaptureDrop(
                new[] { (int)MasaccioCaptureQuestId },
                definition);
            Check(
                "real quest 2257 matches only the configured capture item",
                candidates != null
                    && candidates.Count == 1
                    && candidates[0].QuestId == MasaccioCaptureQuestId
                    && candidates[0].ItemId == MasaccioCaptureItemId
                    && candidates[0].Count == 1
                    && candidates[0].DropRate == 100
                    && candidates[0].SeekingRequiredCount == 1
                    && candidates[0].PreferQuestInventory,
                ref failures);

            Check(
                "capture definition without an active quest is a no-op",
                QuestDropProvider.CheckMonsterCaptureDrop(
                    Array.Empty<int>(),
                    definition) == null,
                ref failures);
        }

        private static void VerifyItemMetadataWarmupAndCache(ref int failures)
        {
            ItemMetadataResolver.Warmup();
            Check(
                "item metadata warmup loads both immutable item lists",
                ItemMetadataResolver.AreItemListsWarmed,
                ref failures);

            var firstMetadata = ItemMetadataResolver.Resolve(
                NightmareRatTailItemId);
            var secondMetadata = ItemMetadataResolver.Resolve(
                NightmareRatTailItemId);
            var firstFileLoaded = ItemMetadataResolver.TryLoadStackableFile(
                NightmareRatTailItemId,
                out var firstFile);
            var secondFileLoaded = ItemMetadataResolver.TryLoadStackableFile(
                NightmareRatTailItemId,
                out var secondFile);
            Check(
                "quest-item metadata and parsed template are reused process-wide",
                ReferenceEquals(firstMetadata, secondMetadata)
                && firstFileLoaded
                && secondFileLoaded
                && ReferenceEquals(firstFile, secondFile),
                ref failures);
        }

        private static void VerifyConfiguredDrops(ref int failures)
        {
            var nightmareDimensionQuest =
                QuestData.GetQuestFile(NightmareDimensionQuestId);
            Check(
                "normal monster enemy reward parses with type one",
                nightmareDimensionQuest != null
                    && nightmareDimensionQuest.EnemyRewardItems.Exists(entry =>
                        entry.EnemyCode == NightmareRatMonsterCode
                        && entry.EnemyType == QuestDropProvider.EnemyTypeMonster
                        && entry.DungeonId == NightmareSourceDungeonId
                        && entry.ItemId == NightmareRatTailItemId
                        && entry.Count == 1
                        && entry.DropRate == 60
                        && entry.MaxStack == 10),
                ref failures);

            var nightmareRatCandidates = QuestDropProvider.CheckMonsterDrop(
                new[] { (int)NightmareDimensionQuestId },
                NightmareSourceDungeonId,
                0,
                NightmareRatMonsterCode);
            Check(
                "normal monster death matches enemy reward type one",
                nightmareRatCandidates != null
                    && nightmareRatCandidates.Count == 1
                    && nightmareRatCandidates[0].QuestId
                        == NightmareDimensionQuestId
                    && nightmareRatCandidates[0].ItemId
                        == NightmareRatTailItemId
                    && nightmareRatCandidates[0].PreferQuestInventory,
                ref failures);

            Check(
                "quest drop is clipped to the remaining requirement",
                QuestDropProvider.RollDrop(
                    new QuestDropCandidate
                    {
                        Count = 3,
                        DropRate = 100,
                        MaxStack = 10,
                    },
                    currentHeld: 9) == 1,
                ref failures);

            var thievesCityQuest = QuestData.GetQuestFile(ThievesCityQuestId);
            var thievesCityEntry = thievesCityQuest?.MonsterRewardItems.Find(
                entry => entry.ItemId == ThievesCityQuestItemId);
            var thievesCityCandidates = thievesCityEntry != null
                ? QuestDropProvider.CheckMonsterDrop(
                    new[] { (int)ThievesCityQuestId },
                    thievesCityEntry.DungeonId > 0
                        ? thievesCityEntry.DungeonId
                        : 0,
                    thievesCityEntry.Difficulty >= 0
                        ? thievesCityEntry.Difficulty
                        : 0,
                    thievesCityEntry.MonsterCode)
                : null;
            var thievesCityCandidate = default(QuestDropCandidate);
            var hasThievesCityCandidate = false;
            if (thievesCityCandidates != null)
            {
                foreach (var candidate in thievesCityCandidates)
                {
                    if (candidate.ItemId != ThievesCityQuestItemId)
                        continue;
                    thievesCityCandidate = candidate;
                    hasThievesCityCandidate = true;
                    break;
                }
            }
            Check(
                "Thieves' City uses seeking count 20 over monster maxStack 50",
                thievesCityEntry != null
                    && thievesCityEntry.MaxStack == 50
                    && hasThievesCityCandidate
                    && thievesCityCandidate.SeekingRequiredCount == 20
                    && QuestDropProvider.GetEffectiveHeldLimit(
                        thievesCityCandidate) == 20,
                ref failures);
            Check(
                "application clamp rejects an injected roller above seeking count",
                hasThievesCityCandidate
                    && QuestDropService.ClampDropCount(
                        thievesCityCandidate,
                        currentHeld: 0,
                        requestedCount: 24) == 20
                    && QuestDropService.ClampDropCount(
                        thievesCityCandidate,
                        currentHeld: 19,
                        requestedCount: 24) == 1
                    && QuestDropService.ClampDropCount(
                        thievesCityCandidate,
                        currentHeld: 20,
                        requestedCount: 24) == 0,
                ref failures);

            var blackChurchQuest =
                QuestData.GetQuestFile(BlackChurchIntrusionQuestId);
            Check(
                "Black Church intrusion quest parses hostile APC reward",
                blackChurchQuest != null
                    && blackChurchQuest.EnemyRewardItems.Exists(entry =>
                        entry.EnemyCode == BlackChurchAiCharacterCode
                        && entry.EnemyType
                            == QuestDropProvider.EnemyTypeAiCharacter
                        && entry.DungeonId == BlackChurchDungeonId
                        && entry.Difficulty == -1
                        && entry.ItemId == BlackChurchQuestItemId
                        && entry.Count == 1
                        && entry.DropRate == 100
                        && entry.MaxStack == 15),
                ref failures);

            var blackChurchAiCandidates = QuestDropProvider.CheckEnemyDrop(
                new[] { (int)BlackChurchIntrusionQuestId },
                BlackChurchDungeonId,
                0,
                BlackChurchAiCharacterCode,
                QuestDropProvider.EnemyTypeAiCharacter);
            Check(
                "normal dungeon hostile APC matches AI-character quest drop",
                blackChurchAiCandidates != null
                    && blackChurchAiCandidates.Count == 1
                    && blackChurchAiCandidates[0].QuestId
                        == BlackChurchIntrusionQuestId
                    && blackChurchAiCandidates[0].ItemId
                        == BlackChurchQuestItemId
                    && blackChurchAiCandidates[0].Count == 1
                    && blackChurchAiCandidates[0].DropRate == 100
                    && blackChurchAiCandidates[0].MaxStack == 15
                    && blackChurchAiCandidates[0].PreferQuestInventory,
                ref failures);
            Check(
                "hostile APC reward does not match normal monster path",
                QuestDropProvider.CheckMonsterDrop(
                    new[] { (int)BlackChurchIntrusionQuestId },
                    BlackChurchDungeonId,
                    0,
                    BlackChurchAiCharacterCode) == null,
                ref failures);
            Check(
                "actor types five through eight use AI-character quest drops",
                DungeonCombatHandler.IsAiCharacterActorType(5)
                    && DungeonCombatHandler.IsAiCharacterActorType(6)
                    && DungeonCombatHandler.IsAiCharacterActorType(7)
                    && DungeonCombatHandler.IsAiCharacterActorType(8)
                    && !DungeonCombatHandler.IsAiCharacterActorType(4)
                    && !DungeonCombatHandler.IsAiCharacterActorType(9),
                ref failures);
        }

        private static void VerifyUnifiedItemAcquisition(ref int failures)
        {
            var inventory = new InventoryService(135002, 135002);
            var acquisition = new DungeonItemAcquisitionService(
                new DropService());
            var firstActivation = QuestActivationId.New();
            var secondActivation = QuestActivationId.New();
            var granted = acquisition.TryGrantItems(
                inventory,
                new[]
                {
                    new DungeonItemGrantRequest
                    {
                        QuestId = NightmareDimensionQuestId,
                        QuestActivationId = firstActivation,
                        ItemTemplateId = NightmareRatTailItemId,
                        Count = 1,
                        Source = DungeonItemAcquisitionSource.QuestAutomaticDrop,
                    },
                    new DungeonItemGrantRequest
                    {
                        QuestId = BlackChurchIntrusionQuestId,
                        QuestActivationId = secondActivation,
                        ItemTemplateId = BlackChurchQuestItemId,
                        Count = 1,
                        Source = DungeonItemAcquisitionSource.QuestAutomaticDrop,
                    },
                },
                out var result);
            Check(
                "unified dungeon item acquisition grants a planned quest batch",
                granted
                    && result.Success
                    && result.Entries.Count == 2
                    && result.Changes.HasChanges
                    && inventory.CountMainItem(NightmareRatTailItemId) == 1
                    && inventory.CountMainItem(BlackChurchQuestItemId) == 1,
                ref failures);

            var heldBeforeInvalid = inventory.CountMainItem(NightmareRatTailItemId);
            var rejected = acquisition.TryGrantItems(
                inventory,
                new[]
                {
                    new DungeonItemGrantRequest
                    {
                        QuestId = NightmareDimensionQuestId,
                        QuestActivationId = firstActivation,
                        ItemTemplateId = NightmareRatTailItemId,
                        Count = 1,
                        Source = DungeonItemAcquisitionSource.QuestAutomaticDrop,
                    },
                    new DungeonItemGrantRequest
                    {
                        QuestId = NightmareDimensionQuestId,
                        QuestActivationId = firstActivation,
                        ItemTemplateId = int.MaxValue,
                        Count = 1,
                        Source = DungeonItemAcquisitionSource.QuestAutomaticDrop,
                    },
                },
                out _);
            Check(
                "invalid quest batch is rejected before any dungeon item is inserted",
                !rejected
                    && inventory.CountMainItem(NightmareRatTailItemId)
                        == heldBeforeInvalid,
                ref failures);
        }

        private static void VerifyNotificationBatcher(ref int failures)
        {
            var session = new Network.EnhancedClientSession(
                new System.Net.Sockets.TcpClient(),
                null);
            session.Player.CharacterId = 135001;

            var inventoryRefreshCount = 0;
            var refreshedSlots = new HashSet<short>();
            var batcher = new QuestDropNotificationBatcher(
                (_, slots) =>
                {
                    inventoryRefreshCount++;
                    foreach (var slot in slots)
                        refreshedSlots.Add(slot);
                    return Task.CompletedTask;
                });

            batcher.Queue(session, new short[] { 178 });
            batcher.Queue(session, new short[] { 178, 179 });
            Check(
                "quest-drop refresh is deferred during a kill burst",
                inventoryRefreshCount == 0,
                ref failures);

            var flushed = batcher.FlushPendingAsync(session)
                .GetAwaiter()
                .GetResult();
            Check(
                "quest-drop burst sends one inventory refresh without a full quest list",
                flushed
                    && inventoryRefreshCount == 1,
                ref failures);
            Check(
                "quest-drop burst deduplicates changed inventory slots",
                refreshedSlots.SetEquals(new short[] { 178, 179 }),
                ref failures);
            Check(
                "quest-drop flush consumes the pending batch once",
                !batcher.FlushPendingAsync(session).GetAwaiter().GetResult()
                    && inventoryRefreshCount == 1,
                ref failures);

            session.Close();
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
