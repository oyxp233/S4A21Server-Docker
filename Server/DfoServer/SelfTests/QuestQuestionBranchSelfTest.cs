using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Quest;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class QuestQuestionBranchSelfTest
    {
        private const int AccountId = 991901;
        private const ushort QuestionQuestId = 3032;
        private const uint UnresolvedTrigger = 4;
        private static readonly ushort[] BranchQuestIds = { 3033, 3034, 3035 };

        public static int Run()
        {
            Console.WriteLine("=== QUEST_QUESTION_BRANCH selftest ===");
            var failures = 0;
            var path = Path.Combine(
                Path.GetTempPath(), $"s4a21-question-branch-{Guid.NewGuid():N}.db");
            try
            {
                Check("PVF Heart's Choice has three answer branches",
                    QuestData.GetQuestFile(QuestionQuestId)?.Name
                        ?.Contains("心灵的选择") == true
                    && QuestData.IsQuestionQuest(QuestionQuestId)
                    && QuestData.GetQuestionAnswerCount(QuestionQuestId) == 3,
                    ref failures);
                for (var answerIndex = 0; answerIndex < BranchQuestIds.Length; answerIndex++)
                {
                    var definition = QuestPrerequisiteCatalog.Get(
                        BranchQuestIds[answerIndex]);
                    Check($"PVF branch {BranchQuestIds[answerIndex]} requires answer {answerIndex}",
                        definition != null && definition.IsValid
                        && definition.RequiredAnswers.Count == 1
                        && definition.RequiredAnswers[0].QuestId == QuestionQuestId
                        && definition.RequiredAnswers[0].AnswerIndex == answerIndex,
                        ref failures);
                }

                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'question-branch-test', '');";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.ExecuteNonQuery();
                }

                for (ushort answerIndex = 0; answerIndex < BranchQuestIds.Length; answerIndex++)
                {
                    VerifyCompletion(database, 991910 + answerIndex,
                        $"A21 SET_TRIGGER answer {answerIndex}", answerIndex,
                        ushort.MaxValue, answerIndex + 1, ref failures);
                    VerifyCompletion(database, 991920 + answerIndex,
                        $"explicit FINISH answer {answerIndex}", UnresolvedTrigger,
                        answerIndex, answerIndex + 1, ref failures);
                }
                VerifyCompletion(database, 991930,
                    "current activity retains the existing answer source priority",
                    1, 2, 2, ref failures);
                VerifyCompletion(database, 991931,
                    "FINISH answer resolves an out-of-range activity trigger",
                    UnresolvedTrigger, 2, 3, ref failures);
                VerifyCompletion(database, 991932,
                    "trigger above answer count without a fallback rejects completion",
                    UnresolvedTrigger, ushort.MaxValue, 0, ref failures);
                VerifyCompletion(database, 991936,
                    "both answer sources out of range reject completion",
                    UnresolvedTrigger, 3, 0, ref failures);
                VerifyCompletion(database, 991937,
                    "trigger equal to answer count rejects completion",
                    3, ushort.MaxValue, 0, ref failures);
                VerifyClientAnswerPackets(database, ref failures);
                VerifyRollback(database, ref failures);
                VerifyReplacedSession(database, ref failures);
                VerifyReacceptedActivation(database, ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] question branch check threw: {ex}");
                failures++;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var databaseFile in new[] { path, path + "-wal", path + "-shm" })
                {
                    if (File.Exists(databaseFile))
                        File.Delete(databaseFile);
                }
            }
            Console.WriteLine(failures == 0
                ? "QUEST_QUESTION_BRANCH selftest passed."
                : $"QUEST_QUESTION_BRANCH selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static QuestCommandOwnerContext PrepareChoice(
            GameDatabase database, int characterId, uint trigger)
        {
            // A21 DNF.exe 0x00E73520 sends questId + type 0 + answerIndex,
            // then FINISH with selection 0xFFFF. Out-of-range stored values below
            // are deliberate corrupt-state fixtures, not client answer packets.
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job, level)
VALUES (@cid, @aid, @name, 0, 86);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@name", $"question-{characterId}");
                    command.ExecuteNonQuery();
                }
                QuestRepository.MarkQuestCleared(
                    connection, transaction, characterId, 3031);
                transaction.Commit();
            }

            var owner = LoadOwner(database, characterId);
            try
            {
                var accepted = new QuestAcceptanceApplicationService(database.ConnectionString)
                    .Apply(owner, new QuestAcceptCommand(QuestionQuestId));
                if (!accepted.Success)
                    throw new InvalidOperationException("could not accept question quest");

                var repository = new QuestRepository(database.ConnectionString);
                var active = repository.LoadActiveQuests(characterId)[0];
                if (trigger < BranchQuestIds.Length)
                {
                    var result = new QuestService(database.ConnectionString)
                        .HandleSetTrigger(owner, AnswerBody((byte)trigger));
                    if (!result.Success)
                        throw new InvalidOperationException("A21 answer packet was rejected");
                }
                else
                {
                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        if (!QuestRepository.TryUpdateTriggerValueCas(
                                connection, transaction, characterId, QuestionQuestId,
                                active.ActivationId, active.Version, active.TriggerValue, trigger))
                            throw new InvalidOperationException("could not prepare invalid trigger");
                        transaction.Commit();
                    }
                }
                return owner;
            }
            catch
            {
                InventoryContext.Unregister(owner.SessionId, characterId);
                throw;
            }
        }

        private static QuestCommandOwnerContext LoadOwner(
            GameDatabase database, int characterId)
        {
            InventoryService inventory;
            using (var connection = database.OpenConnection())
            {
                inventory = InventoryService.LoadFromDb(
                    connection, characterId, AccountId, database);
            }
            var sessionId = Guid.NewGuid();
            var lease = InventoryContext.Register(sessionId, characterId, inventory);
            return new QuestCommandOwnerContext(characterId, AccountId, sessionId, lease);
        }

        private static QuestFinishCommand ParseFinish(ushort answerIndex)
        {
            var fields = new ushort[] { QuestionQuestId, answerIndex, 1, ushort.MaxValue };
            var body = new byte[8];
            Buffer.BlockCopy(fields, 0, body, 0, body.Length);
            if (!QuestCommandParser.TryParseFinish(body, out var command))
                throw new InvalidOperationException("valid FINISH body was rejected");
            return command;
        }

        private static byte[] AnswerBody(byte answerIndex, byte triggerType = 0)
            => new byte[]
            {
                (byte)(QuestionQuestId & 0xFF), (byte)(QuestionQuestId >> 8),
                triggerType, answerIndex,
            };

        private static void VerifyCompletion(
            GameDatabase database, int characterId, string name,
            uint trigger, ushort answerIndex, int expectedFlag, ref int failures)
        {
            var owner = PrepareChoice(database, characterId, trigger);
            try
            {
                var command = ParseFinish(answerIndex);
                var service = new QuestService(database.ConnectionString);
                var repository = new QuestRepository(database.ConnectionString);
                Check(name + " preserves the full stored answer index",
                    repository.LoadActiveQuests(characterId)[0].TriggerValue == trigger,
                    ref failures);
                var result = service.HandleFinishQuest(owner, command);
                Check(name + " persists the expected completion flag",
                    result.Success == (expectedFlag > 0)
                    && repository.ReadClearedFlagValue(characterId, QuestionQuestId) == expectedFlag,
                    ref failures);
                if (expectedFlag == 0)
                {
                    var active = repository.LoadActiveQuests(characterId);
                    Check(name + " preserves the activity for retry",
                        active.Count == 1 && active[0].QuestId == QuestionQuestId
                        && active[0].TriggerValue == trigger,
                        ref failures);
                    return;
                }

                var replay = service.HandleFinishQuest(owner, command);
                Check(name + " rejects replay without changing the answer",
                    !replay.Success
                    && repository.ReadClearedFlagValue(characterId, QuestionQuestId) == expectedFlag
                    && repository.LoadActiveQuests(characterId).Count == 0,
                    ref failures);

                InventoryContext.Unregister(owner.SessionId, characterId);
                owner = LoadOwner(database, characterId);
                VerifyBranch(database, owner, expectedFlag - 1, name, ref failures);
            }
            finally
            {
                InventoryContext.Unregister(owner.SessionId, characterId);
            }
        }

        private static void VerifyClientAnswerPackets(GameDatabase database, ref int failures)
        {
            var owner = PrepareChoice(database, 991938, 2);
            try
            {
                var service = new QuestService(database.ConnectionString);
                var repository = new QuestRepository(database.ConnectionString);
                var before = repository.LoadActiveQuests(owner.CharacterId)[0];
                foreach (var body in new[]
                {
                    AnswerBody(3), AnswerBody(byte.MaxValue), AnswerBody(2, 1),
                    new byte[] { (byte)(QuestionQuestId & 0xFF), (byte)(QuestionQuestId >> 8), 0 },
                })
                {
                    var rejected = service.HandleSetTrigger(owner, body);
                    var after = repository.LoadActiveQuests(owner.CharacterId)[0];
                    Check("invalid A21 answer packet preserves the active answer",
                        !rejected.Success && after.TriggerValue == before.TriggerValue
                        && after.Version == before.Version, ref failures);
                }
                var repeated = service.HandleSetTrigger(owner, AnswerBody(2));
                var unchanged = repository.LoadActiveQuests(owner.CharacterId)[0];
                Check("repeated answer is an absolute assignment without a second increment",
                    repeated.Success && repeated.TriggerValue == 2
                    && unchanged.TriggerValue == 2 && unchanged.Version == before.Version,
                    ref failures);
                Check("changing the answer replaces its index",
                    service.HandleSetTrigger(owner, AnswerBody(1)).Success
                    && repository.LoadActiveQuests(owner.CharacterId)[0].TriggerValue == 1
                    && service.HandleSetTrigger(owner, AnswerBody(2)).Success
                    && service.HandleFinishQuest(owner, ParseFinish(ushort.MaxValue)).Success
                    && repository.ReadClearedFlagValue(owner.CharacterId, QuestionQuestId) == 3,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(owner.SessionId, owner.CharacterId);
            }
        }

        private static void VerifyBranch(
            GameDatabase database, QuestCommandOwnerContext owner,
            int answerIndex, string name, ref int failures)
        {
            var repository = new QuestRepository(database.ConnectionString);
            var flags = repository.LoadClearedFlags(owner.CharacterId);
            var acceptable = QuestData.ComputeAcceptableQuests(
                86, 0, 0, new HashSet<int>(flags.Keys), flags);
            using (var connection = database.OpenConnection())
            {
                Check(name + " survives reconnect in the init completion projection",
                    QuestRepository.LoadAllFlagEntries(connection, null, owner.CharacterId)
                        .Exists(entry => entry.Key == QuestionQuestId
                            && entry.Value == answerIndex + 1),
                    ref failures);
            }
            var acceptance = new QuestAcceptanceApplicationService(database.ConnectionString);
            for (var index = 0; index < BranchQuestIds.Length; index++)
            {
                var expected = index == answerIndex;
                Check(name + $" lists branch {BranchQuestIds[index]} correctly",
                    acceptable.Contains(BranchQuestIds[index]) == expected,
                    ref failures);
                if (!expected)
                {
                    Check(name + $" rejects unrelated branch {BranchQuestIds[index]}",
                        !acceptance.Apply(owner,
                            new QuestAcceptCommand(BranchQuestIds[index])).Success,
                        ref failures);
                }
            }
            Check(name + " accepts only the chosen successor",
                acceptance.Apply(owner,
                    new QuestAcceptCommand(BranchQuestIds[answerIndex])).Success,
                ref failures);
        }

        private static void VerifyRollback(GameDatabase database, ref int failures)
        {
            var owner = PrepareChoice(database, 991933, 2);
            var repository = new QuestRepository(database.ConnectionString);
            var before = repository.LoadActiveQuests(owner.CharacterId)[0];
            try
            {
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
CREATE TRIGGER selftest_question_completion_failure
BEFORE INSERT ON character_quest_completions WHEN NEW.quest_id = 3032
BEGIN SELECT RAISE(ABORT, 'selftest question completion failure'); END;";
                    command.ExecuteNonQuery();
                }
                var service = new QuestService(database.ConnectionString);
                var commandBody = ParseFinish(ushort.MaxValue);
                var failed = service.HandleFinishQuest(owner, commandBody);
                var active = repository.LoadActiveQuests(owner.CharacterId);
                Check("completion failure rolls back the answer and activity deletion",
                    !failed.Success
                    && repository.ReadClearedFlagValue(owner.CharacterId, QuestionQuestId) == 0
                    && active.Count == 1
                    && active[0].ActivationId.Equals(before.ActivationId)
                    && active[0].Version == before.Version
                    && active[0].TriggerValue == before.TriggerValue,
                    ref failures);
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DROP TRIGGER selftest_question_completion_failure";
                    command.ExecuteNonQuery();
                }
                Check("retry after rollback persists the third answer",
                    service.HandleFinishQuest(owner, commandBody).Success
                    && repository.ReadClearedFlagValue(owner.CharacterId, QuestionQuestId) == 3,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(owner.SessionId, owner.CharacterId);
            }
        }

        private static void VerifyReplacedSession(GameDatabase database, ref int failures)
        {
            var oldOwner = PrepareChoice(database, 991934, 2);
            var owner = oldOwner;
            try
            {
                InventoryContext.Unregister(oldOwner.SessionId, oldOwner.CharacterId);
                owner = LoadOwner(database, oldOwner.CharacterId);
                var service = new QuestService(database.ConnectionString);
                var command = ParseFinish(ushort.MaxValue);
                var repository = new QuestRepository(database.ConnectionString);
                Check("old session cannot commit an answer after reconnect",
                    !service.HandleFinishQuest(oldOwner, command).Success
                    && !service.HandleSetTrigger(oldOwner, AnswerBody(0)).Success
                    && repository.ReadClearedFlagValue(owner.CharacterId, QuestionQuestId) == 0
                    && repository.LoadActiveQuests(owner.CharacterId).Count == 1,
                    ref failures);
                Check("current session completes the third branch after reconnect",
                    service.HandleFinishQuest(owner, command).Success
                    && repository.ReadClearedFlagValue(owner.CharacterId, QuestionQuestId) == 3,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(owner.SessionId, owner.CharacterId);
            }
        }

        private static void VerifyReacceptedActivation(GameDatabase database, ref int failures)
        {
            var owner = PrepareChoice(database, 991935, 2);
            try
            {
                var repository = new QuestRepository(database.ConnectionString);
                var previous = repository.LoadActiveQuests(owner.CharacterId)[0];
                var service = new QuestService(database.ConnectionString);
                if (!service.HandleGiveupQuest(owner,
                        new QuestGiveupCommand(QuestionQuestId)).Success
                    || !new QuestAcceptanceApplicationService(database.ConnectionString)
                        .Apply(owner, new QuestAcceptCommand(QuestionQuestId)).Success)
                {
                    throw new InvalidOperationException("could not reaccept question quest");
                }
                var current = repository.LoadActiveQuests(owner.CharacterId)[0];
                Check("reaccepting a question creates a different activation",
                    !current.ActivationId.Equals(previous.ActivationId), ref failures);
                var request = new QuestProgressApplicationRequest
                {
                    CharacterId = owner.CharacterId,
                    Operation = QuestProgressOperation.ClientTrigger,
                    QuestId = QuestionQuestId,
                    TriggerType = 0,
                    Increment = true,
                    QuestionAnswerIndex = 2,
                    CommandOwner = owner,
                    EligibleQuestActivations = new Dictionary<ushort, QuestActivationId>
                    {
                        [QuestionQuestId] = previous.ActivationId,
                    },
                };
                var progress = new QuestProgressApplicationService(database.ConnectionString);
                var stale = progress.Apply(request);
                var unchanged = repository.LoadActiveQuests(owner.CharacterId)[0];
                Check("an old activation cannot change the newly accepted answer",
                    stale.Success && stale.ActivationChanged && stale.QuestNotActive
                    && !stale.MatchedObjective && stale.Changes.Count == 0
                    && unchanged.ActivationId.Equals(current.ActivationId)
                    && unchanged.Version == current.Version
                    && unchanged.TriggerValue == current.TriggerValue, ref failures);

                request.EligibleQuestActivations = new Dictionary<ushort, QuestActivationId>
                {
                    [QuestionQuestId] = current.ActivationId,
                };
                Check("the current activation can select and complete the third branch",
                    service.HandleSetTrigger(owner, AnswerBody(2)).Success
                    && service.HandleFinishQuest(owner, ParseFinish(ushort.MaxValue)).Success
                    && repository.ReadClearedFlagValue(owner.CharacterId, QuestionQuestId) == 3,
                    ref failures);
                VerifyBranch(database, owner, 2, "reaccepted activation", ref failures);
            }
            finally
            {
                InventoryContext.Unregister(owner.SessionId, owner.CharacterId);
            }
        }

        private static void Check(string name, bool passed, ref int failures)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed)
                failures++;
        }
    }
}
