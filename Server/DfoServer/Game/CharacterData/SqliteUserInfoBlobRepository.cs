using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    public sealed class SqliteUserInfoBlobRepository
    {
        private readonly string _connectionString;

        public SqliteUserInfoBlobRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public SqliteUserInfoBlobRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public int LoadSeedCharacterId()
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT seed_character_id FROM get_userinfo_template WHERE id=1", conn))
                {
                    var result = cmd.ExecuteScalar();
                    return result != null && result != System.DBNull.Value ? System.Convert.ToInt32(result) : 0;
                }
            }
        }

        public Network.Builders.GetUserInfoTemplate LoadGetUserInfoTemplate()
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT pkt0_routing_byte7, gate_or_count1, gate_or_count2, flag_or_manage, key_or_point, unknown16, unknown32, pkt2_result_code, pkt2_character_key, pkt2_slot_flag1, pkt2_slot_flag2, pkt2_state_flag, pkt2_flag3, pkt2_reserved, seed_character_id FROM get_userinfo_template WHERE id=1", conn))
                {
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        return new Network.Builders.GetUserInfoTemplate
                        {
                            Pkt0RoutingByte7 = (byte)r.GetInt32(0),
                            GateOrCount1 = (ushort)r.GetInt32(1),
                            GateOrCount2 = (ushort)r.GetInt32(2),
                            FlagOrManage = (byte)r.GetInt32(3),
                            KeyOrPoint = r.GetInt32(4),
                            Unknown16 = (ushort)r.GetInt32(5),
                            Unknown32 = r.GetInt32(6),
                            Pkt2ResultCode = (byte)r.GetInt32(7),
                            Pkt2CharacterKey = r.GetInt32(8),
                            Pkt2SlotFlag1 = (byte)r.GetInt32(9),
                            Pkt2SlotFlag2 = (byte)r.GetInt32(10),
                            Pkt2StateFlag = (byte)r.GetInt32(11),
                            Pkt2Flag3 = (byte)r.GetInt32(12),
                            Pkt2Reserved = (ushort)r.GetInt32(13),
                            SeedCharacterId = r.GetInt32(14),
                        };
                    }
                }
            }
        }

        public void SaveGetUserInfoTemplate(Network.Builders.GetUserInfoTemplate t)
        {
            if (t == null) return;
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(@"INSERT OR REPLACE INTO get_userinfo_template
                    (id, seed_character_id, pkt0_routing_byte7, gate_or_count1, gate_or_count2, flag_or_manage, key_or_point, unknown16, unknown32,
                     pkt2_result_code, pkt2_character_key, pkt2_slot_flag1, pkt2_slot_flag2, pkt2_state_flag, pkt2_flag3, pkt2_reserved)
                    VALUES (1, @sid, @a, @b, @c, @d, @e, @f, @g, @h, @i, @j, @k, @l, @m, @n)", conn))
                {
                    cmd.Parameters.AddWithValue("@sid", t.SeedCharacterId);
                    cmd.Parameters.AddWithValue("@a", (int)t.Pkt0RoutingByte7);
                    cmd.Parameters.AddWithValue("@b", (int)t.GateOrCount1);
                    cmd.Parameters.AddWithValue("@c", (int)t.GateOrCount2);
                    cmd.Parameters.AddWithValue("@d", (int)t.FlagOrManage);
                    cmd.Parameters.AddWithValue("@e", t.KeyOrPoint);
                    cmd.Parameters.AddWithValue("@f", (int)t.Unknown16);
                    cmd.Parameters.AddWithValue("@g", t.Unknown32);
                    cmd.Parameters.AddWithValue("@h", (int)t.Pkt2ResultCode);
                    cmd.Parameters.AddWithValue("@i", t.Pkt2CharacterKey);
                    cmd.Parameters.AddWithValue("@j", (int)t.Pkt2SlotFlag1);
                    cmd.Parameters.AddWithValue("@k", (int)t.Pkt2SlotFlag2);
                    cmd.Parameters.AddWithValue("@l", (int)t.Pkt2StateFlag);
                    cmd.Parameters.AddWithValue("@m", (int)t.Pkt2Flag3);
                    cmd.Parameters.AddWithValue("@n", (int)t.Pkt2Reserved);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
