using System.Collections.Generic;
using System;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public sealed class CollectBoxSlotEntry
    {
        public int BoxIndex { get; set; }

        public int SlotIndex { get; set; }

        public int ItemId { get; set; }
    }

    public sealed class CollectBoxProgressRepository
    {
        private readonly string _connectionString;

        public CollectBoxProgressRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public CollectBoxProgressRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public IReadOnlyList<CollectBoxSlotEntry> LoadSlots(int characterId, int boxIndex)
        {
            var list = new List<CollectBoxSlotEntry>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT slot_index, item_id FROM character_collectbox_slots WHERE character_id=@cid AND box_index=@box ORDER BY slot_index",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@box", boxIndex);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            list.Add(new CollectBoxSlotEntry
                            {
                                BoxIndex = boxIndex,
                                SlotIndex = r.GetInt32(0),
                                ItemId = r.GetInt32(1),
                            });
                        }
                    }
                }
            }

            return list;
        }

        internal static void LoadModel(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            CollectBoxModel model)
        {
            if (conn == null || characterId <= 0 || model == null)
                return;

            using (var cmd = new SqliteCommand(
                @"SELECT box_index, slot_index, item_id
                  FROM character_collectbox_slots
                  WHERE character_id=@cid
                  ORDER BY box_index, slot_index",
                conn,
                tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        model.AttachItem(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
                }
            }
        }

        public bool HasItem(int characterId, int boxIndex, int itemId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM character_collectbox_slots WHERE character_id=@cid AND box_index=@box AND item_id=@item",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@box", boxIndex);
                    cmd.Parameters.AddWithValue("@item", itemId);
                    return System.Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
        }

        public void PutSlot(int characterId, int boxIndex, int slotIndex, int itemId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                PutSlot(conn, null, characterId, boxIndex, slotIndex, itemId);
            }
        }

        public void PutSlot(SqliteConnection conn, SqliteTransaction tx, int characterId, int boxIndex, int slotIndex, int itemId)
        {
            SaveSlot(conn, tx, characterId, boxIndex, slotIndex, itemId);
        }

        internal static void SaveSlot(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            int boxIndex,
            int slotIndex,
            int itemId)
        {
            if (conn == null || characterId <= 0 || boxIndex < 0 || slotIndex < 0)
                return;

            if (itemId <= 0)
            {
                DeleteSlot(conn, tx, characterId, boxIndex, slotIndex);
                return;
            }

            using (var cmd = new SqliteCommand(
                @"INSERT INTO character_collectbox_slots (character_id, box_index, slot_index, item_id)
                  VALUES (@cid, @box, @slot, @item)
                  ON CONFLICT(character_id, box_index, slot_index) DO UPDATE SET item_id=@item",
                conn,
                tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@box", boxIndex);
                cmd.Parameters.AddWithValue("@slot", slotIndex);
                cmd.Parameters.AddWithValue("@item", itemId);
                cmd.ExecuteNonQuery();
            }
        }

        public bool RemoveItem(int characterId, int boxIndex, int itemId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                return RemoveItem(conn, null, characterId, boxIndex, itemId);
            }
        }

        public bool RemoveItem(SqliteConnection conn, SqliteTransaction tx, int characterId, int boxIndex, int itemId)
        {
            using (var cmd = new SqliteCommand(
                "DELETE FROM character_collectbox_slots WHERE character_id=@cid AND box_index=@box AND item_id=@item",
                conn,
                tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@box", boxIndex);
                cmd.Parameters.AddWithValue("@item", itemId);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        internal static bool DeleteSlot(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            int boxIndex,
            int slotIndex)
        {
            if (conn == null || characterId <= 0 || boxIndex < 0 || slotIndex < 0)
                return false;

            using (var cmd = new SqliteCommand(
                "DELETE FROM character_collectbox_slots WHERE character_id=@cid AND box_index=@box AND slot_index=@slot",
                conn,
                tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@box", boxIndex);
                cmd.Parameters.AddWithValue("@slot", slotIndex);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool TryFindSlotByItem(int characterId, int itemId, out int boxIndex, out int slotIndex)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT box_index, slot_index FROM character_collectbox_slots WHERE character_id=@cid AND item_id=@item ORDER BY box_index, slot_index LIMIT 1",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@item", itemId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read())
                        {
                            boxIndex = 0;
                            slotIndex = 0;
                            return false;
                        }

                        boxIndex = r.GetInt32(0);
                        slotIndex = r.GetInt32(1);
                        return true;
                    }
                }
            }
        }
    }
}
