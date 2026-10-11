using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;

namespace DfoServer.Game.Accounts
{
    // 账号级荣誉/成长胶囊经验的进程内累计器。
    //
    // 满级角色每杀一只怪都会走 CharacterExperienceService.Grant -> 本类所在业务;
    // 原实现每次都要开连接 + 事务写 accounts 的两列, 组队 AoE 时把该 session 的
    // 串行收包线程拖成波浪状。这里对齐角色经验已验证的"内存累计 + 延迟落库":
    //   Add                只累计内存, 立即返回含 pending 的有效总值(read-your-writes)
    //   Flush / FlushAll   分钟 tick、出图兜底、停机时把 pending 落库
    //
    // 落库用单条盲增量 UPDATE(MIN(cap, 列 + 增量)), 不做读-改-写, 所以与任何直写路径
    // (任务交付/经验道具/结算的事务内写、GM 工具跨进程写)天然可组合: 既不丢对方的增量,
    // 也不要求对方先 flush。
    //
    // pending 只在两种情况被清掉: 本类自己的 Flush 提交成功, 或胶囊领取提交成功后清胶囊。
    // 直写入口 CompleteDirectWrite 不搬运 pending、也不往调用方事务里写东西, 因此
    // 调用方事务回滚时内存里没有需要撤销的记账, 也不会丢 pending。
    // (反例: 若在调用方事务内顺手 UPDATE 并就地清 pending, 调用方一回滚就两头落空。)
    //
    // 实例是进程内唯一(AccountExperienceProgressService 在多处被 new), pending 的真源
    // 必须进程唯一, 否则同一账号会被拆成多份各自 flush。
    internal sealed class AccountExperienceBuffer
    {
        public static readonly AccountExperienceBuffer Instance = new AccountExperienceBuffer();

        private const string ClockTickName = "account-exp:flush";
        private static readonly object RegisterSync = new object();

        private readonly ConcurrentDictionary<int, Entry> _entries
            = new ConcurrentDictionary<int, Entry>();
        private bool _clockRegistered;

        // 每账号一条。Sync 串行化内存状态(含冷载), FlushSync 串行化"快照 -> 落库 -> 清 pending";
        // 两者不互为反序, 不会死锁。Add 不取 FlushSync, 所以落库 I/O 不阻塞杀怪累计。
        private sealed class Entry
        {
            public readonly object Sync = new object();
            public readonly object FlushSync = new object();
            public string ConnectionString;
            public bool Loaded;
            public ulong KnownHonorExp;    // 本进程视角的 honor_exp 估值(不含 pending)
            public uint KnownCapsuleExp;   // 同上, growth_capsule_exp
            public ulong PendingHonorExp;  // 未落库增量
            public ulong PendingCapsuleExp;
        }

        // 高频杀怪入口: 冷账号先读一次 DB 基线(单行 SELECT, 无事务), 再在内存里加增量。
        // 返回含 pending 的有效总值, 数值与"每只怪直写库"时客户端看到的一致。
        internal AccountExperienceProgressTotals Add(
            int accountId,
            uint honorExpGain,
            string connectionString)
        {
            if (accountId <= 0)
                return default;

            var entry = _entries.GetOrAdd(accountId, _ => new Entry());
            lock (entry.Sync)
            {
                BindConnectionStringLocked(entry, connectionString);
                EnsureLoadedLocked(entry, accountId);

                var honorCap = HonorLevelDataProvider.MaxTotalHonorExp;
                var capsuleCap = GrowthCapsuleDataProvider.RequiredExpPerCapsule;
                var capsuleBefore = ResolveCapsuleLocked(entry, capsuleCap);
                entry.PendingHonorExp = AddSaturating(
                    entry.PendingHonorExp, honorExpGain);
                entry.PendingCapsuleExp = AddSaturating(
                    entry.PendingCapsuleExp,
                    GrowthCapsuleDataProvider.CalculateExpGain(honorExpGain));
                var capsuleAfter = ResolveCapsuleLocked(entry, capsuleCap);
                return new AccountExperienceProgressTotals(
                    ResolveHonorLocked(entry, honorCap),
                    capsuleAfter,
                    capsuleAfter - capsuleBefore);
            }
        }

        // 读有效总值(含 pending)。冷账号读 DB 装载基线(不置任何写标记)。
        internal AccountExperienceProgressTotals GetEffectiveTotals(
            int accountId,
            string connectionString)
        {
            if (accountId <= 0)
                return default;

            var entry = _entries.GetOrAdd(accountId, _ => new Entry());
            lock (entry.Sync)
            {
                BindConnectionStringLocked(entry, connectionString);
                EnsureLoadedLocked(entry, accountId);
                return new AccountExperienceProgressTotals(
                    ResolveHonorLocked(entry, HonorLevelDataProvider.MaxTotalHonorExp),
                    ResolveCapsuleLocked(entry, GrowthCapsuleDataProvider.RequiredExpPerCapsule),
                    0);
            }
        }

        // 胶囊领取判定用的有效值: 在调用方(事务内)读到的 DB 值之上叠加内存 pending。
        // DB 快照是权威值, 不能拿 Known 估值替代 — 外部写(如 GM 工具)可能比 Known 新。
        internal uint ResolveCapsuleExpWithPending(
            int accountId,
            uint persistedTotalExp)
        {
            if (accountId <= 0
                || !_entries.TryGetValue(accountId, out var entry))
                return persistedTotalExp;

            lock (entry.Sync)
            {
                var cap = GrowthCapsuleDataProvider.RequiredExpPerCapsule;
                return (uint)Math.Min(
                    (ulong)cap,
                    (ulong)persistedTotalExp + entry.PendingCapsuleExp);
            }
        }

        // 事务内直写路径(任务交付/经验道具/结算)完成后的内存收口。
        // 调用方已在自己的事务里写完 accounts 的两列, 这里只:
        //   1. 把 Known 同步为直写后的 DB 值, 让后续读(read-your-writes)看得到;
        //   2. 用"直写后的 DB 值 + 内存 pending"算出含 pending 的有效总值;
        //   3. 用同一口径算出本次胶囊增量的实际应用量(胶囊已满时是 0)。
        // 前提(已核实全部调用方): 直写增量随调用方事务提交或回滚, 本方法不搬运 pending,
        // 也不往调用方事务里写任何东西, 所以调用方回滚时 pending 不会被消费, 库值也不会留下
        // 幽灵增量。回滚时唯一的残留是 Known 的乐观估计(第 1 步已经记下未提交的直写值),
        // 它只影响展示口径: 下一次 Flush 的回读会把它校正回真实库值, 且 Flush 用的是
        // 盲增量(基于库值, 不基于 Known), 不会因此多写/少写经验。
        internal AccountExperienceProgressTotals CompleteDirectWrite(
            int accountId,
            ulong totalHonorExp,
            uint totalGrowthCapsuleExp,
            uint growthCapsuleExpBefore)
        {
            if (accountId <= 0)
                return default;

            var entry = _entries.GetOrAdd(accountId, _ => new Entry());
            lock (entry.Sync)
            {
                var honorCap = HonorLevelDataProvider.MaxTotalHonorExp;
                var capsuleCap = GrowthCapsuleDataProvider.RequiredExpPerCapsule;
                var capsuleBefore = (uint)Math.Min(
                    (ulong)capsuleCap,
                    (ulong)growthCapsuleExpBefore + entry.PendingCapsuleExp);

                entry.Loaded = true;
                entry.KnownHonorExp = Math.Min(totalHonorExp, honorCap);
                entry.KnownCapsuleExp = Math.Min(totalGrowthCapsuleExp, capsuleCap);

                var capsuleAfter = ResolveCapsuleLocked(entry, capsuleCap);
                return new AccountExperienceProgressTotals(
                    ResolveHonorLocked(entry, honorCap),
                    capsuleAfter,
                    capsuleAfter - capsuleBefore);
            }
        }

        // 胶囊领取提交成功后调用。领取判定与消耗用的是含 pending 的有效值, 所以 pending
        // 必须一起清零, 否则下一轮 Flush 会把刚被领走的增量重新写回胶囊(re-fill)。
        internal void NotifyCapsuleClaimed(int accountId)
        {
            if (accountId <= 0
                || !_entries.TryGetValue(accountId, out var entry))
                return;

            lock (entry.Sync)
            {
                entry.PendingCapsuleExp = 0;
                entry.KnownCapsuleExp = 0;
            }
        }

        // 把该账号的 pending 落库(自管连接 + 事务)。无 pending 时是空操作。
        // 失败保留 pending 并记日志, 返回 false, 由下一轮 flush 重试。
        internal bool Flush(int accountId)
        {
            if (accountId <= 0
                || !_entries.TryGetValue(accountId, out var entry))
                return true;

            lock (entry.FlushSync)
            {
                ulong honorDelta;
                ulong capsuleDelta;
                lock (entry.Sync)
                {
                    honorDelta = entry.PendingHonorExp;
                    capsuleDelta = entry.PendingCapsuleExp;
                    if (honorDelta == 0 && capsuleDelta == 0)
                        return true;
                }

                if (!TryWritePending(
                        entry,
                        accountId,
                        honorDelta,
                        capsuleDelta,
                        out var honorExp,
                        out var capsuleExp))
                    return false;

                lock (entry.Sync)
                {
                    // 只扣掉本次写回的快照: 落库期间新累计的 pending 留给下一轮。
                    entry.Loaded = true;
                    entry.KnownHonorExp = honorExp;
                    entry.KnownCapsuleExp = capsuleExp;
                    entry.PendingHonorExp -= Math.Min(
                        entry.PendingHonorExp, honorDelta);
                    entry.PendingCapsuleExp -= Math.Min(
                        entry.PendingCapsuleExp, capsuleDelta);
                }

                return true;
            }
        }

        // 逐账号 Flush, 单账号失败不阻塞其他账号。
        internal bool FlushAll()
        {
            var allSucceeded = true;
            foreach (var pair in _entries)
            {
                if (!Flush(pair.Key))
                    allSucceeded = false;
            }

            return allSucceeded;
        }

        internal void RegisterClock(ClockService clock)
        {
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            lock (RegisterSync)
            {
                if (_clockRegistered)
                    return;

                _clockRegistered = true;
                clock.RegisterMinuteTick(ClockTickName, _ => FlushAll());
            }
        }

        // SelfTest 隔离: 丢掉全部账号内存状态, 避免上一个用例的基线与 pending 泄漏到下一个。
        internal static void ResetForTests()
        {
            Instance._entries.Clear();
        }

        private static bool TryWritePending(
            Entry entry,
            int accountId,
            ulong honorDelta,
            ulong capsuleDelta,
            out ulong honorExp,
            out uint capsuleExp)
        {
            honorExp = 0;
            capsuleExp = 0;
            var connectionString = entry.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
                return false;

            var honorCap = HonorLevelDataProvider.MaxTotalHonorExp;
            var capsuleCap = GrowthCapsuleDataProvider.RequiredExpPerCapsule;
            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            // 单条盲增量: 与任何并发直写(事务内经验、GM 工具)可组合。
                            // 增量先按 cap 收口, 避免"列 + 增量"溢出 SQLite 的 64 位整数。
                            command.CommandText = @"
UPDATE accounts
SET honor_exp = MIN(@honorCap, honor_exp + @honorDelta),
    growth_capsule_exp = MIN(@capsuleCap, growth_capsule_exp + @capsuleDelta)
WHERE account_id = @aid;";
                            command.Parameters.AddWithValue("@aid", accountId);
                            command.Parameters.AddWithValue(
                                "@honorCap", ToSqliteInteger(honorCap));
                            command.Parameters.AddWithValue(
                                "@capsuleCap", ToSqliteInteger(capsuleCap));
                            command.Parameters.AddWithValue(
                                "@honorDelta",
                                ToSqliteInteger(Math.Min(honorDelta, honorCap)));
                            command.Parameters.AddWithValue(
                                "@capsuleDelta",
                                ToSqliteInteger(Math.Min(
                                    capsuleDelta, (ulong)capsuleCap)));
                            command.ExecuteNonQuery();
                        }

                        // 回读刷新 Known: 外部写造成的漂移在这里自校正。
                        if (!TryLoadTotals(
                                connection,
                                transaction,
                                accountId,
                                honorCap,
                                capsuleCap,
                                out honorExp,
                                out capsuleExp))
                        {
                            transaction.Rollback();
                            FileLogger.Log(
                                "[AccountExpBuffer] flush skipped, account row "
                                + $"missing: aid={accountId}");
                            return false;
                        }

                        transaction.Commit();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[AccountExpBuffer] flush failed "
                    + $"aid={accountId} honor={honorDelta} capsule={capsuleDelta}: "
                    + ex.Message);
                return false;
            }
        }

        private static void EnsureLoadedLocked(Entry entry, int accountId)
        {
            if (entry.Loaded)
                return;

            var honorCap = HonorLevelDataProvider.MaxTotalHonorExp;
            var capsuleCap = GrowthCapsuleDataProvider.RequiredExpPerCapsule;
            using (var connection = new SqliteConnection(entry.ConnectionString))
            {
                connection.Open();
                TryLoadTotals(
                    connection,
                    null,
                    accountId,
                    honorCap,
                    capsuleCap,
                    out var honorExp,
                    out var capsuleExp);
                entry.KnownHonorExp = honorExp;
                entry.KnownCapsuleExp = capsuleExp;
            }

            entry.Loaded = true;
        }

        // 读 accounts 单行并按 cap 收口, 语义对齐 HonorLevelProgressRepository:104-120
        // 与 GrowthCapsuleProgressRepository:174-197 的读法。
        private static bool TryLoadTotals(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            ulong honorCap,
            uint capsuleCap,
            out ulong honorExp,
            out uint capsuleExp)
        {
            honorExp = 0;
            capsuleExp = 0;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT honor_exp, growth_capsule_exp FROM accounts WHERE account_id=@aid;";
                command.Parameters.AddWithValue("@aid", accountId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    var honor = reader.IsDBNull(0)
                        ? 0L
                        : Convert.ToInt64(reader.GetValue(0));
                    var capsule = reader.IsDBNull(1)
                        ? 0L
                        : Convert.ToInt64(reader.GetValue(1));
                    honorExp = honor <= 0
                        ? 0UL
                        : Math.Min((ulong)honor, honorCap);
                    capsuleExp = capsule <= 0
                        ? 0U
                        : (uint)Math.Min((ulong)capsule, (ulong)capsuleCap);
                }
            }

            return true;
        }

        // 首次绑定连接串。同一账号被指向另一个库(仅 SelfTest 的隔离场景, 正常流程靠
        // ResetForTests 清空)时重置基线, 但保留 pending — 真源数据不因换库而丢弃。
        private static void BindConnectionStringLocked(
            Entry entry,
            string connectionString)
        {
            if (string.Equals(
                    entry.ConnectionString,
                    connectionString,
                    StringComparison.Ordinal))
                return;

            entry.ConnectionString = connectionString;
            entry.Loaded = false;
            entry.KnownHonorExp = 0;
            entry.KnownCapsuleExp = 0;
        }

        private static ulong ResolveHonorLocked(Entry entry, ulong honorCap)
            => Math.Min(honorCap, entry.KnownHonorExp + entry.PendingHonorExp);

        private static uint ResolveCapsuleLocked(Entry entry, uint capsuleCap)
            => (uint)Math.Min(
                (ulong)capsuleCap,
                (ulong)entry.KnownCapsuleExp + entry.PendingCapsuleExp);

        private static ulong AddSaturating(ulong value, ulong delta)
        {
            var sum = value + delta;
            return sum < value ? ulong.MaxValue : sum;
        }

        private static long ToSqliteInteger(ulong value)
            => value > long.MaxValue ? long.MaxValue : (long)value;
    }
}
