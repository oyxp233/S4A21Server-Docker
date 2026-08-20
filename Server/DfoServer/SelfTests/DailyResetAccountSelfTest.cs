using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Globalization;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class DailyResetAccountSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DAILY_RESET_ACCOUNT selftest ===");
            var failures = 0;
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_daily_reset_{Guid.NewGuid():N}.db");

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var service = new DailyResetService(database);
                var todayId = DailyResetService.TodayId();
                if (!TryFindUsableCountLimitItemIds(
                        out var zeroUsedItemId,
                        out var positiveUsedItemId))
                {
                    throw new InvalidOperationException("无法找到可用于自测的真实限次道具");
                }

                const int accountA = 501;
                const int accountB = 502;
                const int characterA1 = 7001;
                const int characterA2 = 7002;
                const int characterB1 = 8001;

                Seed(
                    database,
                    accountA,
                    accountB,
                    characterA1,
                    characterA2,
                    characterB1,
                    zeroUsedItemId,
                    positiveUsedItemId,
                    todayId);

                var currentDayItems = UsableCountLimitService.LoadCurrentDayItems(
                    database.ConnectionString,
                    characterA1);
                Check(
                    "只下发已使用次数大于0的物品",
                    currentDayItems.Count == 1
                    && currentDayItems[0].ItemId == positiveUsedItemId
                    && currentDayItems[0].Value == 1,
                    ref failures);

                var preBoundaryUtc = new DateTime(2026, 8, 20, 21, 30, 0, DateTimeKind.Utc);
                var afterBoundaryUtc = new DateTime(2026, 8, 20, 22, 30, 0, DateTimeKind.Utc);
                var logoutUtc = new DateTime(2026, 8, 20, 20, 30, 0, DateTimeKind.Utc);
                var logoutAfterBoundaryUtc = new DateTime(2026, 8, 20, 23, 10, 0, DateTimeKind.Utc);

                Check(
                    "6点边界按北京时间计算",
                    DailyResetService.GetDailyResetBoundaryUtc(preBoundaryUtc)
                        == new DateTime(2026, 8, 20, 22, 0, 0, DateTimeKind.Utc),
                    ref failures);
                Check(
                    "日切ID会在6点切换",
                    DailyResetService.TodayId(preBoundaryUtc) == 20260820
                    && DailyResetService.TodayId(afterBoundaryUtc) == 20260821,
                    ref failures);

                Check(
                    "记录退出时间成功",
                    service.TryRecordAccountLogout(accountA, logoutUtc),
                    ref failures);
                Check(
                    "退出时间写入正确",
                    ReadText(
                        database,
                        "SELECT last_logout_at FROM account_daily_reset WHERE account_id = @aid;",
                        accountA) == logoutUtc.ToString("o", CultureInfo.InvariantCulture),
                    ref failures);

                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var applied = false;
                    var ok = service.TryRunAccountFirstLoginReset(
                        connection,
                        transaction,
                        accountA,
                        preBoundaryUtc,
                        (conn, tx) => service.ResetUsableCountLimitsForAccount(
                            conn,
                            tx,
                            accountA),
                        out applied);
                    Check("6点前不触发重置", ok && !applied, ref failures);
                    transaction.Commit();
                }

                Check(
                    "6点前限次数据不变",
                    CountLimitRows(database, accountA) == 3
                    && CountLimitRows(database, accountB) == 1,
                    ref failures);

                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var applied = false;
                    var ok = service.TryRunAccountFirstLoginReset(
                        connection,
                        transaction,
                        accountA,
                        afterBoundaryUtc,
                        (conn, tx) => service.ResetUsableCountLimitsForAccount(
                            conn,
                            tx,
                            accountA),
                        out applied);
                    Check("6点后首次进入触发重置", ok && applied, ref failures);
                    transaction.Commit();
                }

                Check(
                    "账号A的限次数据已清空",
                    CountLimitRows(database, accountA) == 0,
                    ref failures);
                Check(
                    "账号B的限次数据未受影响",
                    CountLimitRows(database, accountB) == 1,
                    ref failures);
                Check(
                    "重置锚点记录了上次退出时间",
                    ReadText(
                        database,
                        "SELECT last_reset_anchor_at FROM account_daily_reset WHERE account_id = @aid;",
                        accountA) == logoutUtc.ToString("o", CultureInfo.InvariantCulture),
                    ref failures);

                Check(
                    "同一会话重复检查不会再次重置",
                    RunResetCheck(
                        service,
                        database,
                        accountA,
                        afterBoundaryUtc.AddMinutes(10),
                        out var reapplied),
                    ref failures);
                Check("重复检查保持不再触发", !reapplied, ref failures);

                Check(
                    "6点后新退出会阻止重复重置",
                    service.TryRecordAccountLogout(accountA, logoutAfterBoundaryUtc),
                    ref failures);
                Check(
                    "6点后退出再次登录不重置",
                    RunResetCheck(
                        service,
                        database,
                        accountA,
                        afterBoundaryUtc.AddHours(1),
                        out var postLogoutApplied),
                    ref failures);
                Check("6点后退出后再次检查未触发", !postLogoutApplied, ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DAILY_RESET_ACCOUNT] EXCEPTION: {ex}");
                failures++;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempDbPath))
                        File.Delete(tempDbPath);
                }
                catch
                {
                }
            }

            Console.WriteLine(
                failures == 0
                    ? "DAILY_RESET_ACCOUNT selftest passed."
                    : $"DAILY_RESET_ACCOUNT selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool RunResetCheck(
            DailyResetService service,
            GameDatabase database,
            int accountId,
            DateTime utcNow,
            out bool applied)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = service.TryRunAccountFirstLoginReset(
                    connection,
                    transaction,
                    accountId,
                    utcNow,
                    (conn, tx) => service.ResetUsableCountLimitsForAccount(
                        conn,
                        tx,
                        accountId),
                    out applied);
                if (ok)
                    transaction.Commit();
                else
                    transaction.Rollback();
                return ok;
            }
        }

        private static void Seed(
            GameDatabase database,
            int accountA,
            int accountB,
            int characterA1,
            int characterA2,
            int characterB1,
            int zeroUsedItemId,
            int positiveUsedItemId,
            int dayId)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                InsertAccount(connection, transaction, accountA, "daily-reset-a");
                InsertAccount(connection, transaction, accountB, "daily-reset-b");
                InsertCharacter(connection, transaction, characterA1, accountA, "daily-reset-a1");
                InsertCharacter(connection, transaction, characterA2, accountA, "daily-reset-a2");
                InsertCharacter(connection, transaction, characterB1, accountB, "daily-reset-b1");
                InsertLimit(connection, transaction, characterA1, zeroUsedItemId, 0, dayId);
                InsertLimit(connection, transaction, characterA1, positiveUsedItemId, 1, dayId);
                InsertLimit(connection, transaction, characterA2, positiveUsedItemId, 2, dayId);
                InsertLimit(connection, transaction, characterB1, positiveUsedItemId, 3, dayId);
                transaction.Commit();
            }
        }

        private static void InsertAccount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            string mid)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@mid", mid);
                command.ExecuteNonQuery();
            }
        }

        private static void InsertCharacter(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            string name)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@name", name);
                command.ExecuteNonQuery();
            }
        }

        private static void InsertLimit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int itemId,
            int usedCount,
            int dayId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_usable_count_limits (
    character_id, item_id, used_count, usable_count_limit, day_id
) VALUES (
    @cid, @iid, @usedCount, 3, @dayId
);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@iid", itemId);
                command.Parameters.AddWithValue("@usedCount", usedCount);
                command.Parameters.AddWithValue("@dayId", dayId);
                command.ExecuteNonQuery();
            }
        }

        private static int CountLimitRows(GameDatabase database, int accountId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*)
FROM character_usable_count_limits lim
JOIN characters c ON c.character_id = lim.character_id
    WHERE c.account_id = @aid;";
                command.Parameters.AddWithValue("@aid", accountId);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static string ReadText(
            GameDatabase database,
            string sql,
            int accountId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.Parameters.AddWithValue("@aid", accountId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? null
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static bool TryFindUsableCountLimitItemIds(
            out int zeroUsedItemId,
            out int positiveUsedItemId)
        {
            zeroUsedItemId = 0;
            positiveUsedItemId = 0;

            var stackableList = LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst"));
            foreach (var entry in stackableList.Entries)
            {
                var stackable = StackableItemProvider.Load(entry.Id);
                if (stackable == null || stackable.TotalUsableCount < 0)
                    continue;

                if (zeroUsedItemId <= 0)
                {
                    zeroUsedItemId = entry.Id;
                    continue;
                }

                if (entry.Id != zeroUsedItemId)
                {
                    positiveUsedItemId = entry.Id;
                    return true;
                }
            }

            return false;
        }
    }
}
