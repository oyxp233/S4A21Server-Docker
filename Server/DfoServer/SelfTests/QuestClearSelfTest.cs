using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class QuestClearSelfTest
    {
        private const int CharacterId = 291001;
        private const int AccountId = 291001;
        private const ushort ParentQuestId = 1826;
        private const ushort SampleQuestId = 1827;
        private const ushort ToolQuestId = 1828;
        private const int SampleItemId = 10099813;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_CLEAR selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "quest-clear.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var schemaPath = ServerPaths.SchemaFilePath;
            var characterRepository = new SqliteCharacterRepository(dbPath, schemaPath);
            SeedAccount(dbPath);
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-clear-test"),
                Job = 0,
                GrowType = 0,
                Level = 23,
            });

            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            var questService = new QuestService(connStr);
            var failures = 0;
            var sessionId = Guid.NewGuid();
            var inventory = new InventoryService(CharacterId, AccountId);
            InventoryContext.Register(
                sessionId,
                inventory);

            Check("1826 is quest-clear parent", GameWorld.QuestData.IsQuestClearQuest(ParentQuestId), ref failures);
            Check("1826 requires 1827 and 1828",
                string.Join(",", GameWorld.QuestData.GetQuestClearRequiredQuestIds(ParentQuestId)) == "1827,1828",
                ref failures);

            MarkQuestCleared(connStr, 1825);
            var acceptParent = questService.HandleAcceptQuest(CharacterId,
                BuildQuestBody(ParentQuestId), AccountId);
            Check("accept parent succeeds", IsSuccessAck(acceptParent), ref failures);
            Check("accept parent trigger starts with two missing subquests",
                TryReadAcceptTrigger(acceptParent, out var initTrigger) && initTrigger == 2,
                ref failures);

            ResetQuestState(connStr);
            MarkQuestCleared(connStr, ToolQuestId);
            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = ParentQuestId, TriggerValue = 2 },
                new ActiveQuest { Slot = 1, QuestId = SampleQuestId, TriggerValue = 0 },
            });
            Check("sample quest inventory contains its five required items",
                InventoryRewardGrantService.TryCreateAndInsert(
                    inventory,
                    SampleItemId,
                    ItemCreateReason.QuestReward,
                    5,
                    out _),
                ref failures);
            var finishSample = QuestSelfTestCommandAdapter.HandleFinish(
                questService,
                CharacterId,
                QuestSelfTestCommandAdapter.BuildFinishBody(SampleQuestId));
            Check("finishing last missing child succeeds", IsSuccessAck(finishSample), ref failures);
            Check("parent trigger syncs to zero", LoadTrigger(connStr, ParentQuestId) == 0, ref failures);

            ResetQuestState(connStr);
            MarkQuestCleared(connStr, ToolQuestId);
            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = ParentQuestId, TriggerValue = 1 },
            });
            var blockedParent = QuestSelfTestCommandAdapter.HandleFinish(
                questService,
                CharacterId,
                QuestSelfTestCommandAdapter.BuildFinishBody(ParentQuestId));
            Check("parent finish fails while a subquest is missing", IsFailAck(blockedParent, 22), ref failures);

            ResetQuestState(connStr);
            MarkQuestCleared(connStr, ToolQuestId);
            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = ParentQuestId, TriggerValue = 0 },
            });
            var triggerZeroButMissingChild = QuestSelfTestCommandAdapter.HandleFinish(
                questService,
                CharacterId,
                QuestSelfTestCommandAdapter.BuildFinishBody(ParentQuestId));
            Check("parent finish still checks children when trigger is already zero",
                IsFailAck(triggerZeroButMissingChild, 22),
                ref failures);

            ResetQuestState(connStr);
            MarkQuestCleared(connStr, ToolQuestId);
            MarkQuestCleared(connStr, SampleQuestId);
            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = ParentQuestId, TriggerValue = 2 },
            });
            var staleParent = QuestSelfTestCommandAdapter.HandleFinish(
                questService,
                CharacterId,
                QuestSelfTestCommandAdapter.BuildFinishBody(
                    ParentQuestId,
                    rewardSelection: 0));
            Check("stale nonzero parent trigger can finish after all subquests cleared", IsSuccessAck(staleParent), ref failures);

            InventoryContext.Unregister(sessionId, CharacterId);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildQuestBody(ushort questId)
        {
            var body = new byte[2];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            return body;
        }

        private static bool IsSuccessAck(QuestAcceptResult result)
        {
            return result != null && result.Success;
        }

        private static bool IsSuccessAck(QuestFinishResult result)
        {
            return result != null && result.Success;
        }

        private static bool IsFailAck(QuestFinishResult result, byte errorCode)
        {
            return result != null && !result.Success && result.ErrorCode == errorCode;
        }

        private static bool TryReadAcceptTrigger(QuestAcceptResult result, out uint trigger)
        {
            trigger = result != null ? result.InitTrigger : 0;
            return result != null && result.Success;
        }

        private static uint LoadTrigger(string connStr, ushort questId)
        {
            var active = QuestService.LoadActiveQuests(connStr, CharacterId);
            var quest = QuestService.FindByQuestId(active, questId);
            return quest != null ? quest.TriggerValue : uint.MaxValue;
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
                    cmd.Parameters.AddWithValue("@mid", "quest-clear-test");
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

        private static void ResetQuestState(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM character_active_quests WHERE character_id=@cid;";
                        cmd.Parameters.AddWithValue("@cid", CharacterId);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
DELETE FROM character_quest_completions
WHERE character_id=@cid AND quest_id IN (1826, 1827, 1828);";
                        cmd.Parameters.AddWithValue("@cid", CharacterId);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
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
