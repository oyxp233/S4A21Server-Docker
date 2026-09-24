using DfoServer.Game.Quests;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    internal static class QuestProgressBatchSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo-quest-progress-batch-{Guid.NewGuid():N}.db");
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
    character_id INTEGER NOT NULL,
    slot INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    trigger_value INTEGER NOT NULL,
    version INTEGER NOT NULL DEFAULT 0,
    activation_id TEXT NOT NULL,
    PRIMARY KEY (character_id, slot),
    UNIQUE (character_id, quest_id)
);";
                        command.ExecuteNonQuery();
                    }
                }

                var service = new QuestProgressApplicationService(
                    connectionString);
                var sourceEventId = Guid.NewGuid();
                var result = service.ApplyBatch(new[]
                {
                    new QuestProgressApplicationRequest
                    {
                        CharacterId = 1,
                        Operation = QuestProgressOperation.HuntMonster,
                        SourceEventId = sourceEventId,
                        DungeonId = 1,
                        MonsterCode = 100,
                        MonsterType = 0,
                    },
                    new QuestProgressApplicationRequest
                    {
                        CharacterId = 1,
                        Operation = QuestProgressOperation.HuntEnemy,
                        SourceEventId = sourceEventId,
                        DungeonId = 1,
                        MonsterCode = 100,
                        EnemyType = 1,
                    },
                });
                Check(
                    "one actor death applies both quest projections as one batch",
                    result.Success && result.Results.Count == 2,
                    ref failures);

                var mixedOwner = service.ApplyBatch(new[]
                {
                    new QuestProgressApplicationRequest
                    {
                        CharacterId = 1,
                        Operation = QuestProgressOperation.HuntMonster,
                        DungeonId = 1,
                        MonsterCode = 100,
                    },
                    new QuestProgressApplicationRequest
                    {
                        CharacterId = 2,
                        Operation = QuestProgressOperation.HuntEnemy,
                        DungeonId = 1,
                        MonsterCode = 100,
                        EnemyType = 1,
                    },
                });
                Check(
                    "quest progress batch rejects mixed character owners",
                    !mixedOwner.Success,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"quest progress batch check failed: {ex.Message}");
                failures++;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }

            Console.WriteLine(
                failures == 0
                    ? "QUEST_PROGRESS_BATCH selftest passed."
                    : $"QUEST_PROGRESS_BATCH selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
