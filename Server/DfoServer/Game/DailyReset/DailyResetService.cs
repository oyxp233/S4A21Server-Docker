using System;
using System.Globalization;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.DailyReset
{
    // 每日/周常重置: character_daily_reset 只做门控(该角色周期状态属于哪天/哪周),
    // 全部状态存 character_daily_counters 账本(一功能一 key)。布尔标记 = cap=1 的计数器。
    // 日界 = 北京时间 06:00(凌晨0-6点算前一天); 周界 = ISO 周一 06:00。
    //
    // 设计约束:
    //   1) 跨天/跨周归零内建于每次操作(EnsureRowAndRollover), 调用方不存在"忘记先重置"的使用陷阱;
    //   2) 标记/计数写入均为单条原子语句(upsert WHERE 判定), 无读改写窗口;
    //   3) 全部操作提供 (conn,tx) 变体, 供与发放/扣除物品并入同一事务(参考收集箱模式);
    //   4) 本服务零业务知识: 功能自带 counter_key(自描述蛇形名), 此处不登记任何功能常量/bit。
    public sealed class DailyResetService
    {
        private readonly string _connectionString;

        public DailyResetService(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public DailyResetService(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        // 当前日ID(yyyyMMdd)。UTC+8 再回拨6小时 → 06:00 切日。
        public static int TodayId()
        {
            var d = DateTime.UtcNow.AddHours(8 - 6);
            return d.Year * 10000 + d.Month * 100 + d.Day;
        }

        public static int TodayId(DateTime utcNow)
        {
            var d = utcNow.AddHours(8 - 6);
            return d.Year * 10000 + d.Month * 100 + d.Day;
        }

        // 当前周ID(ISO年*100+ISO周)。用 ISOWeek.GetYear 避免跨年周跳变(12月末53周/1月初1周)。
        // 同一回拨时间基准 → 周一 00:00-05:59 自动归上周。
        public static int WeekId()
        {
            var d = DateTime.UtcNow.AddHours(8 - 6);
            return ISOWeek.GetYear(d) * 100 + ISOWeek.GetWeekOfYear(d);
        }

        public static int WeekId(DateTime utcNow)
        {
            var d = utcNow.AddHours(8 - 6);
            return ISOWeek.GetYear(d) * 100 + ISOWeek.GetWeekOfYear(d);
        }

        internal static DateTime GetGameDayStartUtc()
            => GetGameDayStartUtc(DateTime.UtcNow);

        internal static DateTime GetGameDayStartUtc(DateTime utcNow)
        {
            var d = utcNow.AddHours(8 - 6);
            return d.Date.AddHours(-(8 - 6));
        }

        internal static DateTime GetDailyResetBoundaryUtc(DateTime utcNow)
        {
            var d = utcNow.AddHours(8);
            return d.Date.AddHours(6).AddHours(-8);
        }

        // ── 布尔标记 = cap=1 的计数器: 功能自带 key, 无集中 bit 分配 ──

        // 原子领取当期标记: 未领→置位并返回true; 已领→false。跨天/周自动先归零。
        public bool TryClaimFlag(int characterId, string key, string period = PeriodDay)
            => TryIncrementCounter(characterId, key, 1, period);

        // (conn,tx) 变体: 与同事务内的其他写入(如发放物品)一起提交/回滚。
        public bool TryClaimFlag(SqliteConnection conn, SqliteTransaction tx, int characterId, string key, string period = PeriodDay)
            => TryIncrementCounter(conn, tx, characterId, key, 1, period);

        public bool IsClaimed(int characterId, string key)
            => GetCounter(characterId, key) > 0;

        // ── 计数器(账本式): 一功能一 key ──
        // key 用自描述蛇形名(如 "tower_entry_used"); 同一 key 的 period 必须固定。

        public const string PeriodDay = "day";
        public const string PeriodWeek = "week";
        private static string CreateAccountDailyResetSql => @"
CREATE TABLE IF NOT EXISTS account_daily_reset (
    account_id INTEGER PRIMARY KEY,
    last_logout_at TEXT,
    last_reset_anchor_at TEXT,
    last_reset_day_id INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);";

        private static string CreateUsableCountLimitTableSql => @"
CREATE TABLE IF NOT EXISTS character_usable_count_limits (
    character_id INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    used_count INTEGER NOT NULL DEFAULT 0,
    usable_count_limit INTEGER NOT NULL DEFAULT 0,
    day_id INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, item_id)
);";

        private static string CreateUsableCountLimitIndexSql => @"
CREATE INDEX IF NOT EXISTS idx_character_usable_count_limits_character_day
    ON character_usable_count_limits(character_id, day_id);";

        // 原子递增: value < cap 时 +1 并返回 true; 已达上限返回 false。跨天/周自动先归零。
        // 典型用法(每日3次+道具补充): cap = 3 + GetCounter(extraKey), 补充道具时 AddCounter(extraKey, 1)。
        public bool TryIncrementCounter(int characterId, string key, int cap, string period = PeriodDay)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var allowed = TryIncrementCounter(conn, tx, characterId, key, cap, period);
                    tx.Commit();
                    return allowed;
                }
            }
        }

        // (conn,tx) 变体: 与同事务内的其他写入一起提交/回滚。
        public bool TryIncrementCounter(SqliteConnection conn, SqliteTransaction tx, int characterId, string key, int cap, string period = PeriodDay)
        {
            ValidatePeriod(period);
            if (cap <= 0)
                return false;

            EnsureRowAndRollover(conn, tx, characterId);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO character_daily_counters (character_id, counter_key, period, value)
VALUES (@cid, @key, @period, 1)
ON CONFLICT (character_id, counter_key) DO UPDATE SET value = value + 1 WHERE value < @cap;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@period", period);
                cmd.Parameters.AddWithValue("@cap", cap);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        // 原子增加任意额度。适用于金币、积分等“本次增量 + 已用值不得超过上限”的业务。
        // 调用方把本方法放进自己的事务，可保证额度占用与实际业务写入一起提交或回滚。
        public static bool TryAddCounterAtomic(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            string key,
            long delta,
            long cap,
            string period = PeriodDay)
        {
            ValidatePeriod(period);
            if (characterId <= 0 || string.IsNullOrWhiteSpace(key) || delta < 0 || cap < 0 || delta > cap)
                return false;

            EnsureRowAndRollover(conn, tx, characterId);
            if (delta == 0)
                return true;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO character_daily_counters (character_id, counter_key, period, value)
VALUES (@cid, @key, @period, @delta)
ON CONFLICT (character_id, counter_key) DO UPDATE SET value = value + @delta
WHERE character_daily_counters.period = @period
  AND character_daily_counters.value >= 0
  AND character_daily_counters.value <= @cap - @delta;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@period", period);
                cmd.Parameters.AddWithValue("@delta", delta);
                cmd.Parameters.AddWithValue("@cap", cap);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        // 无条件累加(如道具补充当日额度)。与扣道具并入同一事务时用 (conn,tx) 变体。
        public void AddCounter(int characterId, string key, int delta, string period = PeriodDay)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    AddCounter(conn, tx, characterId, key, delta, period);
                    tx.Commit();
                }
            }
        }

        public void AddCounter(SqliteConnection conn, SqliteTransaction tx, int characterId, string key, int delta, string period = PeriodDay)
        {
            ValidatePeriod(period);
            EnsureRowAndRollover(conn, tx, characterId);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO character_daily_counters (character_id, counter_key, period, value)
VALUES (@cid, @key, @period, @delta)
ON CONFLICT (character_id, counter_key) DO UPDATE SET value = value + @delta;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@period", period);
                cmd.Parameters.AddWithValue("@delta", delta);
                cmd.ExecuteNonQuery();
            }
        }

        public long GetCounter(int characterId, string key)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var value = GetCounter(conn, tx, characterId, key);
                    tx.Commit();   // 归零结果落库
                    return value;
                }
            }
        }

        public long GetCounter(SqliteConnection conn, SqliteTransaction tx, int characterId, string key)
        {
            EnsureRowAndRollover(conn, tx, characterId);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT value FROM character_daily_counters WHERE character_id = @cid AND counter_key = @key;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@key", key);
                return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
        }

        public bool TryRecordAccountLogout(int accountId, DateTime utcNow)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var ok = TryRecordAccountLogout(conn, tx, accountId, utcNow);
                    if (ok)
                        tx.Commit();
                    return ok;
                }
            }
        }

        public bool TryRecordAccountLogout(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId,
            DateTime utcNow)
        {
            if (conn == null || accountId <= 0)
                return false;
            if (!EnsureAccountSchema(conn, tx))
                return false;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO account_daily_reset (account_id, last_logout_at, updated_at)
VALUES (@aid, @logoutAt, CURRENT_TIMESTAMP)
ON CONFLICT(account_id) DO UPDATE SET
    last_logout_at = excluded.last_logout_at,
    updated_at = CURRENT_TIMESTAMP;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.Parameters.AddWithValue("@logoutAt", utcNow.ToString("o", CultureInfo.InvariantCulture));
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool TryRunAccountFirstLoginReset(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId,
            Func<SqliteConnection, SqliteTransaction, bool> resetAction,
            out bool applied)
        {
            return TryRunAccountFirstLoginReset(
                conn,
                tx,
                accountId,
                DateTime.UtcNow,
                resetAction,
                out applied);
        }

        public bool TryRunAccountFirstLoginReset(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId,
            DateTime utcNow,
            Func<SqliteConnection, SqliteTransaction, bool> resetAction,
            out bool applied)
        {
            applied = false;
            if (conn == null || accountId <= 0)
                return false;
            if (!EnsureAccountSchema(conn, tx))
                return false;

            var today = TodayId(utcNow);
            var boundaryUtc = GetDailyResetBoundaryUtc(utcNow);
            if (utcNow < boundaryUtc)
                return true;

            var boundaryUtcText = boundaryUtc.ToString("o", CultureInfo.InvariantCulture);

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"
INSERT OR IGNORE INTO account_daily_reset (account_id)
VALUES (@aid);";
                insert.Parameters.AddWithValue("@aid", accountId);
                insert.ExecuteNonQuery();
            }

            using (var claim = conn.CreateCommand())
            {
                claim.Transaction = tx;
                claim.CommandText = @"
UPDATE account_daily_reset
SET last_reset_anchor_at = COALESCE(last_logout_at, @boundary),
    last_reset_day_id = @today,
    updated_at = CURRENT_TIMESTAMP
WHERE account_id = @aid
  AND (
        (last_logout_at IS NULL AND last_reset_anchor_at IS NULL)
     OR (last_logout_at IS NOT NULL
         AND last_logout_at < @boundary
         AND (last_reset_anchor_at IS NULL OR last_reset_anchor_at <> last_logout_at))
      );";
                claim.Parameters.AddWithValue("@aid", accountId);
                claim.Parameters.AddWithValue("@today", today);
                claim.Parameters.AddWithValue("@boundary", boundaryUtcText);
                if (claim.ExecuteNonQuery() == 0)
                    return true;
            }

            if (resetAction != null && !resetAction(conn, tx))
                return false;

            applied = true;
            return true;
        }

        public bool ResetUsableCountLimitsForAccount(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId)
        {
            if (conn == null || accountId <= 0)
                return false;
            if (!EnsureUsableCountLimitSchema(conn, tx))
                return false;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
DELETE FROM character_usable_count_limits
WHERE character_id IN (
    SELECT character_id
    FROM characters
    WHERE account_id = @aid
);";
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.ExecuteNonQuery();
                return true;
            }
        }

        private static void ValidatePeriod(string period)
        {
            if (period != PeriodDay && period != PeriodWeek)
                throw new ArgumentException("invalid period: " + period);
        }

        // 建行(不存在时) + 跨天/跨周归零: 删除对应周期的计数行(删行=归零) + 拨门控。
        // 语句各自原子, 同一事务内执行; DELETE 必须先于对应门控 UPDATE(靠旧 day_id/week_id 判断过期)。
        private static void EnsureRowAndRollover(SqliteConnection conn, SqliteTransaction tx, int characterId)
        {
            var today = TodayId();
            var week = WeekId();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT OR IGNORE INTO character_daily_reset (character_id, day_id, week_id) VALUES (@cid, @today, @week);
DELETE FROM character_daily_counters
WHERE character_id = @cid AND period = 'day'
  AND EXISTS (SELECT 1 FROM character_daily_reset r WHERE r.character_id = @cid AND r.day_id <> @today);
UPDATE character_daily_reset SET day_id = @today
WHERE character_id = @cid AND day_id <> @today;
DELETE FROM character_daily_counters
WHERE character_id = @cid AND period = 'week'
  AND EXISTS (SELECT 1 FROM character_daily_reset r WHERE r.character_id = @cid AND r.week_id <> @week);
UPDATE character_daily_reset SET week_id = @week
WHERE character_id = @cid AND week_id <> @week;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@today", today);
                cmd.Parameters.AddWithValue("@week", week);
                cmd.ExecuteNonQuery();
            }
        }

        private static bool EnsureAccountSchema(SqliteConnection conn, SqliteTransaction tx)
        {
            if (conn == null)
                return false;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = CreateAccountDailyResetSql;
                cmd.ExecuteNonQuery();
            }

            EnsureAccountDailyResetColumn(
                conn,
                tx,
                "last_reset_anchor_at",
                "TEXT");
            EnsureAccountDailyResetColumn(
                conn,
                tx,
                "last_reset_day_id",
                "INTEGER NOT NULL DEFAULT 0");
            return true;
        }

        private static void EnsureAccountDailyResetColumn(
            SqliteConnection conn,
            SqliteTransaction tx,
            string columnName,
            string columnSql)
        {
            if (HasTableColumn(conn, tx, "account_daily_reset", columnName))
                return;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"ALTER TABLE account_daily_reset ADD COLUMN {columnName} {columnSql};";
                cmd.ExecuteNonQuery();
            }
        }

        private static bool HasTableColumn(
            SqliteConnection conn,
            SqliteTransaction tx,
            string tableName,
            string columnName)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"PRAGMA table_info({tableName});";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(1)
                            && string.Equals(
                                reader.GetString(1),
                                columnName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool EnsureUsableCountLimitSchema(SqliteConnection conn, SqliteTransaction tx)
        {
            if (conn == null)
                return false;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = CreateUsableCountLimitTableSql;
                cmd.ExecuteNonQuery();
                cmd.CommandText = CreateUsableCountLimitIndexSql;
                cmd.ExecuteNonQuery();
                return true;
            }
        }
    }
}
