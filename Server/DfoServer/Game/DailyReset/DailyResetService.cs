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

        // 当前周ID(ISO年*100+ISO周)。用 ISOWeek.GetYear 避免跨年周跳变(12月末53周/1月初1周)。
        // 同一回拨时间基准 → 周一 00:00-05:59 自动归上周。
        public static int WeekId()
        {
            var d = DateTime.UtcNow.AddHours(8 - 6);
            return ISOWeek.GetYear(d) * 100 + ISOWeek.GetWeekOfYear(d);
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
    }
}
