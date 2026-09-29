using System;
using System.IO;
using System.Reflection;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class AntonRaidPhaseQuestSelfTest
    {
        private const ushort QuestId = 12830;

        public class UnusedCharacterRepository : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
                => throw new InvalidOperationException(
                    $"Unexpected repository call: {method.Name}");
        }

        public static int Run()
        {
            Console.WriteLine("=== ANTON_RAID_PHASE_QUEST selftest ===");
            var failures = 0;
            Check(
                "Anton achievement starts with five clears in each phase",
                QuestData.GetInitTrigger(QuestId) == (5u | (5u << 9)),
                ref failures);
            Check(
                "raid phase quest trigger is server owned",
                QuestClientTriggerAuthority.Resolve(QuestId, 0x10)
                    == QuestClientTriggerDisposition.EchoOnly,
                ref failures);
            Check(
                "malformed raid phase targets fail closed",
                !QuestData.TryParseRaidPhaseClearTargets(
                    new QuestFile
                    {
                        Type = "[raid phase clear]",
                        IntData = "0 5 -1 1 bad 5 -1",
                    },
                    out _)
                && !QuestData.TryParseRaidPhaseClearTargets(
                    new QuestFile
                    {
                        Type = "[raid phase clear]",
                        IntData = "0 5 -1 0 5 -1",
                    },
                    out _),
                ref failures);
            VerifyPersistentProgress(ref failures);
            Console.WriteLine(failures == 0
                ? "ANTON_RAID_PHASE_QUEST selftest passed"
                : $"ANTON_RAID_PHASE_QUEST selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool passed, ref int failures)
        {
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {name}");
            if (!passed)
                failures++;
        }

        private static void VerifyPersistentProgress(ref int failures)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"anton-raid-phase-quest-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={path}";
            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
CREATE TABLE character_active_quests (
    character_id INTEGER NOT NULL, slot INTEGER NOT NULL,
    quest_id INTEGER NOT NULL, trigger_value INTEGER NOT NULL,
    version INTEGER NOT NULL DEFAULT 0, activation_id TEXT NOT NULL,
    PRIMARY KEY (character_id, slot), UNIQUE (character_id, quest_id));
CREATE TABLE quest_progress_event_inbox (
    character_id INTEGER NOT NULL, activation_id TEXT NOT NULL,
    event_id TEXT NOT NULL, event_kind TEXT NOT NULL,
    PRIMARY KEY (character_id, activation_id, event_id, event_kind));";
                        command.ExecuteNonQuery();
                    }
                    QuestRepository.InsertActiveQuest(
                        connection, null, 1, 0, QuestId, 5u | (5u << 9));
                    QuestRepository.InsertActiveQuest(
                        connection, null, 1, 1, 12829, 1);
                    QuestRepository.InsertActiveQuest(
                        connection, null, 2, 0, QuestId, 1);
                    QuestRepository.InsertActiveQuest(
                        connection, null, 4, 0, QuestId,
                        5u | (5u << 9));
                    var completedActivation = QuestRepository.InsertActiveQuest(
                        connection, null, 3, 0, QuestId, 1);
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO quest_progress_event_inbox
    (character_id, activation_id, event_id, event_kind)
VALUES (3, @activation, @event, 'raid-phase-clear');";
                        command.Parameters.AddWithValue(
                            "@activation", completedActivation.ToStorageString());
                        command.Parameters.AddWithValue("@event", Guid.NewGuid().ToString("N"));
                        command.ExecuteNonQuery();
                    }
                }

                var service = new QuestService(connectionString);
                var phaseOneEvent = Guid.NewGuid();
                var first = service.SyncRaidPhaseClearQuestProgress(
                    1, 0, phaseOneEvent);
                Check(
                    "phase one decrements only first objective",
                    first.Count == 1 && ReadTrigger(connectionString, 1)
                        == (4u | (5u << 9)),
                    ref failures);
                Check(
                    "phase one replay is idempotent",
                    service.SyncRaidPhaseClearQuestProgress(
                        1, 0, phaseOneEvent).Count == 0
                    && ReadTrigger(connectionString, 1) == (4u | (5u << 9)),
                    ref failures);
                Check(
                    "phase two decrements only second objective",
                    service.SyncRaidPhaseClearQuestProgress(
                        1, 1, Guid.NewGuid()).Count == 1
                    && ReadTrigger(connectionString, 1) == (4u | (4u << 9)),
                    ref failures);
                Check(
                    "legacy trigger is repaired before first phase event",
                    service.SyncRaidPhaseClearQuestProgress(
                        2, 1, Guid.NewGuid()).Count == 1
                    && ReadTrigger(connectionString, 2) == (5u | (4u << 9)),
                    ref failures);
                Check(
                    "legitimate remaining one is not reset",
                    service.SyncRaidPhaseClearQuestProgress(
                        3, 0, Guid.NewGuid()).Count == 1
                    && ReadTrigger(connectionString, 3) == 0,
                    ref failures);
                Check(
                    "invalid phase and inactive character do not change progress",
                    service.SyncRaidPhaseClearQuestProgress(
                        1, 2, Guid.NewGuid()).Count == 0
                    && service.SyncRaidPhaseClearQuestProgress(
                        5, 0, Guid.NewGuid()).Count == 0
                    && ReadTrigger(connectionString, 1) == (4u | (4u << 9)),
                    ref failures);

                var handler = new RaidHandler(
                    DispatchProxy.Create<ICharacterRepository,
                        UnusedCharacterRepository>(),
                    new SessionDirectory(),
                    new RaidManager(),
                    service);
                var instanceId = Guid.NewGuid();
                var memberOne = new RaidMember
                {
                    UserId = 1,
                    CharacterId = 1,
                    SessionId = Guid.NewGuid(),
                };
                var memberTwo = new RaidMember
                {
                    UserId = 2,
                    CharacterId = 2,
                    SessionId = Guid.NewGuid(),
                };
                var phaseOne = new RaidSnapshot
                {
                    InstanceId = instanceId,
                    RaidId = 99,
                    PhaseIndex = 0,
                    State = 5,
                    StateArgument = 0,
                    Members = new[] { memberOne, memberTwo },
                };
                Check(
                    "committed raid phase credits eligible member only",
                    handler.SyncRaidPhaseQuestProgressAsync(
                        phaseOne, new ushort[] { 1 }).GetAwaiter().GetResult() == 1
                    && ReadTrigger(connectionString, 1) == (3u | (4u << 9))
                    && ReadTrigger(connectionString, 2) == (5u | (4u << 9)),
                    ref failures);
                Check(
                    "repeated phase callback keeps quest progress unchanged",
                    handler.SyncRaidPhaseQuestProgressAsync(
                        phaseOne, new ushort[] { 1 }).GetAwaiter().GetResult() == 0
                    && ReadTrigger(connectionString, 1) == (3u | (4u << 9)),
                    ref failures);
                var failedPhaseTwo = new RaidSnapshot
                {
                    InstanceId = instanceId,
                    RaidId = 99,
                    PhaseIndex = 1,
                    State = 4,
                    StateArgument = 1,
                    Members = new[] { memberOne, memberTwo },
                };
                Check(
                    "failed raid phase grants no quest progress",
                    handler.SyncRaidPhaseQuestProgressAsync(
                        failedPhaseTwo, new ushort[] { 1, 2 })
                        .GetAwaiter().GetResult() == 0
                    && ReadTrigger(connectionString, 1) == (3u | (4u << 9)),
                    ref failures);
                var successfulPhaseTwo = new RaidSnapshot
                {
                    InstanceId = instanceId,
                    RaidId = 99,
                    PhaseIndex = 1,
                    State = 4,
                    StateArgument = 0,
                    Members = new[] { memberOne, memberTwo },
                };
                Check(
                    "raid instance and phase form distinct completion events",
                    RaidHandler.BuildRaidPhaseQuestEventId(instanceId, 0)
                        != RaidHandler.BuildRaidPhaseQuestEventId(instanceId, 1)
                    && handler.SyncRaidPhaseQuestProgressAsync(
                        successfulPhaseTwo, new ushort[] { 1 })
                        .GetAwaiter().GetResult() == 1
                    && ReadTrigger(connectionString, 1) == (3u | (3u << 9)),
                    ref failures);

                for (var i = 0; i < 5; i++)
                    service.SyncRaidPhaseClearQuestProgress(4, 0, Guid.NewGuid());
                Check(
                    "five phase-one clears finish only phase-one objective",
                    ReadTrigger(connectionString, 4) == (5u << 9),
                    ref failures);
                for (var i = 0; i < 5; i++)
                    service.SyncRaidPhaseClearQuestProgress(4, 1, Guid.NewGuid());
                Check(
                    "five phase-two clears finish the quest without overflow",
                    ReadTrigger(connectionString, 4) == 0
                    && service.SyncRaidPhaseClearQuestProgress(
                        4, 1, Guid.NewGuid()).Count == 0,
                    ref failures);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static uint ReadTrigger(string connectionString, int characterId)
        {
            var quests = new QuestRepository(connectionString)
                .LoadActiveQuests(characterId);
            return quests.Count > 0 ? quests[0].TriggerValue : 0;
        }
    }
}
