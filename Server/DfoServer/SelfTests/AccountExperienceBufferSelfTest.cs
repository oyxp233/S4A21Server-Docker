using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DfoServer.SelfTests
{
    // 账号级荣誉/成长胶囊经验的"内存累计 + 盲增量落库"。
    // 用临时独立库, 库/内存两侧状态都在用例之间显式隔离:
    //   AccountExperienceBuffer.ResetForTests() 清内存, 每个用例重置 DB 基线。
    public static class AccountExperienceBufferSelfTest
    {
        private const int AccountId = 941017;
        private const int RepoAccountId = 941018;
        private const int CharacterId = 941117;
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== ACCOUNT_EXPERIENCE_BUFFER selftest ===");

            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "account_experience_buffer_" + Guid.NewGuid().ToString("N") + ".db");
            var connectionString = string.Empty;
            try
            {
                connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath, ServerPaths.SchemaFilePath);
                Seed(connectionString);
                AccountExperienceBuffer.ResetForTests();

                VerifyColdLoadAndMemoryAccumulation(connectionString);
                VerifyCapsMatchRepositoryPath(connectionString);
                VerifyFlushPersistsAndClearsPending(connectionString);
                VerifyDirectWritePathKeepsPendingThroughRollback(connectionString);
                VerifyCapsuleClaimUsesPending(connectionString);
                VerifyLevelUpFlushesAccountPending(connectionString);
                VerifyConcurrentAddAndFlush(connectionString);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ACCOUNT_EXPERIENCE_BUFFER FAILED: " + ex);
                _failures++;
            }
            finally
            {
                AccountExperienceBuffer.ResetForTests();
                SqliteConnection.ClearAllPools();
                DeleteDatabase(databasePath);
            }

            Console.WriteLine(_failures == 0
                ? "ACCOUNT_EXPERIENCE_BUFFER selftest passed"
                : $"ACCOUNT_EXPERIENCE_BUFFER selftest failed: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        // 冷账号先读一次 DB 基线, 之后只累计内存; Add 返回含 pending 的有效总值。
        private static void VerifyColdLoadAndMemoryAccumulation(string connectionString)
        {
            AccountExperienceBuffer.ResetForTests();
            SetAccountExp(connectionString, AccountId, 1000, 200);

            var totals = AccountExperienceBuffer.Instance.Add(
                AccountId, 30, connectionString);
            Check(
                "cold add loads the db baseline and accumulates in memory",
                totals.TotalHonorExp == 1030
                    && totals.TotalGrowthCapsuleExp == 230
                    && totals.GrowthCapsuleExpGain == 30,
                ref _failures);
            Check(
                "cold add does not write the database before flush",
                LoadHonor(connectionString, AccountId) == 1000
                    && LoadCapsule(connectionString, AccountId) == 200,
                ref _failures);

            var effective = AccountExperienceBuffer.Instance
                .GetEffectiveTotals(AccountId, connectionString);
            Check(
                "effective totals include pending without dirtying",
                effective.TotalHonorExp == 1030
                    && effective.TotalGrowthCapsuleExp == 230,
                ref _failures);

            SetAccountExp(connectionString, AccountId, 777, 88);
            AccountExperienceBuffer.ResetForTests();
            var reloaded = AccountExperienceBuffer.Instance
                .GetEffectiveTotals(AccountId, connectionString);
            Check(
                "read-only load reports the persisted values",
                reloaded.TotalHonorExp == 777
                    && reloaded.TotalGrowthCapsuleExp == 88
                    && AccountExperienceBuffer.Instance.Flush(AccountId)
                    && LoadHonor(connectionString, AccountId) == 777
                    && LoadCapsule(connectionString, AccountId) == 88,
                ref _failures);

            var invalid = AccountExperienceBuffer.Instance.Add(0, 30, connectionString);
            Check(
                "add ignores non-positive accounts",
                invalid.TotalHonorExp == 0 && invalid.TotalGrowthCapsuleExp == 0,
                ref _failures);
        }

        // cap 截断语义必须与既有的读-改-写直写路径完全一致。
        private static void VerifyCapsMatchRepositoryPath(string connectionString)
        {
            var honorCap = HonorLevelDataProvider.MaxTotalHonorExp;
            var capsuleCap = GrowthCapsuleDataProvider.RequiredExpPerCapsule;
            Check(
                "pvf provides non-zero caps",
                honorCap > 1000 && capsuleCap > 1000,
                ref _failures);

            AccountExperienceBuffer.ResetForTests();
            SetAccountExp(connectionString, AccountId, honorCap - 10, capsuleCap - 19);
            SetAccountExp(connectionString, RepoAccountId, honorCap - 10, capsuleCap - 19);

            var buffered = AccountExperienceBuffer.Instance.Add(
                AccountId, 321, connectionString);

            ulong repoHonor;
            uint repoCapsule;
            uint repoApplied;
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    repoHonor = HonorLevelProgressRepository.AddHonorExpInTransaction(
                        connection, transaction, RepoAccountId, 321);
                    repoCapsule = GrowthCapsuleProgressRepository.AddExpInTransaction(
                        connection,
                        transaction,
                        RepoAccountId,
                        GrowthCapsuleDataProvider.CalculateExpGain(321),
                        out repoApplied);
                    transaction.Commit();
                }
            }

            Check(
                "honor cap truncation matches the repository path",
                buffered.TotalHonorExp == repoHonor && repoHonor == honorCap,
                ref _failures);
            Check(
                "capsule cap truncation matches the repository path",
                buffered.TotalGrowthCapsuleExp == repoCapsule
                    && buffered.GrowthCapsuleExpGain == repoApplied
                    && repoCapsule == capsuleCap
                    && repoApplied == 19,
                ref _failures);
        }

        private static void VerifyFlushPersistsAndClearsPending(string connectionString)
        {
            AccountExperienceBuffer.ResetForTests();
            SetAccountExp(connectionString, AccountId, 1000, 200);
            AccountExperienceBuffer.Instance.Add(AccountId, 30, connectionString);
            AccountExperienceBuffer.Instance.Add(AccountId, 40, connectionString);

            Check(
                "flush writes the accumulated pending",
                AccountExperienceBuffer.Instance.Flush(AccountId)
                    && LoadHonor(connectionString, AccountId) == 1070
                    && LoadCapsule(connectionString, AccountId) == 270,
                ref _failures);

            var effective = AccountExperienceBuffer.Instance
                .GetEffectiveTotals(AccountId, connectionString);
            var afterFlush = AccountExperienceBuffer.Instance.Flush(AccountId);
            Check(
                "flush clears pending and a repeat flush is a no-op",
                effective.TotalHonorExp == 1070
                    && effective.TotalGrowthCapsuleExp == 270
                    && afterFlush
                    && LoadHonor(connectionString, AccountId) == 1070
                    && LoadCapsule(connectionString, AccountId) == 270,
                ref _failures);

            // 外部直写(如 GM 工具)后, 没有 pending 的 flush 不得回退成旧值。
            SetAccountExp(connectionString, AccountId, 500, 60);
            Check(
                "flush without pending leaves external writes alone",
                AccountExperienceBuffer.Instance.Flush(AccountId)
                    && LoadHonor(connectionString, AccountId) == 500
                    && LoadCapsule(connectionString, AccountId) == 60,
                ref _failures);
        }

        // 事务内直写路径: 返回值含 pending(read-your-writes), 且 pending 不随调用方事务回滚丢失。
        private static void VerifyDirectWritePathKeepsPendingThroughRollback(string connectionString)
        {
            AccountExperienceBuffer.ResetForTests();
            SetAccountExp(connectionString, AccountId, 1000, 200);
            AccountExperienceBuffer.Instance.Add(AccountId, 50, connectionString);

            AccountExperienceProgressTotals committed;
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    committed = AccountExperienceProgressService.AddInTransaction(
                        connection, transaction, AccountId, 40);
                    transaction.Commit();
                }
            }

            Check(
                "in-transaction direct write reports pending-aware totals",
                committed.TotalHonorExp == 1090
                    && committed.TotalGrowthCapsuleExp == 290
                    && committed.GrowthCapsuleExpGain == 40,
                ref _failures);
            Check(
                "in-transaction direct write persists only its own delta",
                LoadHonor(connectionString, AccountId) == 1040
                    && LoadCapsule(connectionString, AccountId) == 240,
                ref _failures);

            Check(
                "later flush merges the still-pending kill exp",
                AccountExperienceBuffer.Instance.Flush(AccountId)
                    && LoadHonor(connectionString, AccountId) == 1090
                    && LoadCapsule(connectionString, AccountId) == 290,
                ref _failures);

            // 回滚: 直写增量随事务消失, pending 必须原样保留(不能被消费掉)。
            AccountExperienceBuffer.ResetForTests();
            SetAccountExp(connectionString, AccountId, 1000, 200);
            AccountExperienceBuffer.Instance.Add(AccountId, 50, connectionString);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    AccountExperienceProgressService.AddInTransaction(
                        connection, transaction, AccountId, 40);
                    transaction.Rollback();
                }
            }

            var afterRollback = AccountExperienceBuffer.Instance
                .GetEffectiveTotals(AccountId, connectionString);
            // 回滚只作用于调用方事务: 库值必须原样, pending 必须还在(不能因为"直写过"
            // 就被消费掉)。Known 会乐观地记下直写值, 由下一次 flush 回读自校正 —
            // 只影响展示口径, 不影响落库, 也因此这里不断言精确值。
            Check(
                "rolled back direct write leaves the database and pending intact",
                LoadHonor(connectionString, AccountId) == 1000
                    && LoadCapsule(connectionString, AccountId) == 200
                    && afterRollback.TotalHonorExp >= 1050
                    && afterRollback.TotalGrowthCapsuleExp >= 250,
                ref _failures);
            Check(
                "flush after rollback persists the surviving pending",
                AccountExperienceBuffer.Instance.Flush(AccountId)
                    && LoadHonor(connectionString, AccountId) == 1050
                    && LoadCapsule(connectionString, AccountId) == 250,
                ref _failures);
        }

        // 领取判定/展示按含 pending 的有效值; 领取成功后 pending 不得把胶囊重新填满。
        private static void VerifyCapsuleClaimUsesPending(string connectionString)
        {
            var capsuleCap = GrowthCapsuleDataProvider.RequiredExpPerCapsule;
            var sessionId = Guid.NewGuid();
            // 租约注册/注销可能在替换租约前落盘, 这里显式把背包与领取服务绑到临时库,
            // 不允许落到 ServerPaths.DatabasePath(线上存档)。
            var database = GameDatabase.AttachInitialized(connectionString);
            var inventory = new InventoryService(CharacterId, AccountId, database);
            var lease = InventoryContext.Register(sessionId, CharacterId, inventory);
            try
            {
                VerifyCapsuleClaimUsesPending(
                    connectionString, capsuleCap, lease, inventory, database);
            }
            finally
            {
                InventoryContext.Unregister(sessionId);
            }
        }

        private static void VerifyCapsuleClaimUsesPending(
            string connectionString,
            uint capsuleCap,
            InventoryLease lease,
            InventoryService inventory,
            IGameDatabase database)
        {
            var claimService = new GrowthCapsuleClaimService(database);

            // 落库值差 50, pending 补齐 -> 满格可领。
            AccountExperienceBuffer.ResetForTests();
            SetAccountExp(connectionString, AccountId, 1000, capsuleCap - 50);
            AccountExperienceBuffer.Instance.Add(AccountId, 50, connectionString);
            var claimable = claimService.Claim(lease);
            Check(
                "pending completes the capsule so the claim succeeds",
                claimable.Success
                    && inventory.CountMainItem(GrowthCapsuleDataProvider.RewardItemId) == 1
                    && LoadCapsule(connectionString, AccountId) == 0,
                ref _failures);
            Check(
                "claimed progress is not re-filled by a later flush",
                AccountExperienceBuffer.Instance.Flush(AccountId)
                    && LoadCapsule(connectionString, AccountId) == 0
                    && AccountExperienceBuffer.Instance
                        .GetEffectiveTotals(AccountId, connectionString)
                        .TotalGrowthCapsuleExp == 0,
                ref _failures);

            // pending 不足 -> 判不足, 且 pending 不被消费(下一次 flush 仍会落库)。
            AccountExperienceBuffer.ResetForTests();
            SetAccountExp(connectionString, AccountId, 1000, capsuleCap - 50);
            AccountExperienceBuffer.Instance.Add(AccountId, 10, connectionString);
            var insufficient = claimService.Claim(lease);
            Check(
                "insufficient pending still rejects the claim",
                insufficient.Status == GrowthCapsuleClaimStatus.InsufficientExp
                    && insufficient.Summary.TotalExp == capsuleCap - 40,
                ref _failures);
            Check(
                "rejected claim keeps pending for the next flush",
                AccountExperienceBuffer.Instance.Flush(AccountId)
                    && LoadCapsule(connectionString, AccountId) == capsuleCap - 40,
                ref _failures);
        }

        // 升级点把账号 pending 一并落库: 升级紧随其后的 USERINFO1/subtype0 广播仍直接读库
        // (不在副本内 read-your-writes 的改造范围内), 不落库就会出现 EXP 包是新值、
        // 随后的广播把客户端展示回退成旧值。验的是 CharacterExperienceService.Grant 里的 flush 挂点。
        private static void VerifyLevelUpFlushesAccountPending(string connectionString)
        {
            var previousLevel = (byte)(ExpTableProvider.MaxLevel - 1);
            var previousExp = (uint)Math.Max(
                0, ExpTableProvider.GetLevelThreshold(previousLevel) - 10);
            const uint RawGain = 100;

            var database = GameDatabase.AttachInitialized(connectionString);
            var accountExperience = new AccountExperienceProgressService(
                new SqliteCharacterRepository(database), database);
            var experience = new CharacterExperienceService(
                accountExperience, database);

            AccountExperienceBuffer.ResetForTests();
            SetAccountExp(connectionString, AccountId, 1000, 0);
            var player = new Game.Session.PlayerContext
            {
                CharacterId = CharacterId,
                Level = previousLevel,
                Exp = previousExp,
            };

            var grant = experience.Grant(
                player,
                AccountId,
                RawGain,
                Game.Progression.ExperiencePersistMode.OnLevelUpOnly,
                "selftest-level-up");

            Check(
                "level-up grant flushes the account pending before follow-up broadcasts",
                grant.LeveledUp
                    && grant.HonorExpGain > 0
                    && grant.Honor.TotalHonorExp == 1000 + grant.HonorExpGain
                    && LoadHonor(connectionString, AccountId)
                        == 1000 + grant.HonorExpGain
                    && player.Level == ExpTableProvider.MaxLevel,
                ref _failures);
        }

        // 盲增量必须可组合: 并发 Add 与并发 Flush 之后, 库值 = 基线 + 全部增量。
        private static void VerifyConcurrentAddAndFlush(string connectionString)
        {
            const int ThreadCount = 4;
            const int AddsPerThread = 250;
            const uint GainPerAdd = 10;

            AccountExperienceBuffer.ResetForTests();
            SetAccountExp(connectionString, AccountId, 0, 0);

            var stop = false;
            var flusher = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                    AccountExperienceBuffer.Instance.Flush(AccountId);
            });
            flusher.Start();

            var workers = new List<Thread>();
            for (var index = 0; index < ThreadCount; index++)
            {
                workers.Add(new Thread(() =>
                {
                    for (var add = 0; add < AddsPerThread; add++)
                    {
                        AccountExperienceBuffer.Instance.Add(
                            AccountId, GainPerAdd, connectionString);
                    }
                }));
            }

            foreach (var worker in workers)
                worker.Start();
            foreach (var worker in workers)
                worker.Join();

            Volatile.Write(ref stop, true);
            flusher.Join();

            var expectedHonor = (ulong)ThreadCount * AddsPerThread * GainPerAdd;
            var flushed = AccountExperienceBuffer.Instance.FlushAll();
            var effective = AccountExperienceBuffer.Instance
                .GetEffectiveTotals(AccountId, connectionString);
            Check(
                "concurrent add and flush converge to baseline plus all gains",
                flushed
                    && LoadHonor(connectionString, AccountId) == expectedHonor
                    && LoadCapsule(connectionString, AccountId) == expectedHonor
                    && effective.TotalHonorExp == expectedHonor
                    && effective.TotalGrowthCapsuleExp == expectedHonor,
                ref _failures);
        }

        private static void Seed(string connectionString)
        {
            // 角色停在满级门槛前, 供升级落库的用例使用。
            var previousLevel = ExpTableProvider.MaxLevel - 1;
            var previousExp = Math.Max(
                0, ExpTableProvider.GetLevelThreshold(previousLevel) - 10);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id) VALUES(@aid, 'account_exp_buffer_a');
INSERT INTO accounts(account_id, m_id) VALUES(@otherAid, 'account_exp_buffer_b');
INSERT INTO characters(character_id, account_id, name, job, level, exp)
VALUES(@cid, @aid, 'account_exp_buffer_c', 0, @level, @exp);
INSERT INTO character_subtype0_fields(character_id) VALUES(@cid);
INSERT INTO character_subtype1_fields(character_id) VALUES(@cid);
INSERT INTO character_init_flags(character_id) VALUES(@cid);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@otherAid", RepoAccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@level", previousLevel);
                    command.Parameters.AddWithValue("@exp", previousExp);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SetAccountExp(
            string connectionString,
            int accountId,
            ulong honorExp,
            uint capsuleExp)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE accounts
SET honor_exp=@honor, growth_capsule_exp=@capsule
WHERE account_id=@aid;";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue(
                        "@honor", honorExp > long.MaxValue ? long.MaxValue : (long)honorExp);
                    command.Parameters.AddWithValue("@capsule", (long)capsuleExp);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static ulong LoadHonor(string connectionString, int accountId)
            => (ulong)LoadColumn(connectionString, accountId, "honor_exp");

        private static uint LoadCapsule(string connectionString, int accountId)
            => (uint)LoadColumn(connectionString, accountId, "growth_capsule_exp");

        private static long LoadColumn(
            string connectionString,
            int accountId,
            string column)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        $"SELECT {column} FROM accounts WHERE account_id=@aid;";
                    command.Parameters.AddWithValue("@aid", accountId);
                    var value = command.ExecuteScalar();
                    return value == null || value == DBNull.Value
                        ? 0L
                        : Convert.ToInt64(value);
                }
            }
        }

        private static void Check(string name, bool success, ref int failures)
        {
            Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {name}");
            if (!success)
                failures++;
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
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                    // 残留临时库不影响判定, 交由系统临时目录清理。
                }
            }
        }
    }
}
