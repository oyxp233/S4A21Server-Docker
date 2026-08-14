using System;
using System.IO;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.ReviveCoin;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class ReviveCoinSelfTest
    {
        private const int AccountId = 930017;
        private const int CharacterId = 930117;
        private const int Coin = ReviveCoinService.ItemId;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== REVIVE_COIN selftest ===");

            var tempDb = Path.Combine(Path.GetTempPath(), "revive_coin_selftest.db");
            DeleteTempDatabase(tempDb);
            var database = new GameDatabase(
                tempDb,
                ServerPaths.SchemaFilePath);
            Seed(database.ConnectionString);

            var dailyReset = new DailyResetService(database);
            var reviveCoin = new ReviveCoinService(dailyReset);
            var inventory = new InventoryService(
                CharacterId,
                AccountId,
                database);
            var lease = InventoryContext.Register(
                Guid.NewGuid(),
                CharacterId,
                inventory);

            Check(
                "复活币映射到虚拟 slot1",
                InventoryService.TryResolveMainVirtualSlotByItemId(
                    Coin,
                    out var virtualSlot,
                    out var virtualItemId)
                && virtualSlot == ReviveCoinService.WalletSlot
                && virtualItemId == Coin);
            Check(
                "复活币礼盒保留为实体背包道具",
                !InventoryService.TryResolveMainVirtualSlotByItemId(
                    ReviveCoinService.ConsumableItemId,
                    out _,
                    out _));
            Check("发放复活币成功", inventory.SetMainVirtualCount(ReviveCoinService.WalletSlot, Coin, 3));
            Check("复活币落在 slot1", inventory.GetMainVirtualCount(ReviveCoinService.WalletSlot)?.ItemId == Coin);
            Check("计数=3", inventory.CountMainItem(Coin) == 3);
            Check("扣除3枚成功",
                inventory.TryConsumeMainItem(Coin, 3, out var bulk)
                && bulk.Success
                && bulk.RemainingCount == 0);
            Check("扣光后计数0", inventory.CountMainItem(Coin) == 0);
            Check("重建发放成功", inventory.SetMainVirtualCount(ReviveCoinService.WalletSlot, Coin, 1));
            Check("重建仍落 slot1", inventory.GetMainVirtualCount(ReviveCoinService.WalletSlot)?.Count == 1);

            short grantSlot;
            Check("有币时领取被拒", !reviveCoin.TryGrantDaily(lease, out grantSlot));
            Check("被拒不消耗当日标记", !dailyReset.IsClaimed(CharacterId, ReviveCoinService.DailyClaimKey));

            short useSlot;
            int useRemaining;
            Check("消耗1枚成功", reviveCoin.TryConsume(lease, out useSlot, out useRemaining));
            Check("消耗后剩0", useRemaining == 0 && inventory.CountMainItem(Coin) == 0);

            Check("无币未领时领取成功", reviveCoin.TryGrantDaily(lease, out grantSlot));
            Check("领取落 slot1", grantSlot == ReviveCoinService.WalletSlot);
            Check("领取后计数1", inventory.CountMainItem(Coin) == 1);
            Check("领取后当日已领", dailyReset.IsClaimed(CharacterId, ReviveCoinService.DailyClaimKey));
            Check("当日二次领取被拒", !reviveCoin.TryGrantDaily(lease, out grantSlot));

            Check("再消耗成功", reviveCoin.TryConsume(lease, out useSlot, out useRemaining));
            Check("无币时消耗被拒", !reviveCoin.TryConsume(lease, out useSlot, out useRemaining));
            Check("扣光后当日仍不可再领", !reviveCoin.TryGrantDaily(lease, out grantSlot));

            InventoryContext.Unregister(
                lease.SessionId,
                lease.CharacterId);
            DeleteTempDatabase(tempDb);
            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static void Seed(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash) VALUES (@aid, 'revive-coin-selftest', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name) VALUES (@cid, @aid, 'revive-coin-selftest');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            try
            {
                if (File.Exists(databasePath)) File.Delete(databasePath);
                if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
                if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
            }
            catch
            {
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}
