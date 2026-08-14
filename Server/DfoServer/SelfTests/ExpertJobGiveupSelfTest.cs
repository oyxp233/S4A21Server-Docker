using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.CharacterData;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.ExpertJob;
using DfoServer.Network.Parsers.ExpertJob;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class ExpertJobGiveupSelfTest
    {
        private const int FirstCharacterId = 992000;

        public static int Run()
        {
            Console.WriteLine("=== EXPERT_JOB_GIVEUP selftest ===");
            var failures = 0;
            Check("empty giveup request accepts null",
                GiveupExpertJobRequest.IsValid(null), ref failures);
            Check("empty giveup request accepts zero bytes",
                GiveupExpertJobRequest.IsValid(Array.Empty<byte>()), ref failures);
            Check("giveup request rejects payload",
                !GiveupExpertJobRequest.IsValid(new byte[] { 0 }), ref failures);

            var packet = ExpertJobGiveupPacketBuilder.BuildSuccess(
                new ExpertJobGiveupResult
                {
                    CurrentGold = 54321,
                    GiveupCount = 3,
                });
            Check("giveup success ACK is success/gold/count",
                packet.SequenceEqual(new byte[] { 1, 0x31, 0xD4, 0, 0, 3 }),
                ref failures);

            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo-expert-job-giveup-" + Guid.NewGuid().ToString("N") + ".db");
            var sessions = new List<(Guid SessionId, int CharacterId)>();
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var states = new SqliteExpertJobStateRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var service = new ExpertJobGiveupApplicationService(
                    databasePath,
                    ServerPaths.SchemaFilePath,
                    states);

                RunConfigurationChecks(ref failures);
                RunSuccessfulGiveupChecks(
                    connectionString,
                    states,
                    service,
                    sessions,
                    ref failures);
                RunFailureChecks(
                    connectionString,
                    states,
                    service,
                    sessions,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                failures++;
            }
            finally
            {
                foreach (var entry in sessions)
                    InventoryContext.Unregister(entry.SessionId, entry.CharacterId);
                DeleteTempDatabase(databasePath);
            }

            Console.WriteLine(
                failures == 0
                    ? "EXPERT_JOB_GIVEUP selftest PASS"
                    : "EXPERT_JOB_GIVEUP selftest FAIL count=" + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void RunConfigurationChecks(ref int failures)
        {
            var expectedDeleteItems = new Dictionary<byte, int>
            {
                [ExpertJobStateCodec.EnchanterType] = 2600482,
                [ExpertJobStateCodec.AlchemistType] = 2600463,
                [ExpertJobStateCodec.DisjointerType] = 0,
                [ExpertJobStateCodec.DollControllerType] = 2600474,
            };
            var expectedSkills = new Dictionary<byte, ushort>
            {
                [ExpertJobStateCodec.EnchanterType] = 191,
                [ExpertJobStateCodec.AlchemistType] = 193,
                [ExpertJobStateCodec.DisjointerType] = 194,
                [ExpertJobStateCodec.DollControllerType] = 192,
            };

            foreach (var type in expectedDeleteItems.Keys)
            {
                var available = ExpertJobGiveupConfigProvider.TryGet(type, out var config);
                Check("PVF giveup config type=" + type,
                    available
                    && config.ClearQuestIds.Count == 13
                    && config.ConnectQuestIds.Count > 0
                    && config.GiveupCosts.SequenceEqual(new[] { 1000, 10000, 100000, 1000000 })
                    && config.DeleteItemId == expectedDeleteItems[type]
                    && config.SkillIds.SequenceEqual(new[] { expectedSkills[type] }),
                    ref failures);
            }
        }

        private static void RunSuccessfulGiveupChecks(
            string connectionString,
            SqliteExpertJobStateRepository states,
            ExpertJobGiveupApplicationService service,
            ICollection<(Guid SessionId, int CharacterId)> sessions,
            ref int failures)
        {
            var types = new[]
            {
                ExpertJobStateCodec.EnchanterType,
                ExpertJobStateCodec.AlchemistType,
                ExpertJobStateCodec.DisjointerType,
                ExpertJobStateCodec.DollControllerType,
            };
            for (var index = 0; index < types.Length; index++)
            {
                var type = types[index];
                ExpertJobGiveupConfigProvider.TryGet(type, out var config);
                var characterId = FirstCharacterId + index;
                var giveupCount = index;
                var cost = config.GiveupCosts[giveupCount];
                var sessionId = Guid.NewGuid();
                var lease = SeedCharacter(
                    connectionString,
                    characterId,
                    type,
                    giveupCount,
                    cost + 123,
                    config,
                    includeConnectQuest: true,
                    includeDeleteItem: config.DeleteItemId > 0,
                    sessionId);
                sessions.Add((sessionId, characterId));

                var result = service.Apply(lease, sessionId, config);
                var expectedNextCount = (byte)Math.Min(
                    config.GiveupCosts.Count - 1,
                    giveupCount + 1);
                Check("giveup succeeds type=" + type,
                    result.Success
                    && result.CurrentGold == 123
                    && result.GiveupCount == expectedNextCount
                    && result.InventoryChanges.Slots.Any(slot =>
                        slot.ListType == InventoryListType.Main
                        && slot.SlotIndex == InventoryService.MainVirtualCurrencySlotStart),
                    ref failures);
                Check("giveup resets subtype0 type=" + type,
                    ReadSubtype(connectionString, characterId) == (0, -1), ref failures);
                Check("giveup resets persistent state type=" + type,
                    ReadExpertJobState(connectionString, characterId)
                    == (expectedNextCount, 0, 0, 0), ref failures);
                Check("giveup clears recipes type=" + type,
                    ReadInt(connectionString, @"
SELECT COUNT(*) FROM character_expert_job_recipes WHERE character_id=@cid;", characterId) == 0,
                    ref failures);
                Check("giveup resets all expert quest flags type=" + type,
                    !HasAnyClearedQuest(
                        connectionString,
                        characterId,
                        config.ClearQuestIds),
                    ref failures);
                Check("giveup deletes active expert transfer quests type=" + type,
                    !new QuestRepository(connectionString).LoadActiveQuests(characterId)
                        .Any(quest => config.ClearQuestIds.Contains(quest.QuestId)),
                    ref failures);

                var skills = SqliteCharacterProgressRepository
                    .FromConnectionString(connectionString)
                    .LoadSkills(characterId);
                Check("giveup removes expert skill only type=" + type,
                    skills.Pages.All(page => page.Entries.All(entry =>
                        entry.SkillId != config.SkillIds[0]))
                    && skills.Pages.SelectMany(page => page.Entries).Any(entry => entry.SkillId == 77),
                    ref failures);
                Check("giveup removes profession item type=" + type,
                    config.DeleteItemId == 0
                    || lease.Inventory.CountMainItem(config.DeleteItemId) == 0,
                    ref failures);
                using (var connection = OpenConnection(connectionString))
                using (var transaction = connection.BeginTransaction())
                {
                    if (!SqliteSubtype0FieldsRepository.SetExpertJobInTransaction(
                            connection,
                            transaction,
                            characterId,
                            type))
                        throw new InvalidOperationException(
                            "could not seed expert-job retransfer");
                    SqliteExpertJobStateRepository.InitializeInTransaction(
                        connection,
                        transaction,
                        characterId,
                        type);
                    transaction.Commit();
                }
                Check("expert-job retransfer resets experience type=" + type,
                    ReadSubtype(connectionString, characterId) == (type, 0),
                    ref failures);
                Check("expert-job retransfer preserves giveup count type=" + type,
                    states.Load(characterId, type).GiveUpCount == expectedNextCount,
                    ref failures);
            }
        }

        private static void RunFailureChecks(
            string connectionString,
            SqliteExpertJobStateRepository states,
            ExpertJobGiveupApplicationService service,
            ICollection<(Guid SessionId, int CharacterId)> sessions,
            ref int failures)
        {
            ExpertJobGiveupConfigProvider.TryGet(
                ExpertJobStateCodec.EnchanterType,
                out var enchanter);
            var insufficientCharacterId = FirstCharacterId + 10;
            var insufficientSessionId = Guid.NewGuid();
            var insufficientLease = SeedCharacter(
                connectionString,
                insufficientCharacterId,
                ExpertJobStateCodec.EnchanterType,
                0,
                enchanter.GiveupCosts[0] - 1,
                enchanter,
                includeConnectQuest: true,
                includeDeleteItem: true,
                insufficientSessionId);
            sessions.Add((insufficientSessionId, insufficientCharacterId));
            var insufficient = service.Apply(
                insufficientLease,
                insufficientSessionId,
                enchanter);
            Check("giveup rejects insufficient gold",
                !insufficient.Success
                && insufficient.ErrorCode == ExpertJobGiveupResult.ErrorInsufficientGold
                && ReadSubtype(connectionString, insufficientCharacterId).Type
                    == ExpertJobStateCodec.EnchanterType
                && insufficientLease.Inventory.CountMainItem(
                    InventoryService.MainVirtualCurrencySlotStart)
                    == enchanter.GiveupCosts[0] - 1,
                ref failures);

            ExpertJobGiveupConfigProvider.TryGet(
                ExpertJobStateCodec.AlchemistType,
                out var alchemist);
            var missingQuestCharacterId = FirstCharacterId + 11;
            var missingQuestSessionId = Guid.NewGuid();
            var missingQuestLease = SeedCharacter(
                connectionString,
                missingQuestCharacterId,
                ExpertJobStateCodec.AlchemistType,
                0,
                alchemist.GiveupCosts[0],
                alchemist,
                includeConnectQuest: false,
                includeDeleteItem: true,
                missingQuestSessionId);
            sessions.Add((missingQuestSessionId, missingQuestCharacterId));
            var missingQuest = service.Apply(
                missingQuestLease,
                missingQuestSessionId,
                alchemist);
            Check("giveup rejects missing transfer completion",
                !missingQuest.Success
                && missingQuest.ErrorCode == ExpertJobGiveupResult.ErrorInvalidState
                && ReadSubtype(connectionString, missingQuestCharacterId).Type
                    == ExpertJobStateCodec.AlchemistType,
                ref failures);

            ExpertJobGiveupConfigProvider.TryGet(
                ExpertJobStateCodec.DollControllerType,
                out var dollController);
            var rollbackCharacterId = FirstCharacterId + 12;
            var rollbackSessionId = Guid.NewGuid();
            var rollbackLease = SeedCharacter(
                connectionString,
                rollbackCharacterId,
                ExpertJobStateCodec.DollControllerType,
                1,
                dollController.GiveupCosts[1] + 55,
                dollController,
                includeConnectQuest: true,
                includeDeleteItem: true,
                rollbackSessionId);
            sessions.Add((rollbackSessionId, rollbackCharacterId));
            Execute(connectionString, @"
CREATE TRIGGER selftest_expert_job_giveup_rollback
BEFORE UPDATE OF expert_job_type ON character_subtype0_fields
WHEN NEW.expert_job_type = 0
BEGIN
    SELECT RAISE(ABORT, 'selftest rollback');
END;");
            var rollback = service.Apply(
                rollbackLease,
                rollbackSessionId,
                dollController);
            Check("giveup rolls back all mutations on persistence error",
                !rollback.Success
                && rollback.ErrorCode == ExpertJobGiveupResult.ErrorPersistence
                && ReadSubtype(connectionString, rollbackCharacterId).Type
                    == ExpertJobStateCodec.DollControllerType
                && rollbackLease.Inventory.CountMainItem(
                    InventoryService.MainVirtualCurrencySlotStart)
                    == dollController.GiveupCosts[1] + 55
                && rollbackLease.Inventory.CountMainItem(dollController.DeleteItemId) == 5
                && states.Load(rollbackCharacterId, ExpertJobStateCodec.DollControllerType)
                    .GiveUpCount == 1,
                ref failures);
        }

        private static InventoryLease SeedCharacter(
            string connectionString,
            int characterId,
            byte expertJobType,
            int giveupCount,
            int gold,
            ExpertJobGiveupConfig config,
            bool includeConnectQuest,
            bool includeDeleteItem,
            Guid sessionId)
        {
            using (var connection = OpenConnection(connectionString))
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id) VALUES (@cid, @name);
INSERT INTO characters (character_id, account_id, name)
VALUES (@cid, @cid, @name);
INSERT INTO character_subtype0_fields (
    character_id, expert_job_type, expert_job_exp)
VALUES (@cid, @type, 321);
INSERT INTO character_expert_job (
    character_id, giveup_count, disjoint_machine_grade,
    disjoint_machine_endurance, enchanter_endurance)
VALUES (@cid, @giveup, 7, 333, 444);
INSERT INTO character_expert_job_recipes (character_id, recipe_id)
VALUES (@cid, 987654);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@name", "expert-giveup-" + characterId);
                    command.Parameters.AddWithValue("@type", (int)expertJobType);
                    command.Parameters.AddWithValue("@giveup", giveupCount);
                    command.ExecuteNonQuery();
                }

                foreach (var questId in config.ClearQuestIds)
                    QuestRepository.MarkQuestCleared(
                        connection,
                        transaction,
                        characterId,
                        questId);
                foreach (var questId in config.ConnectQuestIds)
                {
                    QuestRepository.DeleteClearedFlag(
                        connection,
                        transaction,
                        characterId,
                        questId);
                }
                if (includeConnectQuest)
                    QuestRepository.MarkQuestCleared(
                        connection,
                        transaction,
                        characterId,
                        config.ConnectQuestIds[0]);
                QuestRepository.InsertActiveQuest(
                    connection,
                    transaction,
                    characterId,
                    0,
                    config.ClearQuestIds[config.ClearQuestIds.Count - 1],
                    0);

                var progress = SqliteCharacterProgressRepository
                    .FromConnectionString(connectionString);
                progress.SaveSkillProgress(
                    connection,
                    transaction,
                    characterId,
                    BuildSkills(config.SkillIds[0]));
                transaction.Commit();
            }

            var inventory = new InventoryService(characterId, characterId);
            inventory.SetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                gold);
            if (includeDeleteItem && config.DeleteItemId > 0)
            {
                inventory.SetItem(
                    InventoryListType.Main,
                    233,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindExpertJobMaterial,
                        ItemId = config.DeleteItemId,
                        Count = 2,
                    });
                inventory.SetItem(
                    InventoryListType.Main,
                    234,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindExpertJobMaterial,
                        ItemId = config.DeleteItemId,
                        Count = 3,
                    });
            }
            var lease = InventoryContext.Register(sessionId, inventory);
            using (var connection = OpenConnection(connectionString))
            using (var transaction = connection.BeginTransaction())
            {
                if (!InventoryPersistenceService.SaveDirtyInTransaction(
                        connection,
                        transaction,
                        lease))
                    throw new InvalidOperationException("could not seed inventory");
                transaction.Commit();
            }
            inventory.ClearDirtyState();
            return lease;
        }

        private static SkillInfoSnapshot BuildSkills(ushort expertSkillId)
        {
            var skills = new SkillInfoSnapshot();
            skills.Pages.Add(new SkillInfoPageSnapshot());
            skills.Pages.Add(new SkillInfoPageSnapshot());
            skills.Pages[0].Entries.Add(new SkillInfoEntrySnapshot
            {
                Slot = 0,
                SkillId = expertSkillId,
                Level = 1,
            });
            skills.Pages[1].Entries.Add(new SkillInfoEntrySnapshot
            {
                Slot = 1,
                SkillId = 77,
                Level = 1,
            });
            return skills;
        }

        private static (int Type, int Exp) ReadSubtype(string connectionString, int characterId)
        {
            using (var connection = OpenConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT expert_job_type, expert_job_exp
FROM character_subtype0_fields
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    return reader.Read()
                        ? (reader.GetInt32(0), reader.GetInt32(1))
                        : (-1, -1);
                }
            }
        }

        private static (int GiveupCount, int Grade, int Endurance, int EnchanterEndurance)
            ReadExpertJobState(string connectionString, int characterId)
        {
            using (var connection = OpenConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT giveup_count, disjoint_machine_grade, disjoint_machine_endurance,
       enchanter_endurance
FROM character_expert_job
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    return reader.Read()
                        ? (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3))
                        : (-1, -1, -1, -1);
                }
            }
        }

        private static int ReadInt(string connectionString, string sql, int characterId)
        {
            using (var connection = OpenConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.Parameters.AddWithValue("@cid", characterId);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static bool HasAnyClearedQuest(
            string connectionString,
            int characterId,
            IReadOnlyCollection<ushort> questIds)
        {
            using (var connection = OpenConnection(connectionString))
            {
                return QuestRepository.HasAnyClearedQuest(
                    connection,
                    null,
                    characterId,
                    questIds);
            }
        }

        private static SqliteConnection OpenConnection(string connectionString)
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection;
        }

        private static void Execute(string connectionString, string sql)
        {
            using (var connection = OpenConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            try
            {
                if (File.Exists(databasePath))
                    File.Delete(databasePath);
            }
            catch
            {
            }
        }

        private static void Check(string label, bool condition, ref int failures)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + label);
            if (!condition)
                failures++;
        }
    }
}
