using System;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Names
{
    internal sealed class CharacterRenameRepository
    {
        internal const int CardId = 2682290;
        internal const long CooldownSeconds = 90L * 86400;
        internal const long ReserveSeconds = 180L * 86400;

        private readonly IGameDatabase _database;

        internal CharacterRenameRepository(IGameDatabase database) => _database = database;

        internal static bool IsCard(int id) => id == CardId;

        internal static int MinimumLevel => StackableItemProvider.Load(CardId)?.MinimumLevel ?? int.MaxValue;

        internal bool CanRename(int id, int level, long now) => level >= MinimumLevel
            && _database.Read(c => LastChange(c, null, id) <= now - CooldownSeconds && HasCard(c, id, now));

        internal bool IsNameReserved(byte[] name, long now) => _database.Read(c => Reserved(c, null, name, now));

        internal bool CanPurchase(InventoryService inventory) => _database.Read(c =>
            CanPurchase(c, null, inventory, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        internal static bool CanPurchase(
            SqliteConnection c,
            SqliteTransaction t,
            InventoryService inventory,
            long now)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "SELECT level FROM characters WHERE character_id=@id AND delete_flag=0;";
            cmd.Parameters.AddWithValue("@id", inventory.CharacterId);
            var level = cmd.ExecuteScalar();
            return level != null && Convert.ToInt32(level) >= MinimumLevel
                && inventory.CountMainItem(CardId) == 0
                && LastChange(c, t, inventory.CharacterId) <= now - CooldownSeconds;
        }

        internal static long LastChange(SqliteConnection c, SqliteTransaction t, int id)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "SELECT MAX(changed_at) FROM character_rename_history WHERE character_id=@id;";
            cmd.Parameters.AddWithValue("@id", id);
            var value = cmd.ExecuteScalar();
            return value == DBNull.Value || value == null ? 0 : Convert.ToInt64(value);
        }

        private static bool HasCard(SqliteConnection c, int id, long now)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT item_core FROM character_inventory_items WHERE character_id=@id AND list_type=0;";
            cmd.Parameters.AddWithValue("@id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var bytes = (byte[])reader.GetValue(0);
                if (bytes.Length < ItemCore.LegacySize)
                    continue;
                if (IsUsableCard(ItemCore.FromBytes(bytes), now))
                    return true;
            }
            return false;
        }

        internal static bool IsUsableCard(ItemCore item, long now) => item != null && item.ItemId == CardId
            && item.Count > 0 && (item.ExpireTime == 0 || item.ExpireTime > now);

        internal static bool Reserved(SqliteConnection c, SqliteTransaction t, byte[] name, long now)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "SELECT character_id FROM character_rename_history WHERE old_name=@name AND changed_at>@cutoff LIMIT 1;";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@cutoff", now - ReserveSeconds);
            return cmd.ExecuteScalar() != null;
        }

        internal static bool TryChange(
            SqliteConnection c,
            SqliteTransaction t,
            int account,
            int id,
            byte[] oldName,
            byte[] newName,
            long now)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "SELECT name,level FROM characters WHERE character_id=@id AND account_id=@account AND delete_flag=0;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@account", account);
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()
                    || !((byte[])r.GetValue(0)).AsSpan().SequenceEqual(oldName)
                    || r.GetInt32(1) < MinimumLevel)
                    return false;
            }
            if (LastChange(c, t, id) > now - CooldownSeconds)
                return false;

            cmd.CommandText = "SELECT character_id FROM characters WHERE name=@new LIMIT 1;";
            cmd.Parameters.AddWithValue("@new", newName);
            if (cmd.ExecuteScalar() != null || Reserved(c, t, newName, now))
                return false;

            cmd.CommandText = "UPDATE characters SET name=@new, updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
            cmd.ExecuteNonQuery();

            cmd.Parameters.Clear();
            cmd.CommandText = "INSERT INTO character_rename_history(character_id,changed_at,old_name,new_name,source_item) VALUES(@id,@now,@old,@new,@item);";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@old", oldName);
            cmd.Parameters.AddWithValue("@new", newName);
            cmd.Parameters.AddWithValue("@item", CardId);
            cmd.ExecuteNonQuery();

            // Friend relations use name keys; preserve both directions in this transaction.
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@old", ClientTextEncoding.GetString(oldName));
            cmd.Parameters.AddWithValue("@new", ClientTextEncoding.GetString(newName));
            cmd.CommandText = "UPDATE united_friend_relations SET owner_name=@new WHERE owner_name=@old;"
                + " UPDATE united_friend_relations SET friend_name=@new WHERE friend_name=@old;";
            cmd.ExecuteNonQuery();
            return true;
        }
    }
}
