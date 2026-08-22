using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    internal sealed class CharacterItemStateRepository
    {
        private readonly string _connectionString;

        internal CharacterItemStateRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        internal List<ItemStateEntrySnapshot> LoadItemStateList(int characterId, string stateKind)
        {
            var items = new List<ItemStateEntrySnapshot>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                LoadItemStateList(conn, null, characterId, stateKind, items);
            }

            return items;
        }

        internal void SaveItemStateListIfEmpty(
            int characterId,
            string stateKind,
            List<ItemStateEntrySnapshot> items)
        {
            if (LoadItemStateList(characterId, stateKind).Count == 0 && items.Count > 0)
                SaveItemStateList(characterId, stateKind, items);
        }

        internal static void LoadInto(
            SqliteConnection connection,
            int characterId,
            InventoryItemStateBook itemStates)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (itemStates == null)
                throw new ArgumentNullException(nameof(itemStates));

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT state_kind, item_id, expire_time
FROM character_item_states
WHERE character_id = @cid
ORDER BY state_kind, item_id;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        itemStates.Attach(
                            reader.GetString(0),
                            reader.GetInt32(1),
                            reader.GetInt32(2));
                    }
                }
            }
        }

        internal static void SaveAll(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryItemStateBook itemStates)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (itemStates == null)
                throw new ArgumentNullException(nameof(itemStates));

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM character_item_states WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }

            foreach (var entry in itemStates.GetEntries())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO character_item_states (
    character_id, state_kind, item_id, expire_time, updated_at
) VALUES (
    @cid, @kind, @iid, @expireTime, CURRENT_TIMESTAMP
);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@kind", entry.StateKind);
                    command.Parameters.AddWithValue("@iid", entry.ItemId);
                    command.Parameters.AddWithValue("@expireTime", entry.ExpireTime);
                    command.ExecuteNonQuery();
                }
            }
        }

        private void SaveItemStateList(
            int characterId,
            string stateKind,
            List<ItemStateEntrySnapshot> items)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand(
                        "DELETE FROM character_item_states WHERE character_id = @cid AND state_kind = @kind",
                        conn,
                        tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@kind", stateKind);
                        cmd.ExecuteNonQuery();
                    }

                    foreach (var item in items)
                    {
                        using (var cmd = new SqliteCommand(
                            @"INSERT INTO character_item_states
                                (character_id, state_kind, item_id, expire_time)
                              VALUES (@cid, @kind, @iid, @expireTime)
                              ON CONFLICT(character_id, state_kind, item_id)
                              DO UPDATE SET expire_time = excluded.expire_time,
                                            updated_at = CURRENT_TIMESTAMP",
                            conn,
                            tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@kind", stateKind);
                            cmd.Parameters.AddWithValue("@iid", item.ItemId);
                            cmd.Parameters.AddWithValue("@expireTime", item.ExpireTime);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                }
            }
        }

        private static void LoadItemStateList(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            string stateKind,
            List<ItemStateEntrySnapshot> output)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_id, expire_time
FROM character_item_states
WHERE character_id = @cid AND state_kind = @kind
ORDER BY item_id;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@kind", stateKind);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        output.Add(new ItemStateEntrySnapshot
                        {
                            ItemId = reader.GetInt32(0),
                            ExpireTime = reader.GetInt32(1),
                        });
                    }
                }
            }
        }
    }
}
