using System;
using System.IO;
using System.Text.Json;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class DungeonPersistentEffectSelfTest
    {
        private const int AccountId = 979200;
        private const int RecoveryCharacterId = 979201;
        private const int AtomicCharacterId = 979202;
        private const int LuckyStarCharacterId = 979203;
        private const int ConflictCharacterId = 979204;
        private const int ScoreAdjustmentCharacterId = 979205;

        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== DUNGEON_PERSISTENT_EFFECT selftest ===");
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"dungeon-persistent-effect-{Guid.NewGuid():N}.db");

            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(connectionString);

                Check(
                    "persistent outbox is part of the database baseline",
                    TableExists(
                        connectionString,
                        "dungeon_persistent_effect_outbox"));
                TestRepositoryTransitions(connectionString);
                TestForeignLeaseRecovery(connectionString);
                TestAtomicRollbackAndReplay(connectionString);
                TestScoreAdjustmentIdempotency(connectionString);
                TestLuckyStarIdempotency(connectionString);
                TestExpectedValueConflict(connectionString);
                TestUnknownPayloadsFailClosed(connectionString);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] unexpected exception: " + ex);
                _fail++;
            }
            finally
            {
                DeleteDatabaseFiles(databasePath);
            }

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void TestRepositoryTransitions(string connectionString)
        {
            var outbox = new DungeonPersistentEffectOutbox(
                connectionString,
                Guid.NewGuid());
            var effectId = NewEffectId("selftest-ledger", 1);
            var definition = new DungeonPersistentEffectDefinition
            {
                EffectId = effectId,
                CharacterId = RecoveryCharacterId,
                AccountId = AccountId,
                PayloadVersion = 1,
                PayloadJson = "{\"value\":1}",
            };

            Check("first enqueue creates pending effect", outbox.Enqueue(definition));
            Check("same effect and payload enqueue is a no-op", !outbox.Enqueue(definition));

            var conflictRejected = false;
            try
            {
                outbox.Enqueue(new DungeonPersistentEffectDefinition
                {
                    EffectId = effectId,
                    CharacterId = RecoveryCharacterId,
                    AccountId = AccountId,
                    PayloadVersion = 1,
                    PayloadJson = "{\"value\":2}",
                });
            }
            catch (InvalidOperationException)
            {
                conflictRejected = true;
            }
            Check("same EffectId with different payload is rejected", conflictRejected);

            var firstClaim = outbox.TryClaim(
                effectId,
                TimeSpan.FromMinutes(1),
                out var firstReservation,
                out _);
            var busyClaim = outbox.TryClaim(
                effectId,
                TimeSpan.FromMinutes(1),
                out _,
                out _);
            Check(
                "owned lease excludes a concurrent claimant",
                firstClaim == DungeonPersistentEffectClaimResult.Claimed
                    && busyClaim == DungeonPersistentEffectClaimResult.Busy);
            Check(
                "failed reservation becomes retryable",
                outbox.TryFail(firstReservation, "injected retryable failure")
                    && outbox.Get(effectId).State
                        == DungeonPersistentEffectState.Failed);

            var retryClaim = outbox.TryClaim(
                effectId,
                TimeSpan.FromMinutes(1),
                out var retryReservation,
                out _);
            var wrongReservation = new DungeonPersistentEffectReservation(
                effectId,
                Guid.NewGuid(),
                outbox.LeaseOwner);
            Check(
                "only the owned lease can commit",
                retryClaim == DungeonPersistentEffectClaimResult.Claimed
                    && !outbox.TryCommit(wrongReservation, 1, "{\"ok\":true}")
                    && outbox.TryCommit(
                        retryReservation,
                        1,
                        "{\"ok\":true}"));

            var rebuilt = new DungeonPersistentEffectOutbox(
                connectionString,
                Guid.NewGuid());
            var committed = rebuilt.Get(effectId);
            Check(
                "committed state and result survive repository reconstruction",
                committed.State == DungeonPersistentEffectState.Committed
                    && committed.AttemptCount == 2
                    && committed.ResultVersion == 1
                    && committed.ResultJson == "{\"ok\":true}");
        }

        private static void TestForeignLeaseRecovery(string connectionString)
        {
            var effectId = NewEffectId(
                DungeonPersistentEffectKinds.SettlementExperienceGrant,
                2);
            var payload = NewExperiencePayload(
                RecoveryCharacterId,
                previousExp: 0,
                expectedDatabaseExp: 0,
                rawGain: 100);
            var oldOutbox = new DungeonPersistentEffectOutbox(
                connectionString,
                Guid.NewGuid());
            oldOutbox.Enqueue(NewDefinition(effectId, payload));
            var claimed = oldOutbox.TryClaim(
                effectId,
                TimeSpan.FromHours(1),
                out _,
                out _);

            var newOutbox = new DungeonPersistentEffectOutbox(
                connectionString,
                Guid.NewGuid());
            var service = new DungeonPersistentEffectApplicationService(
                connectionString,
                newOutbox);
            var recovery = service.RecoverCharacter(RecoveryCharacterId);
            var record = newOutbox.Get(effectId);
            Check(
                "new process releases a foreign lease and recovers the effect",
                claimed == DungeonPersistentEffectClaimResult.Claimed
                    && recovery.CommittedCount == 1
                    && record.State == DungeonPersistentEffectState.Committed
                    && record.AttemptCount == 2
                    && ReadCharacterExp(
                        connectionString,
                        RecoveryCharacterId) == 100);

            var secondRecovery = service.RecoverCharacter(RecoveryCharacterId);
            Check(
                "recovery replay does not grant experience twice",
                secondRecovery.CommittedCount == 0
                    && ReadCharacterExp(
                        connectionString,
                        RecoveryCharacterId) == 100);
        }

        private static void TestAtomicRollbackAndReplay(string connectionString)
        {
            var service = new DungeonPersistentEffectApplicationService(
                connectionString,
                new DungeonPersistentEffectOutbox(
                    connectionString,
                    Guid.NewGuid()));
            var effectId = NewEffectId(
                DungeonPersistentEffectKinds.SettlementExperienceGrant,
                3);
            Execute(connectionString, $@"
CREATE TRIGGER fail_dungeon_effect_commit
BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox
WHEN NEW.state = {(int)DungeonPersistentEffectState.Committed}
 AND NEW.effect_kind = '{DungeonPersistentEffectKinds.SettlementExperienceGrant}'
 AND NEW.scope_target = 3
BEGIN
    SELECT RAISE(ABORT, 'injected persistent effect commit failure');
END;");

            var first = service.TryApplySettlementExperience(
                effectId,
                AtomicCharacterId,
                AccountId,
                previousLevel: 50,
                previousExp: 0,
                rawGain: 100,
                out _,
                out _);
            var failedRecord = service.Outbox.Get(effectId);
            Check(
                "business mutation rolls back when effect commit fails",
                !first
                    && ReadCharacterExp(connectionString, AtomicCharacterId) == 0
                    && failedRecord.State == DungeonPersistentEffectState.Failed);

            Execute(connectionString, "DROP TRIGGER fail_dungeon_effect_commit;");
            var retry = service.TryApplySettlementExperience(
                effectId,
                AtomicCharacterId,
                AccountId,
                previousLevel: 50,
                previousExp: 0,
                rawGain: 100,
                out var retryResult,
                out var retryError);
            var afterRetry = ReadCharacterExp(connectionString, AtomicCharacterId);
            var replay = service.TryApplySettlementExperience(
                effectId,
                AtomicCharacterId,
                AccountId,
                previousLevel: 50,
                previousExp: 0,
                rawGain: 100,
                out var replayResult,
                out var replayError);
            Check(
                "failed experience effect retries once and committed replay is no-op",
                retry
                    && replay
                    && retryError == null
                    && replayError == null
                    && retryResult.NewExp == 100
                    && replayResult.NewExp == 100
                    && afterRetry == 100
                    && ReadCharacterExp(
                        connectionString,
                        AtomicCharacterId) == 100
                    && service.Outbox.Get(effectId).AttemptCount == 2);
        }

        private static void TestLuckyStarIdempotency(string connectionString)
        {
            var service = new DungeonPersistentEffectApplicationService(
                connectionString,
                new DungeonPersistentEffectOutbox(
                    connectionString,
                    Guid.NewGuid()));
            var effectId = NewEffectId(
                DungeonPersistentEffectKinds.SuitableDungeonLuckyStar,
                4);
            var first = service.TryApplySuitableDungeonLuckyStar(
                effectId,
                LuckyStarCharacterId,
                AccountId,
                dungeonId: 123,
                clearLevel: 50,
                out var firstResult,
                out var firstError);
            var replay = service.TryApplySuitableDungeonLuckyStar(
                effectId,
                LuckyStarCharacterId,
                AccountId,
                dungeonId: 123,
                clearLevel: 50,
                out var replayResult,
                out var replayError);
            Check(
                "lucky-star effect commits once and replay returns stored result",
                first
                    && replay
                    && firstError == null
                    && replayError == null
                    && firstResult.Granted
                    && replayResult.Granted
                    && firstResult.NewTotal == replayResult.NewTotal
                    && ReadLong(
                        connectionString,
                        $"SELECT lucky_star FROM accounts " +
                        $"WHERE account_id = {AccountId};") == 1);
        }

        private static void TestScoreAdjustmentIdempotency(
            string connectionString)
        {
            var service = new DungeonPersistentEffectApplicationService(
                connectionString,
                new DungeonPersistentEffectOutbox(
                    connectionString,
                    Guid.NewGuid()));
            var effectId = NewEffectId(
                DungeonPersistentEffectKinds.SettlementScoreExperienceAdjustment,
                8);
            var first = service.TryApplySettlementScoreExperienceAdjustment(
                effectId,
                ScoreAdjustmentCharacterId,
                AccountId,
                previousLevel: 50,
                previousExp: 0,
                rawGain: 25,
                out var firstResult,
                out var firstError);
            var replay = service.TryApplySettlementScoreExperienceAdjustment(
                effectId,
                ScoreAdjustmentCharacterId,
                AccountId,
                previousLevel: 50,
                previousExp: 0,
                rawGain: 25,
                out var replayResult,
                out var replayError);
            Check(
                "late score experience adjustment is durable and idempotent",
                first
                    && replay
                    && firstError == null
                    && replayError == null
                    && firstResult.NewExp == 25
                    && replayResult.NewExp == 25
                    && ReadCharacterExp(
                        connectionString,
                        ScoreAdjustmentCharacterId) == 25);
        }

        private static void TestExpectedValueConflict(string connectionString)
        {
            var effectId = NewEffectId(
                DungeonPersistentEffectKinds.SettlementExperienceGrant,
                5);
            var payload = NewExperiencePayload(
                ConflictCharacterId,
                previousExp: 0,
                expectedDatabaseExp: 0,
                rawGain: 100);
            var outbox = new DungeonPersistentEffectOutbox(
                connectionString,
                Guid.NewGuid());
            outbox.Enqueue(NewDefinition(effectId, payload));
            Execute(
                connectionString,
                $"UPDATE characters SET exp = 5 " +
                $"WHERE character_id = {ConflictCharacterId};");
            var service = new DungeonPersistentEffectApplicationService(
                connectionString,
                outbox);
            var recovery = service.RecoverCharacter(ConflictCharacterId);
            Check(
                "stale experience payload fails closed instead of overwriting progress",
                recovery.FailedCount == 1
                    && outbox.Get(effectId).State
                        == DungeonPersistentEffectState.DeadLetter
                    && ReadCharacterExp(
                        connectionString,
                        ConflictCharacterId) == 5);
        }

        private static void TestUnknownPayloadsFailClosed(string connectionString)
        {
            var outbox = new DungeonPersistentEffectOutbox(
                connectionString,
                Guid.NewGuid());
            var unknownKindId = NewEffectId("unknown-effect-kind", 6);
            outbox.Enqueue(new DungeonPersistentEffectDefinition
            {
                EffectId = unknownKindId,
                CharacterId = ConflictCharacterId,
                AccountId = AccountId,
                PayloadVersion = 1,
                PayloadJson = "{}",
            });
            var unknownVersionId = NewEffectId(
                DungeonPersistentEffectKinds.SettlementExperienceGrant,
                7);
            outbox.Enqueue(new DungeonPersistentEffectDefinition
            {
                EffectId = unknownVersionId,
                CharacterId = ConflictCharacterId,
                AccountId = AccountId,
                PayloadVersion = 99,
                PayloadJson = "{}",
            });
            var service = new DungeonPersistentEffectApplicationService(
                connectionString,
                outbox);
            var recovery = service.RecoverCharacter(ConflictCharacterId);
            Check(
                "unknown kind and payload version are dead-lettered without mutation",
                recovery.DeadLetterCount == 1
                    && recovery.FailedCount == 1
                    && outbox.Get(unknownKindId).State
                        == DungeonPersistentEffectState.DeadLetter
                    && outbox.Get(unknownVersionId).State
                        == DungeonPersistentEffectState.DeadLetter
                    && ReadCharacterExp(
                        connectionString,
                        ConflictCharacterId) == 5);
        }

        private static SettlementExperienceEffectPayload NewExperiencePayload(
            int characterId,
            uint previousExp,
            uint expectedDatabaseExp,
            uint rawGain)
            => new SettlementExperienceEffectPayload
            {
                CharacterId = characterId,
                AccountId = AccountId,
                PreviousLevel = 50,
                PreviousExp = previousExp,
                RawGain = rawGain,
                NormalizeMaxLevelExp = true,
                ExpectedDatabaseLevel = 50,
                ExpectedDatabaseExp = expectedDatabaseExp,
            };

        private static DungeonPersistentEffectDefinition NewDefinition(
            DungeonEffectId effectId,
            SettlementExperienceEffectPayload payload)
            => new DungeonPersistentEffectDefinition
            {
                EffectId = effectId,
                CharacterId = payload.CharacterId,
                AccountId = payload.AccountId,
                PayloadVersion = 1,
                PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            };

        private static DungeonEffectId NewEffectId(string kind, long target)
            => new DungeonEffectId(
                Guid.NewGuid(),
                kind,
                DungeonEffectScope.Player,
                target);

        private static void Seed(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, 'dungeon-effect-selftest', '');
INSERT INTO characters(
    character_id, account_id, name, job, grow_type, level, exp)
VALUES
    (@recoveryId, @accountId, 'EffectRecovery', 0, 0, 50, 0),
    (@atomicId, @accountId, 'EffectAtomic', 0, 0, 50, 0),
    (@luckyId, @accountId, 'EffectLucky', 0, 0, 50, 0),
    (@conflictId, @accountId, 'EffectConflict', 0, 0, 50, 0),
    (@scoreAdjustmentId, @accountId, 'EffectScoreAdjustment', 0, 0, 50, 0);
INSERT INTO character_subtype1_fields(character_id)
VALUES(@recoveryId), (@atomicId), (@luckyId), (@conflictId),
      (@scoreAdjustmentId);";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue(
                        "@recoveryId",
                        RecoveryCharacterId);
                    command.Parameters.AddWithValue(
                        "@atomicId",
                        AtomicCharacterId);
                    command.Parameters.AddWithValue(
                        "@luckyId",
                        LuckyStarCharacterId);
                    command.Parameters.AddWithValue(
                        "@conflictId",
                        ConflictCharacterId);
                    command.Parameters.AddWithValue(
                        "@scoreAdjustmentId",
                        ScoreAdjustmentCharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static uint ReadCharacterExp(
            string connectionString,
            int characterId)
            => (uint)ReadLong(
                connectionString,
                $"SELECT exp FROM characters WHERE character_id = {characterId};");

        private static long ReadLong(string connectionString, string sql)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private static bool TableExists(
            string connectionString,
            string tableName)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*) FROM sqlite_master
WHERE type = 'table' AND name = @name;";
                    command.Parameters.AddWithValue("@name", tableName);
                    return Convert.ToInt32(command.ExecuteScalar()) == 1;
                }
            }
        }

        private static void Execute(string connectionString, string sql)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void Check(string name, bool passed)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (passed)
                _pass++;
            else
                _fail++;
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var path in new[]
                     {
                         databasePath,
                         databasePath + "-wal",
                         databasePath + "-shm",
                     })
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
}
