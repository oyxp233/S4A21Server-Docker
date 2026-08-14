using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class QuestChainAvailabilitySelfTest
    {
        private const ushort FirstQuestId = 101;
        private const ushort SecondQuestId = 1776;
        private const ushort ThirdQuestId = 1777;
        private const int AccountId = 986027;
        private const int CharacterId = 986127;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_CHAIN_AVAILABILITY selftest ===");

            var failures = 0;
            var noneCleared = BuildQuestIds();
            Check("second quest is unavailable before its prerequisite",
                !noneCleared.Contains(SecondQuestId),
                ref failures);

            var firstCleared = BuildQuestIds(FirstQuestId);
            Check("clearing quest 101 exposes quest 1776",
                firstCleared.Contains(SecondQuestId),
                ref failures);
            Check("quest 1777 remains unavailable before quest 1776 is cleared",
                !firstCleared.Contains(ThirdQuestId),
                ref failures);

            var secondCleared = BuildQuestIds(FirstQuestId, SecondQuestId);
            Check("clearing quest 1776 exposes quest 1777",
                secondCleared.Contains(ThirdQuestId),
                ref failures);

            CheckCompletionRefresh(ref failures);
            CheckJobCompletionRefresh(ref failures);
            CheckExpertJobCompletionRefresh(ref failures);
            CheckDanglingPrerequisitePolicy(ref failures);
            CheckTimeGateSameSlotProjection(ref failures);
            CheckJobAndExpertJobSuccessors(ref failures);
            CheckLevelAndPrerequisiteAvailability(
                questId: 2121,
                prerequisiteQuestId: 2120,
                minimumLevel: 55,
                label: "Gent Outskirts",
                ref failures);
            CheckLevelAndPrerequisiteAvailability(
                questId: 2089,
                prerequisiteQuestId: 12683,
                minimumLevel: 53,
                label: "Vilmark",
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckCompletionRefresh(ref int failures)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var databasePath = Path.Combine(tempDir, "quest-chain-availability.db");
            DeleteDatabase(databasePath);

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            SeedCharacter(databasePath);
            QuestService.SaveActiveQuests(
                connectionString,
                CharacterId,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = FirstQuestId,
                        TriggerValue = 0,
                    },
                });

            var sessionId = Guid.NewGuid();
            InventoryContext.Register(
                sessionId,
                new InventoryService(CharacterId, AccountId));
            try
            {
                var sender = new RecordingSender();
                var manager = new QuestManager(sender, connectionString);
                manager.HandleFinishQuestAsync(
                        0x0022,
                        BuildWireFinishBody(FirstQuestId),
                        sessionId)
                    .GetAwaiter()
                    .GetResult();

                Check("quest finish ACK is emitted first",
                    sender.Calls.Count > 0
                    && sender.Calls[0] == "ACK:0022"
                    && sender.LastAckBody != null
                    && sender.LastAckBody.Length > 0
                    && sender.LastAckBody[0] == 1,
                    ref failures);
                Check("quest finish does not rebuild the active list with 0x023F",
                    !sender.Calls.Contains("NOTI:023F"),
                    ref failures);
                Check("quest finish refreshes acceptable quests with 0x0015",
                    sender.Calls.Count > 1
                    && sender.Calls[sender.Calls.Count - 1] == "NOTI:0015"
                    && ParseQuestIds(sender.LastAcceptableQuestBody).Contains(SecondQuestId),
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, CharacterId);
            }
        }

        private static void CheckJobCompletionRefresh(ref int failures)
        {
            const ushort jobQuestId = 7810;
            const ushort successorQuestId = 4427;
            var databasePath = CreateFixtureDatabase(
                "quest-job-chain-completion.db",
                level: 21,
                growType: 0);
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            QuestService.SaveActiveQuests(
                connectionString,
                CharacterId,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = jobQuestId,
                        TriggerValue = 0,
                    },
                });

            var sessionId = Guid.NewGuid();
            InventoryContext.Register(
                sessionId,
                new InventoryService(CharacterId, AccountId));
            try
            {
                var sender = new RecordingSender();
                sender.Player.Level = 21;
                var manager = new QuestManager(sender, connectionString);
                manager.HandleFinishQuestAsync(
                        0x0022,
                        BuildWireFinishBody(jobQuestId),
                        sessionId)
                    .GetAwaiter()
                    .GetResult();

                Check("job completion ACK projects grow type 3",
                    TryReadFinishChain(
                        sender.LastAckBody,
                        out var chainType,
                        out var growNumber)
                    && chainType == 1
                    && growNumber == 3,
                    ref failures);
                Check("job completion updates session grow type before quest refresh",
                    sender.Player.GrowType == 3,
                    ref failures);
                Check("job completion refresh exposes successor 4427",
                    sender.Calls.Count > 0
                    && sender.Calls[sender.Calls.Count - 1] == "NOTI:0015"
                    && ParseQuestIds(sender.LastAcceptableQuestBody)
                        .Contains(successorQuestId),
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, CharacterId);
            }
        }

        private static void CheckExpertJobCompletionRefresh(ref int failures)
        {
            const ushort expertJobQuestId = 2702;
            const ushort successorQuestId = 11007;
            const int requiredItemId = 3037;
            const int requiredItemCount = 100;
            var databasePath = CreateFixtureDatabase(
                "quest-expert-job-chain-completion.db",
                level: 21,
                growType: 2);
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            QuestService.SaveActiveQuests(
                connectionString,
                CharacterId,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = expertJobQuestId,
                        TriggerValue = 0,
                    },
                });

            var sessionId = Guid.NewGuid();
            var inventory = new InventoryService(CharacterId, AccountId);
            InventoryContext.Register(sessionId, inventory);
            try
            {
                var inserted = InventoryRewardGrantService.TryCreateAndInsert(
                    inventory,
                    requiredItemId,
                    ItemCreateReason.QuestReward,
                    requiredItemCount,
                    out var grant);
                Check("expert-job completion fixture inserts required material",
                    inserted && grant.Success,
                    ref failures);

                var sender = new RecordingSender();
                sender.Player.Level = 21;
                sender.Player.GrowType = 2;
                var manager = new QuestManager(sender, connectionString);
                manager.HandleFinishQuestAsync(
                        0x0022,
                        BuildWireFinishBody(expertJobQuestId),
                        sessionId)
                    .GetAwaiter()
                    .GetResult();

                Check("expert-job completion ACK projects expert type 1",
                    TryReadFinishChain(
                        sender.LastAckBody,
                        out var chainType,
                        out var growNumber)
                    && chainType == 20
                    && growNumber == 1,
                    ref failures);
                Check("expert-job completion projects expert state before quest refresh",
                    sender.Calls.Contains("NOTI:00CD")
                    && sender.Player.Subtype0Tail != null
                    && sender.Player.Subtype0Tail.ExpertJobType == 1,
                    ref failures);
                Check("expert-job completion refresh exposes successor 11007",
                    sender.Calls.Count > 0
                    && sender.Calls[sender.Calls.Count - 1] == "NOTI:0015"
                    && ParseQuestIds(sender.LastAcceptableQuestBody)
                        .Contains(successorQuestId),
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, CharacterId);
            }
        }

        private static HashSet<ushort> BuildQuestIds(params ushort[] clearedQuestIds)
            => BuildQuestIds(
                level: 86,
                job: 0,
                growType: 0,
                clearedQuestIds);

        private static HashSet<ushort> BuildQuestIds(
            int level,
            int job,
            int growType,
            params ushort[] clearedQuestIds)
        {
            var clearedFlags = new Dictionary<int, int>();
            foreach (var questId in clearedQuestIds)
                clearedFlags[questId] = 1;

            var body = QuestListBodyBuilder.BuildBody(
                level,
                job,
                growType,
                clearedFlags);
            if (body == null || body.Length < 3)
                throw new InvalidOperationException("Quest list body is truncated.");

            return ParseQuestIds(body);
        }

        private static void CheckJobAndExpertJobSuccessors(ref int failures)
        {
            var jobSuccessors = BuildQuestIds(
                level: 21,
                job: 0,
                growType: 3,
                7810);
            Check("clearing berserker job quest exposes PVF successor 4427",
                jobSuccessors.Contains(4427),
                ref failures);

            var enchanterSuccessors = BuildQuestIds(
                level: 21,
                job: 0,
                growType: 0,
                2702);
            Check("clearing enchanter quest 2702 exposes PVF successor 11007",
                enchanterSuccessors.Contains(11007),
                ref failures);

            var alchemistSuccessors = BuildQuestIds(
                level: 21,
                job: 0,
                growType: 0,
                2708);
            Check("clearing alchemist quest 2708 exposes PVF successor 11013",
                alchemistSuccessors.Contains(11013),
                ref failures);

            var dollControllerSuccessors = BuildQuestIds(
                level: 21,
                job: 0,
                growType: 0,
                2712);
            Check("clearing doll-controller quest 2712 exposes PVF successor 11016",
                dollControllerSuccessors.Contains(11016),
                ref failures);

            var disjointSuccessors = BuildQuestIds(
                level: 21,
                job: 0,
                growType: 0,
                2710);
            Check("disjointer quest 2710 does not invent a missing PVF successor",
                !disjointSuccessors.Contains(11007)
                && !disjointSuccessors.Contains(11013)
                && !disjointSuccessors.Contains(11016),
                ref failures);
        }

        private static void CheckLevelAndPrerequisiteAvailability(
            ushort questId,
            ushort prerequisiteQuestId,
            int minimumLevel,
            string label,
            ref int failures)
        {
            var prerequisite = QuestPrerequisiteCatalog.Get(
                questId);
            Check($"{label} quest keeps its PVF prerequisite",
                prerequisite != null
                && prerequisite.IsValid
                && prerequisite.CompletedQuestGroups.Count == 1
                && prerequisite.CompletedQuestGroups[0].Length == 1
                && prerequisite.CompletedQuestGroups[0][0]
                    == prerequisiteQuestId,
                ref failures);

            Check($"{label} quest remains hidden below its minimum level",
                !BuildQuestIds(
                    level: minimumLevel - 1,
                    job: 0,
                    growType: 0,
                    prerequisiteQuestId).Contains(questId),
                ref failures);
            Check($"{label} quest remains hidden before its prerequisite",
                !BuildQuestIds(
                    level: minimumLevel,
                    job: 0,
                    growType: 0).Contains(questId),
                ref failures);
            Check($"{label} quest appears after level and prerequisite match",
                BuildQuestIds(
                    level: minimumLevel,
                    job: 0,
                    growType: 0,
                    prerequisiteQuestId).Contains(questId),
                ref failures);
            Check($"completed {label} quest is not offered again",
                !BuildQuestIds(
                    level: minimumLevel,
                    job: 0,
                    growType: 0,
                    prerequisiteQuestId,
                    questId).Contains(questId),
                ref failures);
        }

        private static void CheckDanglingPrerequisitePolicy(ref int failures)
        {
            var noirperaSuccessor = QuestPrerequisiteCatalog.Get(1926);
            Check("Noirpera successor keeps its valid prerequisite candidate",
                noirperaSuccessor != null
                && noirperaSuccessor.IsValid
                && noirperaSuccessor.CompletedQuestGroups.Count == 1
                && noirperaSuccessor.CompletedQuestGroups[0].Length == 1
                && noirperaSuccessor.CompletedQuestGroups[0][0] == 1925,
                ref failures);
            Check("clearing Noirpera quest 1925 exposes successor 1926",
                BuildQuestIds(1925).Contains(1926),
                ref failures);
            Check("Noirpera successor remains unavailable before quest 1925",
                !BuildQuestIds().Contains(1926),
                ref failures);

            var mixedAndOrDefinition = QuestPrerequisiteCatalog.Get(5853);
            Check("a dangling AND candidate does not invalidate a legal OR candidate",
                mixedAndOrDefinition != null
                && mixedAndOrDefinition.IsValid
                && mixedAndOrDefinition.CompletedQuestGroups.Count == 1
                && mixedAndOrDefinition.CompletedQuestGroups[0].Length == 2
                && mixedAndOrDefinition.CompletedQuestGroups[0][0] == 2253
                && mixedAndOrDefinition.CompletedQuestGroups[0][1] == 5852,
                ref failures);

            var danglingCollisionDefinition = QuestPrerequisiteCatalog.Get(12733);
            Check("an unknown collision target is diagnostic rather than authorization",
                danglingCollisionDefinition != null
                && danglingCollisionDefinition.IsValid
                && danglingCollisionDefinition.CompletedQuestGroups.Count == 1
                && danglingCollisionDefinition.CompletedQuestGroups[0].Length == 1
                && danglingCollisionDefinition.CompletedQuestGroups[0][0] == 12732
                && danglingCollisionDefinition.CollisionQuestIds.Count == 0
                && danglingCollisionDefinition.Diagnostics.Count > 0,
                ref failures);

            var failClosedQuestIds = new[]
            {
                4344, 4073, 4496, 12732, 12734, 12778,
                20715, 20716, 20717, 20750, 20751, 20752, 20753,
            };
            var allFailClosed = true;
            foreach (var questId in failClosedQuestIds)
            {
                var definition = QuestPrerequisiteCatalog.Get(questId);
                if (definition == null || definition.IsValid)
                {
                    allFailClosed = false;
                    break;
                }
            }
            Check("prerequisites with no valid candidate group remain fail-closed",
                allFailClosed,
                ref failures);

            var unknownAnswer = new PvfLib.QuestFile
            {
                PreRequiredQuestAnswerGroups = new List<string> { "999 0" },
            };
            Check("unknown answer prerequisites remain fail-closed",
                !QuestPrerequisiteDefinition.Parse(
                    1,
                    unknownAnswer,
                    questId => questId == 1).IsValid,
                ref failures);

            var incompleteAndGroup = new PvfLib.QuestFile
            {
                PreRequiredQuestGroups = new List<string> { "2 999" },
            };
            var incompleteAndDefinition = QuestPrerequisiteDefinition.Parse(
                1,
                incompleteAndGroup,
                questId => questId == 1 || questId == 2);
            Check("an AND group cannot survive by dropping its unknown member",
                !incompleteAndDefinition.IsValid,
                ref failures);
        }

        private static HashSet<ushort> ParseQuestIds(byte[] body)
        {
            if (body == null || body.Length < 3)
                throw new InvalidOperationException("Quest list body is truncated.");

            var count = BitConverter.ToUInt16(body, 1);
            if (body.Length != 3 + count * 2)
                throw new InvalidOperationException("Quest list body count does not match its payload length.");

            var result = new HashSet<ushort>();
            for (var index = 0; index < count; index++)
                result.Add(BitConverter.ToUInt16(body, 3 + index * 2));
            return result;
        }

        private static bool TryReadFinishChain(
            byte[] body,
            out byte chainType,
            out byte growNumber)
        {
            chainType = 0;
            growNumber = 0;
            if (body == null || body.Length < 14 || body[0] != 1)
                return false;

            var offset = 12;
            var consumedCount = body[offset++];
            offset += consumedCount * 7;
            if (offset + 1 >= body.Length)
                return false;

            chainType = body[offset++];
            growNumber = body[offset];
            return true;
        }

        private static byte[] BuildWireFinishBody(ushort questId)
        {
            var body = new byte[10];
            BitConverter.GetBytes((ushort)0x0022).CopyTo(body, 0);
            BitConverter.GetBytes(questId).CopyTo(body, 2);
            BitConverter.GetBytes(ushort.MaxValue).CopyTo(body, 4);
            BitConverter.GetBytes((ushort)1).CopyTo(body, 6);
            BitConverter.GetBytes(ushort.MaxValue).CopyTo(body, 8);
            return body;
        }

        private static void CheckTimeGateSameSlotProjection(ref int failures)
        {
            // PVF fixture: dungeon 515 and 518 share one physical XUI slot.
            // The planner must make this deterministic without changing
            // prerequisite OR semantics.
            var active = new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 3, QuestId = 2356 },
                new ActiveQuest { Slot = 5, QuestId = 2406 },
            };

            var projected = QuestDungeonPresentationPlanner.ProjectActiveQuestIds(
                active,
                new Dictionary<int, int>
                {
                    [2350] = 1,
                    [2404] = 1,
                });

            Check("same XUI slot projects only the higher-priority Time Gate quest",
                projected.Contains(2356) && !projected.Contains(2406),
                ref failures);

            var fallback = QuestDungeonPresentationPlanner.ProjectActiveQuestIds(
                new[] { new ActiveQuest { Slot = 5, QuestId = 2406 } },
                new Dictionary<int, int> { [2404] = 1 });
            Check("same-slot candidate takes over after the winner is removed",
                fallback.Contains(2406),
                ref failures);

            var unrelated = QuestDungeonPresentationPlanner.ProjectActiveQuestIds(
                new[]
                {
                    new ActiveQuest { Slot = 3, QuestId = 2356 },
                    new ActiveQuest { Slot = 7, QuestId = 2358 },
                },
                new Dictionary<int, int> { [2350] = 1 });
            Check("different physical slots remain independently visible",
                unrelated.Contains(2356) && unrelated.Contains(2358),
                ref failures);

            var granFloris = QuestDungeonPresentationPlanner.ProjectActiveQuestIds(
                new[]
                {
                    new ActiveQuest { Slot = 1, QuestId = 7803 },
                    new ActiveQuest { Slot = 2, QuestId = 7807 },
                },
                new Dictionary<int, int>());
            Check("non-Time-Gate world map uses the same global slot arbitration",
                granFloris.Count == 1
                && (granFloris.Contains(7803) || granFloris.Contains(7807)),
                ref failures);

            var allowed = granFloris.Contains(7803) ? (ushort)7803 : (ushort)7807;
            var blocked = allowed == 7803 ? (ushort)7807 : (ushort)7803;
            Check("an occupied task-dungeon slot rejects every second active candidate",
                !QuestDungeonActivationPolicy.IsAcceptanceAllowed(
                    blocked,
                    new[] { new ActiveQuest { Slot = 1, QuestId = allowed } })
                && !QuestDungeonActivationPolicy.IsAcceptanceAllowed(
                    allowed,
                    new[] { new ActiveQuest { Slot = 1, QuestId = blocked } }),
                ref failures);

            var acceptableBody = QuestListBodyBuilder.BuildBody(
                level: 86,
                job: 0,
                growType: 0,
                clearedFlags: new Dictionary<int, int>
                {
                    [2350] = 1,
                    [2404] = 1,
                });
            var acceptableIds = ParseQuestIds(acceptableBody);
            Check("acceptable quest packet preserves all legal Time Gate choices",
                acceptableIds.Contains(2356) && acceptableIds.Contains(2406),
                ref failures);

            var jobChoiceBody = QuestListBodyBuilder.BuildBody(
                level: 86,
                job: 0,
                growType: 0,
                clearedFlags: new Dictionary<int, int> { [13099] = 1 });
            var jobChoiceIds = ParseQuestIds(jobChoiceBody);
            Check("acceptable quest packet preserves every swordman job branch",
                jobChoiceIds.Contains(7803)
                && jobChoiceIds.Contains(7807)
                && jobChoiceIds.Contains(7810)
                && jobChoiceIds.Contains(7814),
                ref failures);
        }

        private static string CreateFixtureDatabase(
            string fileName,
            byte level,
            byte growType)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var databasePath = Path.Combine(tempDir, fileName);
            DeleteDatabase(databasePath);
            SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            SeedCharacter(databasePath, level, growType);
            return databasePath;
        }

        private static void SeedCharacter(
            string databasePath,
            byte level = 86,
            byte growType = 0)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'quest-chain-selftest', '');";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.ExecuteNonQuery();
                }
            }

            var repository = new SqliteCharacterRepository(
                databasePath,
                ServerPaths.SchemaFilePath);
            repository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-chain-selftest"),
                Job = 0,
                GrowType = growType,
                Level = level,
            });
            new SqliteSubtype1Repository(databasePath, ServerPaths.SchemaFilePath)
                .UpdateSkillTreeIndex(CharacterId, 0);
        }

        private static void DeleteDatabase(string databasePath)
        {
            foreach (var path in new[]
            {
                databasePath,
                databasePath + "-wal",
                databasePath + "-shm",
            })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private sealed class RecordingSender : ISessionPacketSender
        {
            internal List<string> Calls { get; } = new List<string>();
            internal byte[] LastAckBody { get; private set; }
            internal byte[] LastAcceptableQuestBody { get; private set; }

            public PlayerContext Player { get; } = new PlayerContext
            {
                CharacterId = QuestChainAvailabilitySelfTest.CharacterId,
                Job = 0,
                GrowType = 0,
                Level = 86,
            };

            public int CharacterId => QuestChainAvailabilitySelfTest.CharacterId;
            public int AccountId => QuestChainAvailabilitySelfTest.AccountId;

            public Task SendPacketAsync(byte[] rawPacket) => Task.CompletedTask;

            public Task SendNotiAsync(ushort notiType, byte[] body)
            {
                Calls.Add($"NOTI:{notiType:X4}");
                if (notiType == 0x0015)
                    LastAcceptableQuestBody = body;
                return Task.CompletedTask;
            }

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                Calls.Add($"ACK:{cmdType:X4}");
                LastAckBody = body;
                return Task.CompletedTask;
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
