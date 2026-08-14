using DfoServer.Game.SelectCharacter;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    internal sealed class CharacterMiscStateRepository
    {
        private readonly string _connectionString;

        internal CharacterMiscStateRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        internal List<Unknown725Snapshot> LoadUnknown725(int characterId)
        {
            var list = new List<Unknown725Snapshot>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT param_a, mode_or_state, content_id, param_b FROM character_daily_schedule_states WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            list.Add(new Unknown725Snapshot
                            {
                                ParamA = reader.GetInt32(0),
                                ModeOrState = reader.GetInt32(1),
                                ContentId = reader.GetInt32(2),
                                ParamB = reader.GetInt32(3),
                            });
                    }
                }
            }
            return list;
        }

        internal void SaveUnknown725(int characterId, List<Unknown725Snapshot> packets)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_daily_schedule_states WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    for (int i = 0; i < packets.Count; i++)
                    {
                        var p = packets[i];
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_daily_schedule_states (character_id, sort_order, param_a, mode_or_state, content_id, param_b) VALUES (@cid, @ord, @pa, @ms, @ci, @pb)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@pa", p.ParamA);
                            cmd.Parameters.AddWithValue("@ms", p.ModeOrState);
                            cmd.Parameters.AddWithValue("@ci", p.ContentId);
                            cmd.Parameters.AddWithValue("@pb", p.ParamB);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        internal Unknown730Snapshot LoadUnknown730(int characterId)
        {
            var snapshot = new Unknown730Snapshot();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT entry_id, sentinel_or_value, flag FROM character_buy_restrict_items WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.Entries.Add(new Unknown730EntrySnapshot
                            {
                                EntryId = reader.GetInt32(0),
                                SentinelOrValue = reader.GetInt32(1),
                                Flag = reader.GetInt32(2),
                            });
                    }
                }
            }
            return snapshot;
        }

        internal void SaveUnknown730(int characterId, Unknown730Snapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_buy_restrict_items WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    for (int i = 0; i < snapshot.Entries.Count; i++)
                    {
                        var e = snapshot.Entries[i];
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_buy_restrict_items (character_id, sort_order, entry_id, sentinel_or_value, flag) VALUES (@cid, @ord, @eid, @sv, @f)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@eid", e.EntryId);
                            cmd.Parameters.AddWithValue("@sv", e.SentinelOrValue);
                            cmd.Parameters.AddWithValue("@f", e.Flag);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }
    }
}
