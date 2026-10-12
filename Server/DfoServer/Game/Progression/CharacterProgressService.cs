using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Progression
{
    // 经验系统内部原语: characters 表 level/exp 的唯一业务写入点(连带战斗属性重算)。
    // 业务代码一律走 CharacterExperienceService, 不要直接调这里。
    internal static class CharacterProgressService
    {
        internal static bool PersistLevelAndExp(
            int characterId,
            byte level,
            uint exp,
            IGameDatabase database,
            string source = "experience")
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            using (var connection = database.OpenConnection())
                return PersistLevelAndExp(connection, characterId, level, exp, source);
        }

        internal static bool PersistLevelAndExp(
            string connectionString,
            int characterId,
            byte level,
            uint exp,
            string source = "experience")
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("connectionString is empty", nameof(connectionString));

            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                return PersistLevelAndExp(conn, characterId, level, exp, source);
            }
        }

        // 等级/经验写与战斗属性写必须同生共死: 崩在中间会留下"等级已升属性没跟上"的
        // 不一致状态(历史上启动时的全量重算就是为修这类存量而生)。显式事务包住两步。
        private static bool PersistLevelAndExp(
            SqliteConnection conn,
            int characterId,
            byte level,
            uint exp,
            string source)
        {
            using (var tx = conn.BeginTransaction())
            {
                var updated = PersistLevelAndExp(conn, tx, characterId, level, exp, source);
                if (updated)
                    tx.Commit();
                else
                    tx.Rollback();
                return updated;
            }
        }

        // (conn, tx) 变体: 并入调用方的事务, 由调用方提交/回滚。
        // GrantInTransaction 走这里, 使"业务变更 + 经验/等级/战斗属性"整体原子。
        internal static bool PersistLevelAndExp(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            byte level,
            uint exp,
            string source = "experience")
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (tx == null) throw new ArgumentNullException(nameof(tx));

            byte job;
            byte growType;
            byte previousLevel;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT job, grow_type, level FROM characters WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;   // 角色不存在: 随事务回滚, 不留半截写入

                    job = (byte)reader.GetInt32(0);
                    growType = (byte)reader.GetInt32(1);
                    previousLevel = (byte)reader.GetInt32(2);
                }
            }

            CharacterStatComputer.DecodeGrowType(growType, out int firstGrow, out int secondGrow);
            var combatStats = CharacterStatComputer.BuildAdditionalInfo(job, level, firstGrow, secondGrow);
            if (SqliteSubtype1Repository.UpdateCombatStatsOnConnection(conn, characterId, combatStats, tx) <= 0)
                return false;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE characters
SET level = @lvl, exp = @exp, updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@lvl", (int)level);
                cmd.Parameters.AddWithValue("@exp", (long)exp);
                cmd.Parameters.AddWithValue("@cid", characterId);
                if (cmd.ExecuteNonQuery() != 1)
                    return false;
            }

            if (level > previousLevel)
                CharacterLevelUpEventRepository.Record(conn, tx, characterId, previousLevel, level, source);
            return true;
        }
    }
}
