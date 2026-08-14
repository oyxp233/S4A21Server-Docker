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
    public static class QuestionQuestBranchSelfTest
    {
        private const int CharacterId = 364001;
        private const int AccountId = 364001;
        private const ushort PrerequisiteQuestId = 1861;
        private const ushort BranchQuestionQuestId = 1862;
        private const ushort PrincessBranchQuestId = 1863;
        private const ushort PrinceBranchQuestId = 1864;

        public static int Run()
        {
            Console.WriteLine("=== QUESTION_QUEST_BRANCH selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "question-quest-branch.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var schemaPath = ServerPaths.SchemaFilePath;
            var characterRepository = new SqliteCharacterRepository(dbPath, schemaPath);
            SeedAccount(dbPath);
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("question-branch-test"),
                Job = 0,
                GrowType = 0,
                Level = 86,
            });

            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            var questService = new QuestService(connStr);
            var failures = 0;
            var sessionId = Guid.NewGuid();
            InventoryContext.Register(
                sessionId,
                new InventoryService(CharacterId, AccountId));

            Check("1862 is question quest", GameWorld.QuestData.IsQuestionQuest(BranchQuestionQuestId), ref failures);
            Check("1862 has two answer-dependent successors", GameWorld.QuestData.GetQuestionAnswerCount(BranchQuestionQuestId) == 2, ref failures);

            ResetBranchState(connStr);
            MarkQuestCleared(connStr, PrerequisiteQuestId, 1);
            var acceptQuestionForPrincess = questService.HandleAcceptQuest(CharacterId,
                BuildQuestBody(BranchQuestionQuestId), AccountId);
            Check("accept 1862 for princess branch succeeds", IsSuccessAck(acceptQuestionForPrincess), ref failures);
            Check("1862 starts with trigger 1", TryReadAcceptTrigger(acceptQuestionForPrincess, out var firstInitTrigger) && firstInitTrigger == 1, ref failures);

            questService.HandleSetTrigger(CharacterId, BuildSetTriggerBody(BranchQuestionQuestId, increment: false));
            var finishPrincessChoice = QuestSelfTestCommandAdapter.HandleFinish(
                questService,
                CharacterId,
                BuildFinishBody(BranchQuestionQuestId, ushort.MaxValue));
            Check("finish 1862 after first answer succeeds", IsSuccessAck(finishPrincessChoice), ref failures);
            Check("1862 stores first answer as flag 1", LoadQuestFlag(connStr, BranchQuestionQuestId) == 1, ref failures);
            Check("first answer exposes 1863 only", IsAcceptableBranch(connStr, PrincessBranchQuestId) && !IsAcceptableBranch(connStr, PrinceBranchQuestId), ref failures);

            ResetBranchState(connStr);
            MarkQuestCleared(connStr, PrerequisiteQuestId, 1);
            var acceptQuestionForPrince = questService.HandleAcceptQuest(CharacterId,
                BuildQuestBody(BranchQuestionQuestId), AccountId);
            Check("accept 1862 for prince branch succeeds", IsSuccessAck(acceptQuestionForPrince), ref failures);

            questService.HandleSetTrigger(CharacterId, BuildSetTriggerBody(BranchQuestionQuestId, increment: true));
            var finishPrinceChoice = QuestSelfTestCommandAdapter.HandleFinish(
                questService,
                CharacterId,
                BuildFinishBody(BranchQuestionQuestId, 0));
            Check("finish 1862 after second answer succeeds even with reward index zero", IsSuccessAck(finishPrinceChoice), ref failures);
            Check("1862 stores second answer trigger as flag 2", LoadQuestFlag(connStr, BranchQuestionQuestId) == 2, ref failures);
            Check("second answer exposes 1864 only", !IsAcceptableBranch(connStr, PrincessBranchQuestId) && IsAcceptableBranch(connStr, PrinceBranchQuestId), ref failures);

            var acceptWrongBranch = questService.HandleAcceptQuest(CharacterId,
                BuildQuestBody(PrincessBranchQuestId), AccountId);
            Check("direct accept of unchosen 1863 is rejected", IsFailAck(acceptWrongBranch, 21), ref failures);

            var acceptCorrectBranch = questService.HandleAcceptQuest(CharacterId,
                BuildQuestBody(PrinceBranchQuestId), AccountId);
            Check("direct accept of chosen 1864 succeeds", IsSuccessAck(acceptCorrectBranch), ref failures);

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

        private static byte[] BuildFinishBody(
            ushort questId,
            ushort rewardSelectIdx) =>
            QuestSelfTestCommandAdapter.BuildFinishBody(
                questId,
                rewardSelectIdx);

        private static byte[] BuildSetTriggerBody(ushort questId, bool increment)
        {
            var body = new byte[4];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            body[2] = 0;
            body[3] = increment ? (byte)1 : (byte)0;
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

        private static bool IsFailAck(QuestAcceptResult result, byte errorCode)
        {
            return result != null && !result.Success && result.ErrorCode == errorCode;
        }

        private static bool TryReadAcceptTrigger(QuestAcceptResult result, out uint trigger)
        {
            trigger = result != null ? result.InitTrigger : 0;
            return result != null && result.Success;
        }

        private static bool IsAcceptableBranch(string connStr, ushort questId)
        {
            var flags = LoadClearedFlags(connStr);
            var clearedSet = new HashSet<int>(flags.Keys);
            var acceptable = GameWorld.QuestData.ComputeAcceptableQuests(86, 0, 0, clearedSet, flags);
            return acceptable.Contains(questId);
        }

        private static Dictionary<int, int> LoadClearedFlags(string connStr)
        {
            var result = new Dictionary<int, int>();
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT quest_id, completion_value FROM character_quest_completions WHERE character_id=@cid";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int questId = reader.GetInt32(0);
                            int flagValue = reader.GetInt32(1);
                            if (flagValue != 0)
                                result[questId] = flagValue;
                        }
                    }
                }
            }

            return result;
        }

        private static int LoadQuestFlag(string connStr, ushort questId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT completion_value FROM character_quest_completions WHERE character_id=@cid AND quest_id=@qid";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.Parameters.AddWithValue("@qid", questId);
                    var result = cmd.ExecuteScalar();
                    return result != null ? Convert.ToInt32(result) : 0;
                }
            }
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
                    cmd.Parameters.AddWithValue("@mid", "question-quest-branch-test");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void MarkQuestCleared(string connStr, ushort questId, int flagValue)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR REPLACE INTO character_quest_completions (character_id, quest_id, completion_value)
VALUES (@cid, @qid, @flag);";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.Parameters.AddWithValue("@qid", questId);
                    cmd.Parameters.AddWithValue("@flag", flagValue);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void ResetBranchState(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
DELETE FROM character_active_quests
WHERE character_id=@cid AND quest_id IN (1862, 1863, 1864);";
                        cmd.Parameters.AddWithValue("@cid", CharacterId);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
DELETE FROM character_quest_completions
WHERE character_id=@cid AND quest_id IN (1862, 1863, 1864);";
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
