using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;

namespace DfoServer.SelfTests
{
    public static class ClearMapQuestSelfTest
    {
        private const int CharacterId = 189001;
        private const ushort DungeonQuestId = 1890;
        private const ushort MapQuestId = 1891;
        private const ushort OtherQuestId = 1892;
        private const ushort QuestConnectedStartMapQuestId = 2100;
        private const int QuestConnectedDungeonId = 155;
        private const int QuestConnectedStartMapId = 35801;
        private const int QuestConnectedBossMapId = 14198;
        private const ushort BlackEarthObjectiveQuestId = 7731;
        private const int BlackEarthObjectiveMapId = 39118;

        public static int Run()
        {
            Console.WriteLine("=== CLEAR_MAP_QUEST selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "clear-map-quest.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            // character_active_quests 已收编进 schema(不再懒建表): 必须走 Initialize 建库并满足 FK
            var connStr = SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash) VALUES (189001, 'clear-map-quest-selftest', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name) VALUES (189001, 189001, 'clear-map-quest-selftest');";
                    cmd.ExecuteNonQuery();
                }
            }

            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = DungeonQuestId, TriggerValue = 1 },
                new ActiveQuest { Slot = 1, QuestId = MapQuestId, TriggerValue = 2 },
                new ActiveQuest { Slot = 2, QuestId = OtherQuestId, TriggerValue = 3 },
            });

            var failures = 0;

            var changed = QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId: 0,
                mapId: 33061,
                matchesClearMapQuest: MatchesClearMapQuest);
            Check("room clear only updates map-target quest", changed == 1, ref failures);
            Check("map quest trigger cleared", LoadTrigger(connStr, MapQuestId) == 0, ref failures);
            Check("dungeon quest unchanged by room-only sync", LoadTrigger(connStr, DungeonQuestId) == 1, ref failures);

            changed = QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId: 33060,
                mapId: 0,
                matchesClearMapQuest: MatchesClearMapQuest);
            Check("dungeon clear updates dungeon-target quest", changed == 1, ref failures);
            Check("dungeon quest trigger cleared", LoadTrigger(connStr, DungeonQuestId) == 0, ref failures);
            Check("unmatched quest unchanged", LoadTrigger(connStr, OtherQuestId) == 3, ref failures);

            Check("issue 189 quest matches start map id",
                GameWorld.QuestData.MatchesClearMapTarget(1981, dungeonId: 0, mapId: 33060),
                ref failures);
            Check("issue 189 quest does not match boss map id",
                !GameWorld.QuestData.MatchesClearMapTarget(1981, dungeonId: 0, mapId: 33061),
                ref failures);
            Check("issue 189 start-map SET_TRIGGER is deferred before dungeon clear",
                QuestManager.ShouldDeferQuestConnectedStartMapSetTrigger(
                    1981,
                    triggerType: 0,
                    isIncrement: false,
                    dungeonCleared: false,
                    mazeQuestConnected: true,
                    mazeStartMapId: 33060),
                ref failures);
            Check("issue 189 start-map SET_TRIGGER is allowed after dungeon clear",
                !QuestManager.ShouldDeferQuestConnectedStartMapSetTrigger(
                    1981,
                    triggerType: 0,
                    isIncrement: false,
                    dungeonCleared: true,
                    mazeQuestConnected: true,
                    mazeStartMapId: 33060),
                ref failures);
            Check("issue 189 start-map increment trigger is not deferred",
                !QuestManager.ShouldDeferQuestConnectedStartMapSetTrigger(
                    1981,
                    triggerType: 0,
                    isIncrement: true,
                    dungeonCleared: false,
                    mazeQuestConnected: true,
                    mazeStartMapId: 33060),
                ref failures);
            Check("auto-complete start-map quest is not deferred",
                !QuestManager.ShouldDeferQuestConnectedStartMapSetTrigger(
                    2541,
                    triggerType: 0,
                    isIncrement: false,
                    dungeonCleared: false,
                    mazeQuestConnected: true,
                    mazeStartMapId: 18017),
                ref failures);

            changed = QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId: 33060,
                mapId: 33061,
                matchesClearMapQuest: MatchesClearMapQuest);
            Check("clear-map sync is idempotent", changed == 0, ref failures);

            Check("quest-connected start-map definition matches real PVF target",
                GameWorld.QuestData.MatchesClearMapTarget(
                    QuestConnectedStartMapQuestId,
                    dungeonId: 0,
                    mapId: QuestConnectedStartMapId),
                ref failures);
            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest
                {
                    Slot = 0,
                    QuestId = QuestConnectedStartMapQuestId,
                    TriggerValue = 1,
                },
            });
            var questConnected = QuestService.FindByQuestId(
                QuestService.LoadActiveQuests(connStr, CharacterId),
                QuestConnectedStartMapQuestId);
            var eligibleActivations = new Dictionary<ushort, QuestActivationId>
            {
                [QuestConnectedStartMapQuestId] = questConnected.ActivationId,
            };
            var clearEventId = Guid.Parse("21000000-0000-0000-0000-000000035801");

            changed = ApplyQuestConnectedClearFact(
                connStr,
                dungeonId: 0,
                mapId: QuestConnectedBossMapId,
                clearEventId,
                eligibleActivations);
            Check("boss-room clear phase does not complete start-map target",
                changed == 0
                && LoadTrigger(connStr, QuestConnectedStartMapQuestId) == 1
                && CountProgressEvents(
                    connStr,
                    questConnected.ActivationId,
                    clearEventId,
                    "clear-map") == 0,
                ref failures);

            changed = ApplyQuestConnectedClearFact(
                connStr,
                dungeonId: QuestConnectedDungeonId,
                mapId: QuestConnectedBossMapId,
                clearEventId,
                eligibleActivations);
            Check("dungeon-clear phase does not complete start-map target",
                changed == 0
                && LoadTrigger(connStr, QuestConnectedStartMapQuestId) == 1
                && CountProgressEvents(
                    connStr,
                    questConnected.ActivationId,
                    clearEventId,
                    "clear-dungeon") == 0,
                ref failures);

            changed = ApplyQuestConnectedClearFact(
                connStr,
                dungeonId: 0,
                mapId: QuestConnectedStartMapId,
                clearEventId,
                eligibleActivations);
            Check("deferred start-map phase completes real clear-map quest",
                changed == 1
                && LoadTrigger(connStr, QuestConnectedStartMapQuestId) == 0
                && CountProgressEvents(
                    connStr,
                    questConnected.ActivationId,
                    clearEventId,
                    "clear-map") == 1,
                ref failures);

            changed = ApplyQuestConnectedClearFact(
                connStr,
                dungeonId: 0,
                mapId: QuestConnectedStartMapId,
                clearEventId,
                eligibleActivations);
            Check("deferred start-map replay remains idempotent",
                changed == 0
                && LoadTrigger(connStr, QuestConnectedStartMapQuestId) == 0
                && CountProgressEvents(
                    connStr,
                    questConnected.ActivationId,
                    clearEventId,
                    "clear-map") == 1,
                ref failures);

            Check("Black Earth objective quest matches its intermediate MAP",
                GameWorld.QuestData.MatchesClearMapTarget(
                    BlackEarthObjectiveQuestId,
                    dungeonId: 0,
                    mapId: BlackEarthObjectiveMapId),
                ref failures);
            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest
                {
                    Slot = 0,
                    QuestId = BlackEarthObjectiveQuestId,
                    TriggerValue = 1,
                },
            });
            changed = QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId: 0,
                mapId: BlackEarthObjectiveMapId,
                matchesClearMapQuest: (questId, dungeonId, mapId) =>
                    GameWorld.QuestData.MatchesClearMapTarget(
                        questId,
                        dungeonId,
                        mapId));
            Check("Black Earth intermediate MAP clear completes the real quest",
                changed == 1
                && LoadTrigger(connStr, BlackEarthObjectiveQuestId) == 0,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool MatchesClearMapQuest(ushort questId, int dungeonId, int mapId)
        {
            if (questId == DungeonQuestId)
                return dungeonId == 33060;
            if (questId == MapQuestId)
                return mapId == 33061;
            return false;
        }

        private static uint LoadTrigger(string connStr, ushort questId)
        {
            var active = QuestService.LoadActiveQuests(connStr, CharacterId);
            var quest = QuestService.FindByQuestId(active, questId);
            return quest != null ? quest.TriggerValue : uint.MaxValue;
        }

        private static int ApplyQuestConnectedClearFact(
            string connStr,
            int dungeonId,
            int mapId,
            Guid eventId,
            IReadOnlyDictionary<ushort, QuestActivationId> eligibleActivations)
        {
            return QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId,
                mapId,
                (questId, targetDungeonId, targetMapId) =>
                    GameWorld.QuestData.MatchesClearMapTarget(
                        questId,
                        targetDungeonId,
                        targetMapId),
                eventId,
                new[] { QuestConnectedStartMapQuestId },
                eligibleActivations);
        }

        private static int CountProgressEvents(
            string connStr,
            QuestActivationId activationId,
            Guid eventId,
            string eventKind)
        {
            using (var connection =
                   new Microsoft.Data.Sqlite.SqliteConnection(connStr))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM quest_progress_event_inbox
WHERE character_id=@cid
  AND activation_id=@activation
  AND event_id=@eventId
  AND event_kind=@eventKind;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@activation",
                        activationId.ToStorageString());
                    command.Parameters.AddWithValue(
                        "@eventId",
                        eventId.ToString("N"));
                    command.Parameters.AddWithValue("@eventKind", eventKind);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
