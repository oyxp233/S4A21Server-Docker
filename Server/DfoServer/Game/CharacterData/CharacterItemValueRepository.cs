using DfoServer.Game.SelectCharacter;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    internal sealed class CharacterItemValueRepository
    {
        private readonly string _connectionString;

        internal CharacterItemValueRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        internal List<ItemValueEntrySnapshot> LoadItemValueList(int characterId, string listKind)
        {
            var items = new List<ItemValueEntrySnapshot>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT item_id, value FROM character_item_values WHERE character_id = @cid AND list_kind = @kind ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@kind", listKind);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            items.Add(new ItemValueEntrySnapshot { ItemId = reader.GetInt32(0), Value = reader.GetInt32(1) });
                    }
                }
            }
            return items;
        }

        internal void SaveItemValueList(int characterId, string listKind, List<ItemValueEntrySnapshot> items)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_item_values WHERE character_id = @cid AND list_kind = @kind", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@kind", listKind);
                        cmd.ExecuteNonQuery();
                    }
                    for (int i = 0; i < items.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_item_values (character_id, list_kind, sort_order, item_id, value) VALUES (@cid, @kind, @ord, @iid, @val)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@kind", listKind);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@iid", items[i].ItemId);
                            cmd.Parameters.AddWithValue("@val", items[i].Value);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        internal void SaveItemValueListIfEmpty(int characterId, string kind, List<ItemValueEntrySnapshot> items)
        {
            if (LoadItemValueList(characterId, kind).Count == 0 && items.Count > 0)
                SaveItemValueList(characterId, kind, items);
        }
    }
}
