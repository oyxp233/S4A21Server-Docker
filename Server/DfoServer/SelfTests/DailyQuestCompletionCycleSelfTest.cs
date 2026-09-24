using System;
using System.IO;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.SelfTests
{
    public static class DailyQuestCompletionCycleSelfTest
    {
        private const int DailyQuestId = 2683;
        private const int OrdinaryQuestId = 2680;
        private const int CurrentCharacterId = 991802;
        private const int BoundaryCharacterId = 991803;
        private const int RollbackCharacterId = 991804;
        private const int FinishCharacterId = 991805;

        public static int Run()
        {
            Console.WriteLine("=== DAILY_QUEST_COMPLETION_CYCLE selftest ===");
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "s4a21-daily-quest-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                Check("three daily grades are classified together",
                    QuestData.IsDailyGrade("[daily]")
                    && QuestData.IsDailyGrade("[daily random]")
                    && QuestData.IsDailyGrade("[special daily]")
                    && !QuestData.IsDailyGrade("[normaly repeat]"),
                    ref failures);
                Check("immediate repeat is separate",
                    QuestData.IsImmediatelyRepeatableGrade("[normaly repeat]")
                    && !QuestData.IsImmediatelyRepeatableGrade("[daily]"),
                    ref failures);
                Check("PVF quest 2683 is daily and single-completion",
                    QuestData.IsDailyQuest(DailyQuestId)
                    && QuestData.TryResolveCompletionDefinition(
                        DailyQuestId, out var definition, out _)
                    && !definition.SupportsBatchCompletion,
                    ref failures);
                var repeatSeekingSupportsBatch = false;
                foreach (var questId in QuestCatalog.OrderedIds)
                {
                    if (QuestData.IsImmediatelyRepeatableQuest(questId)
                        && QuestData.TryResolveCompletionDefinition(
                            questId, out var repeatDefinition, out _)
                        && repeatDefinition.SupportsBatchCompletion)
                    {
                        repeatSeekingSupportsBatch = true;
                        break;
                    }
                }
                Check("ordinary repeatable seeking quest retains batch completion",
                    repeatSeekingSupportsBatch,
                    ref failures);

                var database = new GameDatabase(
                    databasePath, ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (991801, 'daily-quest-cycle-test', '');
INSERT INTO characters (character_id, account_id, name, job, level)
VALUES
  (991802, 991801, 'daily-current', 0, 86),
  (991803, 991801, 'daily-boundary', 0, 86),
  (991804, 991801, 'daily-rollback', 0, 86),
  (991805, 991801, 'daily-finish', 0, 86);";
                    command.ExecuteNonQuery();
                }

                var now = DateTime.UtcNow;
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var first = DailyQuestCompletionCycle.TryClaim(
                        connection, transaction, CurrentCharacterId,
                        DailyQuestId, now);
                    var duplicate = DailyQuestCompletionCycle.TryClaim(
                        connection, transaction, CurrentCharacterId,
                        DailyQuestId, now);
                    Check("same-day claim is atomic and capped at one",
                        first && !duplicate
                        && DailyQuestCompletionCycle.GetCurrentCounter(
                            connection, transaction, CurrentCharacterId,
                            DailyQuestId, now) == 1,
                        ref failures);
                    QuestRepository.MarkQuestCleared(
                        connection, transaction, CurrentCharacterId,
                        DailyQuestId);
                    QuestRepository.MarkQuestCleared(
                        connection, transaction, CurrentCharacterId,
                        OrdinaryQuestId);
                    transaction.Commit();
                }

                var repository = new QuestRepository(database.ConnectionString);
                var cleared = repository.LoadClearedFlags(CurrentCharacterId);
                Check("current daily completion and ordinary completion project",
                    cleared.ContainsKey(DailyQuestId)
                    && cleared.ContainsKey(OrdinaryQuestId)
                    && repository.ReadClearedFlagValue(
                        CurrentCharacterId, DailyQuestId) == 1,
                    ref failures);

                var sessionId = Guid.NewGuid();
                using (var connection = database.OpenConnection())
                {
                    var inventory = InventoryService.LoadFromDb(
                        connection, CurrentCharacterId, 991801, database);
                    var lease = InventoryContext.Register(
                        sessionId, CurrentCharacterId, inventory);
                    try
                    {
                        var owner = new QuestCommandOwnerContext(
                            CurrentCharacterId, 991801, sessionId, lease);
                        var result = new QuestService(database.ConnectionString)
                            .HandleAcceptQuest(
                                owner, new QuestAcceptCommand(DailyQuestId));
                        Check("claimed daily quest is rejected by ACCEPT",
                            !result.Success && result.ErrorCode == 18,
                            ref failures);
                    }
                    finally
                    {
                        InventoryContext.Unregister(
                            sessionId, CurrentCharacterId);
                    }
                }

                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    Check("next-day ledger rollover clears the claim",
                        DailyQuestCompletionCycle.GetCurrentCounter(
                            connection, transaction, CurrentCharacterId,
                            DailyQuestId, now.AddDays(1)) == 0,
                        ref failures);
                    transaction.Commit();
                }
                cleared = repository.LoadClearedFlags(CurrentCharacterId);
                using (var connection = database.OpenConnection())
                {
                    var initEntries = QuestRepository.LoadAllFlagEntries(
                        connection, null, CurrentCharacterId);
                    Check("expired daily projection is hidden on list and init reads",
                        !cleared.ContainsKey(DailyQuestId)
                        && cleared.ContainsKey(OrdinaryQuestId)
                        && repository.ReadClearedFlagValue(
                            CurrentCharacterId, DailyQuestId) == 0
                        && !initEntries.Exists(entry => entry.Key == DailyQuestId),
                        ref failures);
                }

                var before = new DateTime(
                    2026, 8, 20, 21, 59, 59, DateTimeKind.Utc);
                var atBoundary = new DateTime(
                    2026, 8, 20, 22, 0, 0, DateTimeKind.Utc);
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var first = DailyQuestCompletionCycle.TryClaim(
                        connection, transaction, BoundaryCharacterId,
                        DailyQuestId, before);
                    var stillClaimed = DailyQuestCompletionCycle.IsClaimedToday(
                        connection, transaction, BoundaryCharacterId,
                        DailyQuestId, before);
                    var duplicate = DailyQuestCompletionCycle.TryClaim(
                        connection, transaction, BoundaryCharacterId,
                        DailyQuestId, before);
                    var nextDay = DailyQuestCompletionCycle.GetCurrentCounter(
                        connection, transaction, BoundaryCharacterId,
                        DailyQuestId, atBoundary);
                    var secondDayClaim = DailyQuestCompletionCycle.TryClaim(
                        connection, transaction, BoundaryCharacterId,
                        DailyQuestId, atBoundary);
                    var staleRetry = DailyQuestCompletionCycle.TryClaim(
                        connection, transaction, BoundaryCharacterId,
                        DailyQuestId, before);
                    Check("05:59:59 stays locked and 06:00:00 reopens",
                        first && stillClaimed && !duplicate
                        && nextDay == 0 && secondDayClaim && !staleRetry,
                        ref failures);
                    transaction.Commit();
                }

                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    Check("uncommitted claim succeeds before rollback",
                        DailyQuestCompletionCycle.TryClaim(
                            connection, transaction, RollbackCharacterId,
                            DailyQuestId, now),
                        ref failures);
                    transaction.Rollback();
                }
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    Check("rolled-back claim can be retried",
                        DailyQuestCompletionCycle.TryClaim(
                            connection, transaction, RollbackCharacterId,
                            DailyQuestId, now),
                        ref failures);
                    transaction.Commit();
                }

                VerifyRealFinish(database, ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL daily quest cycle exception: " + ex);
                failures++;
            }
            finally
            {
                foreach (var path in new[]
                {
                    databasePath, databasePath + "-wal", databasePath + "-shm"
                })
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            Console.WriteLine(failures == 0
                ? "DAILY_QUEST_COMPLETION_CYCLE selftest passed"
                : $"DAILY_QUEST_COMPLETION_CYCLE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyRealFinish(
            GameDatabase database,
            ref int failures)
        {
            var sessionId = Guid.NewGuid();
            InventoryService inventory;
            using (var connection = database.OpenConnection())
            {
                inventory = InventoryService.LoadFromDb(
                    connection, FinishCharacterId, 991801, database);
            }
            var materialIds = new[] { 10157245, 10157246, 10157247, 10157248 };
            for (var index = 0; index < materialIds.Length; index++)
            {
                inventory.SetItem(
                    InventoryListType.Main,
                    (short)(177 + index),
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindQuest,
                        ItemId = materialIds[index],
                        Count = 1,
                    });
            }
            var lease = InventoryContext.Register(
                sessionId, FinishCharacterId, inventory);
            try
            {
                if (!OnlineInventoryMutationCommitCoordinator.TryCommit(
                        lease, "selftest-daily-quest-materials"))
                {
                    throw new InvalidOperationException(
                        "could not persist daily quest test materials");
                }
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    QuestRepository.InsertActiveQuest(
                        connection, transaction, FinishCharacterId,
                        0, DailyQuestId, 0);
                    transaction.Commit();
                }
                var owner = new QuestCommandOwnerContext(
                    FinishCharacterId, 991801, sessionId, lease);
                var service = new QuestService(database.ConnectionString);
                var command = new QuestFinishCommand(
                    DailyQuestId, false, 0, 1);
                var first = service.HandleFinishQuest(owner, command);
                var replay = service.HandleFinishQuest(owner, command);
                Check("real FINISH rewards once and rejects replay",
                    first.Success && !replay.Success
                    && inventory.CountMainItem(10093974) == 1
                    && new QuestRepository(database.ConnectionString)
                        .IsQuestCleared(FinishCharacterId, DailyQuestId),
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, FinishCharacterId);
            }
        }

        private static void Check(string name, bool passed, ref int failures)
        {
            Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
            if (!passed)
                failures++;
        }
    }
}
