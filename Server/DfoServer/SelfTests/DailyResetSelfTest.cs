using DfoServer.Game.DailyReset;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    // 每日/周常重置机制自测(纯机制, 不涉及任何业务功能):
    // 键控领取(cap1)/计数器上限判定/跨天跨周归零/周期互不干扰/组合事务回滚。
    // 功能侧用例见各功能自测(如 ReviveCoinSelfTest)。
    public static class DailyResetSelfTest
    {
        private const int AccountId = 930016;
        private const int CharacterId = 930116;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== DAILY_RESET selftest ===");

            var tempDb = Path.Combine(Path.GetTempPath(), "daily_reset_selftest.db");
            DeleteTempDatabase(tempDb);
            var connStr = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
            Seed(connStr);

            var svc = new DailyResetService(tempDb, ServerPaths.SchemaFilePath);

            // ── 原子领取(布尔标记 = cap1 计数器, 功能自带 key) ──
            const string ClaimKey = "selftest_daily_claim";
            Check("初始未领取", !svc.IsClaimed(CharacterId, ClaimKey));
            Check("首次领取成功", svc.TryClaimFlag(CharacterId, ClaimKey));
            Check("领取后标记为已领", svc.IsClaimed(CharacterId, ClaimKey));
            Check("当日二次领取被拒", !svc.TryClaimFlag(CharacterId, ClaimKey));

            // ── 跨天归零(把 day_id 拨回昨天) ──
            Exec(connStr, "UPDATE character_daily_reset SET day_id = day_id - 1 WHERE character_id = " + CharacterId);
            Check("跨天后标记自动归零", !svc.IsClaimed(CharacterId, ClaimKey));
            Check("跨天后可再次领取", svc.TryClaimFlag(CharacterId, ClaimKey));

            // ── 周常标记与跨周归零 ──
            Check("周常标记领取", svc.TryClaimFlag(CharacterId, "selftest_weekly_claim", DailyResetService.PeriodWeek));
            Exec(connStr, "UPDATE character_daily_reset SET week_id = week_id - 1 WHERE character_id = " + CharacterId);
            Check("跨周后周标记归零", !svc.IsClaimed(CharacterId, "selftest_weekly_claim"));
            Check("跨周不影响当日标记", svc.IsClaimed(CharacterId, ClaimKey));

            // ── 组合事务回滚: (conn,tx) 变体领取后不提交 ──
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    Check("事务内领取成功", svc.TryClaimFlag(conn, tx, CharacterId, "selftest_tx_claim"));
                    // 故意不 Commit
                }
            }
            Check("未提交事务回滚后仍可领取", svc.TryClaimFlag(CharacterId, "selftest_tx_claim"));

            // ── 计数器: 每日3次进图+道具补充场景 ──
            const string EntryKey = "selftest_entry_used";
            const string ExtraKey = "selftest_entry_extra";
            Check("第1次进入放行", svc.TryIncrementCounter(CharacterId, EntryKey, 3));
            Check("第2次进入放行", svc.TryIncrementCounter(CharacterId, EntryKey, 3));
            Check("第3次进入放行", svc.TryIncrementCounter(CharacterId, EntryKey, 3));
            Check("第4次被拒(3/3)", !svc.TryIncrementCounter(CharacterId, EntryKey, 3));
            Check("已用计数=3", svc.GetCounter(CharacterId, EntryKey) == 3);
            svc.AddCounter(CharacterId, ExtraKey, 1);   // 道具补充1次
            var cap = 3 + (int)svc.GetCounter(CharacterId, ExtraKey);
            Check("补充后第4次放行(cap=4)", svc.TryIncrementCounter(CharacterId, EntryKey, cap));
            Check("第5次仍被拒", !svc.TryIncrementCounter(CharacterId, EntryKey, cap));

            // ── 计数器跨天/跨周清理 ──
            svc.AddCounter(CharacterId, "selftest_weekly", 2, DailyResetService.PeriodWeek);
            Exec(connStr, "UPDATE character_daily_reset SET day_id = day_id - 1 WHERE character_id = " + CharacterId);
            Check("跨天后日计数清零", svc.GetCounter(CharacterId, EntryKey) == 0);
            Check("跨天不清周计数", svc.GetCounter(CharacterId, "selftest_weekly") == 2);
            Exec(connStr, "UPDATE character_daily_reset SET week_id = week_id - 1 WHERE character_id = " + CharacterId);
            Check("跨周后周计数清零", svc.GetCounter(CharacterId, "selftest_weekly") == 0);

            // ── 计数器事务回滚 ──
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    Check("事务内计数放行", svc.TryIncrementCounter(conn, tx, CharacterId, EntryKey, 3));
                    // 故意不 Commit
                }
            }
            Check("回滚后计数不变", svc.GetCounter(CharacterId, EntryKey) == 0);

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static void Seed(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash) VALUES (@aid, 'daily-reset-selftest', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name) VALUES (@cid, @aid, 'daily-reset-selftest');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void Exec(string connStr, string sql)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            try
            {
                if (File.Exists(databasePath)) File.Delete(databasePath);
                if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
                if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
            }
            catch
            {
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}
