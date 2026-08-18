using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Currency
{
    public static class CurrencyService
    {
        private static readonly Dictionary<int, (string ColumnName, int Slot)> CubeFragmentMap =
            new Dictionary<int, (string, int)>
            {
                { 3033, ("cube_black", 354) },
                { 3034, ("cube_white", 355) },
                { 3035, ("cube_red", 356) },
                { 3036, ("cube_blue", 357) },
                { 3037, ("cube_clear", 358) },
                { 3262, ("cube_gold", 359) },
            };

        private static readonly Dictionary<int, (string ColumnName, int Slot)> SoulWarehouseMap =
            new Dictionary<int, (string, int)>
            {
                { 10100115, ("soul_10100115", 360) },
                { 10100116, ("soul_10100116", 361) },
                { 10099773, ("soul_10099773", 362) },
                { 10099774, ("soul_10099774", 363) },
                { 10099775, ("soul_10099775", 364) },
            };

        public const int CubeFragmentSlotStart = 354;
        public const int CubeFragmentSlotEnd = 359;

        public const int SoulWarehouseSlotStart = 360;
        public const int SoulWarehouseSlotEnd = 364;

        public static bool IsCubeFragment(int itemId)
        {
            return CubeFragmentMap.ContainsKey(itemId);
        }

        public static int GetCubeFragmentSlot(int itemId)
        {
            return CubeFragmentMap.TryGetValue(itemId, out var entry) ? entry.Slot : -1;
        }

        public static int GetCubeFragmentItemIdFromSlot(int slot)
        {
            return GetItemIdFromSlot(CubeFragmentMap, slot);
        }

        public static bool IsCubeFragmentSlot(int slot)
        {
            return slot >= CubeFragmentSlotStart && slot <= CubeFragmentSlotEnd;
        }

        public static bool IsSoulWarehouseItem(int itemId)
        {
            return SoulWarehouseMap.ContainsKey(itemId);
        }

        public static int GetSoulWarehouseSlot(int itemId)
        {
            return SoulWarehouseMap.TryGetValue(itemId, out var entry) ? entry.Slot : -1;
        }

        public static int GetSoulWarehouseItemIdFromSlot(int slot)
        {
            return GetItemIdFromSlot(SoulWarehouseMap, slot);
        }

        public static bool IsSoulWarehouseSlot(int slot)
        {
            return slot >= SoulWarehouseSlotStart && slot <= SoulWarehouseSlotEnd;
        }

        public static bool IsAccountWarehouseItem(int itemId)
        {
            return IsCubeFragment(itemId) || IsSoulWarehouseItem(itemId);
        }

        public static int GetAccountWarehouseSlot(int itemId)
        {
            var slot = GetCubeFragmentSlot(itemId);
            if (slot >= 0)
                return slot;

            return GetSoulWarehouseSlot(itemId);
        }

        public static int GetAccountWarehouseItemIdFromSlot(int slot)
        {
            var itemId = GetCubeFragmentItemIdFromSlot(slot);
            if (itemId > 0)
                return itemId;

            return GetSoulWarehouseItemIdFromSlot(slot);
        }

        public static bool IsAccountWarehouseSlot(int slot)
        {
            return IsCubeFragmentSlot(slot) || IsSoulWarehouseSlot(slot);
        }

        public static List<(int ItemId, int Slot, int Count)> LoadCubeFragments(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId)
        {
            var result = new List<(int, int, int)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT cube_black, cube_white, cube_red, cube_blue, cube_clear, cube_gold FROM accounts WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        result.Add((3033, 354, reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0))));
                        result.Add((3034, 355, reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1))));
                        result.Add((3035, 356, reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2))));
                        result.Add((3036, 357, reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3))));
                        result.Add((3037, 358, reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4))));
                        result.Add((3262, 359, reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5))));
                    }
                }
            }

            return result;
        }

        public static List<(int ItemId, int Slot, int Count)> LoadSoulWarehouseCounts(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId)
        {
            var result = new List<(int, int, int)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT soul_10100115, soul_10100116, soul_10099773, soul_10099774, soul_10099775 FROM accounts WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        result.Add((10100115, 360, reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0))));
                        result.Add((10100116, 361, reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1))));
                        result.Add((10099773, 362, reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2))));
                        result.Add((10099774, 363, reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3))));
                        result.Add((10099775, 364, reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4))));
                    }
                }
            }

            return result;
        }

        public static void AddCubeFragment(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId,
            int itemId,
            int count)
        {
            if (!CubeFragmentMap.TryGetValue(itemId, out var entry))
                throw new ArgumentException($"itemId {itemId} is not a cube fragment");

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"UPDATE accounts SET {entry.ColumnName} = {entry.ColumnName} + @count WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@count", count);
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.ExecuteNonQuery();
            }
        }

        public static void SetCubeFragmentCount(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId,
            int itemId,
            int count)
        {
            if (!CubeFragmentMap.TryGetValue(itemId, out var entry))
                throw new ArgumentException($"itemId {itemId} is not a cube fragment");

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"UPDATE accounts SET {entry.ColumnName} = @count WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@count", Math.Max(0, count));
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.ExecuteNonQuery();
            }
        }

        public static void SetSoulWarehouseCount(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId,
            int itemId,
            int count)
        {
            if (!SoulWarehouseMap.TryGetValue(itemId, out var entry))
                throw new ArgumentException($"itemId {itemId} is not a soul warehouse item");

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"UPDATE accounts SET {entry.ColumnName} = @count WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@count", Math.Max(0, count));
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.ExecuteNonQuery();
            }
        }

        public static WalletSnapshot LoadWallet(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var wallet = new WalletSnapshot { Gold = LoadGold(connection, transaction, characterId) };
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT a.cera, a.token_cera, a.happy_token_cera, a.lucky_star
FROM accounts a
JOIN characters c ON c.account_id = a.account_id
WHERE c.character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        wallet.Cera = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                        wallet.TokenCera = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                        wallet.HappyTokenCera = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                        wallet.LuckyStar = reader.IsDBNull(3) ? (ushort)0 : NormalizeLuckyStar(Convert.ToInt32(reader.GetValue(3)));
                    }
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT a.cube_black, a.cube_white, a.cube_red, a.cube_blue, a.cube_clear, a.cube_gold
FROM accounts a
JOIN characters c ON c.account_id = a.account_id
WHERE c.character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        wallet.CubeBlack = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                        wallet.CubeWhite = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                        wallet.CubeRed = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                        wallet.CubeBlue = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3));
                        wallet.CubeClear = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
                        wallet.CubeGold = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5));
                    }
                }
            }

            return wallet;
        }

        public static int GrantGold(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "GrantGold amount must be >= 0; use TrySpendGold to deduct");
            if (amount == 0)
                return 0;

            var limit = CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(
                connection,
                transaction,
                characterId);

            return InventoryMainVirtualCountRepository.GrantCurrency(
                connection,
                transaction,
                characterId,
                InventoryService.MainVirtualCurrencySlotStart,
                amount,
                limit);
        }

        public static bool TrySpendGold(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "TrySpendGold amount must be >= 0");
            if (amount == 0)
                return true;

            return InventoryMainVirtualCountRepository.TrySpendCurrency(
                connection,
                transaction,
                characterId,
                InventoryService.MainVirtualCurrencySlotStart,
                amount);
        }

        public static void GrantCera(SqliteConnection connection, SqliteTransaction transaction, int characterId, int amount)
            => GrantAccountCurrency(connection, transaction, characterId, "cera", amount);

        public static bool TrySpendCera(SqliteConnection connection, SqliteTransaction transaction, int characterId, int amount)
            => TrySpendAccountCurrency(connection, transaction, characterId, "cera", amount);

        public static void GrantTokenCera(SqliteConnection connection, SqliteTransaction transaction, int characterId, int amount)
            => GrantAccountCurrency(connection, transaction, characterId, "token_cera", amount);

        public static bool TrySpendTokenCera(SqliteConnection connection, SqliteTransaction transaction, int characterId, int amount)
            => TrySpendAccountCurrency(connection, transaction, characterId, "token_cera", amount);

        public static void GrantHappyTokenCera(SqliteConnection connection, SqliteTransaction transaction, int characterId, int amount)
            => GrantAccountCurrency(connection, transaction, characterId, "happy_token_cera", amount);

        public static bool TrySpendHappyTokenCera(SqliteConnection connection, SqliteTransaction transaction, int characterId, int amount)
            => TrySpendAccountCurrency(connection, transaction, characterId, "happy_token_cera", amount);

        public static void GrantLuckyStar(SqliteConnection connection, SqliteTransaction transaction, int accountId, int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "GrantLuckyStar amount must be >= 0; use TrySpendLuckyStar to deduct");
            if (amount == 0)
                return;

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE accounts
SET lucky_star = MIN(999, lucky_star + @amt)
WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@amt", amount);
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.ExecuteNonQuery();
            }
        }

        public static bool TrySpendLuckyStar(SqliteConnection connection, SqliteTransaction transaction, int accountId, int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "TrySpendLuckyStar amount must be >= 0");
            if (amount == 0)
                return true;

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE accounts
SET lucky_star = lucky_star - @amt
WHERE account_id = @aid AND lucky_star >= @amt;";
                cmd.Parameters.AddWithValue("@amt", amount);
                cmd.Parameters.AddWithValue("@aid", accountId);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static void GrantAccountCurrency(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            string column,
            int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, $"Grant {column} amount must be >= 0; use TrySpend to deduct");
            if (amount == 0)
                return;

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $@"
UPDATE accounts
SET {column} = {column} + @amt
WHERE account_id = (SELECT account_id FROM characters WHERE character_id = @cid);";
                cmd.Parameters.AddWithValue("@amt", amount);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        private static bool TrySpendAccountCurrency(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            string column,
            int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, $"TrySpend {column} amount must be >= 0");
            if (amount == 0)
                return true;

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $@"
UPDATE accounts
SET {column} = {column} - @amt
WHERE account_id = (SELECT account_id FROM characters WHERE character_id = @cid)
  AND {column} >= @amt;";
                cmd.Parameters.AddWithValue("@amt", amount);
                cmd.Parameters.AddWithValue("@cid", characterId);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static ushort NormalizeLuckyStar(int value)
        {
            if (value <= 0)
                return 0;
            if (value > 999)
                return 999;
            return (ushort)value;
        }

        private static int LoadGold(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            return InventoryMainVirtualCountRepository.LoadCurrencyCount(
                connection,
                transaction,
                characterId,
                InventoryService.MainVirtualCurrencySlotStart);
        }

        private static int GetItemIdFromSlot(
            Dictionary<int, (string ColumnName, int Slot)> map,
            int slot)
        {
            foreach (var kv in map)
            {
                if (kv.Value.Slot == slot)
                    return kv.Key;
            }

            return -1;
        }
    }

    public sealed class WalletSnapshot
    {
        public int Gold { get; set; }

        public int Cera { get; set; }

        public int Sp { get; set; }

        public int TokenCera { get; set; }

        public int HappyTokenCera { get; set; }

        public ushort LuckyStar { get; set; }

        public int CubeBlack { get; set; }

        public int CubeWhite { get; set; }

        public int CubeRed { get; set; }

        public int CubeBlue { get; set; }

        public int CubeClear { get; set; }

        public int CubeGold { get; set; }
    }
}
