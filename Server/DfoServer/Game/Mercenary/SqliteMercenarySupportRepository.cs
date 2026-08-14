using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Mercenary
{
    // 保存支援兵选择状态。
    public sealed class SqliteMercenarySupportRepository
    {
        // 支援兵界面启用时写入 subtype0 兼容字段；opaque 值集中管理。
        private const byte SupportUiEnabledCompat = 1;
        private const byte SupportOpaqueStateCompat = 4;
        private const byte SupportRefreshByteCompat = 1;

        private readonly string _connectionString;

        public SqliteMercenarySupportRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public SqliteMercenarySupportRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public MercenarySupportState LoadSlot(int ownerCharacterId, byte slot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(@"
SELECT owner_character_id, slot, support_character_id, skill_id, striker_skill_id
FROM character_mercenary_support
WHERE owner_character_id = @owner AND slot = @slot", conn))
                {
                    cmd.Parameters.AddWithValue("@owner", ownerCharacterId);
                    cmd.Parameters.AddWithValue("@slot", (int)slot);
                    using (var reader = cmd.ExecuteReader())
                    {
                        return reader.Read() ? ReadState(reader) : null;
                    }
                }
            }
        }

        public void Save(MercenarySupportState state)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(@"
INSERT INTO character_mercenary_support (
    owner_character_id, slot, support_character_id, skill_id, striker_skill_id, updated_at
) VALUES (
    @owner, @slot, @support, @skill, @strikerSkill, CURRENT_TIMESTAMP
)
ON CONFLICT(owner_character_id, slot) DO UPDATE SET
    support_character_id = excluded.support_character_id,
    skill_id = excluded.skill_id,
    striker_skill_id = excluded.striker_skill_id,
    updated_at = CURRENT_TIMESTAMP", conn))
                {
                    cmd.Parameters.AddWithValue("@owner", state.OwnerCharacterId);
                    cmd.Parameters.AddWithValue("@slot", (int)state.Slot);
                    cmd.Parameters.AddWithValue("@support", state.SupportCharacterId);
                    cmd.Parameters.AddWithValue("@skill", (int)state.SkillId);
                    cmd.Parameters.AddWithValue("@strikerSkill", (int)state.StrikerSkillId);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = new SqliteCommand(@"
DELETE FROM character_mercenary_support
WHERE owner_character_id = @owner AND slot <> @slot", conn))
                {
                    cmd.Parameters.AddWithValue("@owner", state.OwnerCharacterId);
                    cmd.Parameters.AddWithValue("@slot", (int)state.Slot);
                    cmd.ExecuteNonQuery();
                }

                SyncOwnerSubtype0LinkFields(conn, state.OwnerCharacterId, enabled: true);
            }
        }

        public void Clear(int ownerCharacterId, byte slot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(@"
DELETE FROM character_mercenary_support
WHERE owner_character_id = @owner AND slot = @slot", conn))
                {
                    cmd.Parameters.AddWithValue("@owner", ownerCharacterId);
                    cmd.Parameters.AddWithValue("@slot", (int)slot);
                    cmd.ExecuteNonQuery();
                }

                if (!HasAnyActiveSupport(conn, ownerCharacterId))
                    SyncOwnerSubtype0LinkFields(conn, ownerCharacterId, enabled: false);
            }
        }

        private static bool HasAnyActiveSupport(SqliteConnection conn, int ownerCharacterId)
        {
            using (var cmd = new SqliteCommand(@"
SELECT COUNT(*)
FROM character_mercenary_support
WHERE owner_character_id = @owner AND slot = @slot AND support_character_id > 0 AND skill_id > 0", conn))
            {
                cmd.Parameters.AddWithValue("@owner", ownerCharacterId);
                cmd.Parameters.AddWithValue("@slot", (int)MercenarySupportState.SingletonStateKey);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        private static void SyncOwnerSubtype0LinkFields(SqliteConnection conn, int ownerCharacterId, bool enabled)
        {
            if (ownerCharacterId <= 0)
                return;

            var tail = SqliteSubtype0FieldsRepository.Load(conn, ownerCharacterId) ?? new UserInfoMinimumTailSnapshot();
            tail.LinkSlotEnabled = enabled ? SupportUiEnabledCompat : (byte)0;
            tail.LinkTypeA = enabled ? SupportOpaqueStateCompat : (byte)0;
            tail.LinkTypeB = enabled ? SupportRefreshByteCompat : (byte)0;
            SqliteSubtype0FieldsRepository.Save(conn, ownerCharacterId, tail);
        }

        private static MercenarySupportState ReadState(SqliteDataReader reader)
        {
            return new MercenarySupportState
            {
                OwnerCharacterId = reader.GetInt32(0),
                Slot = (byte)reader.GetInt32(1),
                SupportCharacterId = reader.GetInt32(2),
                SkillId = (ushort)reader.GetInt32(3),
                StrikerSkillId = (ushort)reader.GetInt32(4),
            };
        }
    }
}
