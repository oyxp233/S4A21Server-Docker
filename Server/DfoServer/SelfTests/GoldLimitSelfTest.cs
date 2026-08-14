using DfoServer.Game.Currency;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class GoldLimitSelfTest
    {
        private const int AccountId = 926000;
        private const int CharacterId = 926001;
        private const int LowLevelCharacterId = 926002;
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== GOLD_LIMIT selftest ===");

            var parsed = GoldLimitDataProvider.ParseBaseCarryLimits(@"
[gold limit from level]
0 0 1 100000 19 8900000 20 30000000 60 400000000 99 400000000
[/gold limit from level]");
            Check("PVF level pairs parse exactly", parsed[1] == 100000 && parsed[19] == 8900000 && parsed[60] == 400000000);
            Check("expanded client constants are 5/6/7/8 hundred million",
                GoldLimitDataProvider.GetExpandedLimit(1) == 500000000
                && GoldLimitDataProvider.GetExpandedLimit(4) == 800000000);

            var tempDb = Path.Combine(Path.GetTempPath(), "gold_limit_selftest.db");
            DeleteTempDatabase(tempDb);
            var connectionString = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
            Seed(connectionString);
            var repository = new CharacterGoldLimitRepository(tempDb, ServerPaths.SchemaFilePath);

            var initial = repository.LoadOrCreate(CharacterId, 60);
            Check("initial limits persist two independent values",
                initial.GoldCarryLimit == 400000000 && initial.AuctionGoldLimit == 400000000 && initial.UpgradeLevel == 0);

            for (byte expectedLevel = 1; expectedLevel <= GoldLimitDataProvider.MaximumUpgradeLevel; expectedLevel++)
            {
                var upgraded = repository.TryUpgrade(CharacterId);
                var expectedLimit = GoldLimitDataProvider.GetExpandedLimit(expectedLevel);
                Check($"upgrade tier {expectedLevel} synchronizes both limits",
                    upgraded.Status == GoldLimitUpgradeStatus.Success
                    && upgraded.Limits.UpgradeLevel == expectedLevel
                    && upgraded.Limits.GoldCarryLimit == expectedLimit
                    && upgraded.Limits.AuctionGoldLimit == expectedLimit);
            }
            Check("four upgrades deduct 20 million", LoadGold(connectionString, CharacterId) == 10000000);
            var maximum = repository.TryUpgrade(CharacterId);
            Check("fifth upgrade is refused without spending", maximum.Status == GoldLimitUpgradeStatus.AlreadyMaximum && LoadGold(connectionString, CharacterId) == 10000000);

            var lowLevel = repository.TryUpgrade(LowLevelCharacterId);
            Check("characters below level 60 cannot upgrade", lowLevel.Status == GoldLimitUpgradeStatus.LevelTooLow);

            SetGold(connectionString, CharacterId, 799999990);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var granted = CurrencyService.GrantGold(connection, transaction, CharacterId, 100);
                    transaction.Commit();
                    Check("gold grant reports actual capped amount", granted == 10);
                }
            }
            Check("gold balance is capped at effective carry limit", LoadGold(connectionString, CharacterId) == 800000000);

            var previousDatabasePath = Environment.GetEnvironmentVariable("INVENTORY_DATABASE_PATH");
            Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", tempDb);
            try
            {
                var dropService = new DropService();
                var inventory = new InventoryService(CharacterId, AccountId);
                var lease = new InventoryLease(Guid.NewGuid(), CharacterId, inventory, 1);
                var run = new DungeonRun();

                SetGold(connectionString, CharacterId, 799999990);
                inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 799999990);
                run.Drops[1] = new DropInfo { SceneSlot = 1, TemplateId = 0, StackCount = 100 };
                var partialPickup = dropService.TryPickup(run, 1, lease);
                Check("gold pickup reports only the amount that fits",
                    partialPickup.Success && partialPickup.GoldAmount == 10 && partialPickup.ExtraGold == 0);
                Check("partially swallowed gold drop is removed and balance stays capped",
                    !run.Drops.ContainsKey(1)
                    && inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart) == 800000000);

                run.Drops[2] = new DropInfo { SceneSlot = 2, TemplateId = 0, StackCount = 100 };
                var fullPickup = dropService.TryPickup(run, 2, lease);
                Check("gold pickup at cap reports zero credited gold",
                    fullPickup.Success && fullPickup.GoldAmount == 0 && fullPickup.ExtraGold == 0);
                Check("fully swallowed gold drop is removed and balance remains capped",
                    !run.Drops.ContainsKey(2)
                    && inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart) == 800000000);
            }
            finally
            {
                Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", previousDatabasePath);
            }

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void Seed(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash) VALUES(@aid, 'gold-limit-selftest', '');
INSERT INTO characters(character_id, account_id, name, level) VALUES(@cid, @aid, 'gold-limit-main', 60);
INSERT INTO characters(character_id, account_id, name, level) VALUES(@lowCid, @aid, 'gold-limit-low', 59);";
                        command.Parameters.AddWithValue("@aid", AccountId);
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.Parameters.AddWithValue("@lowCid", LowLevelCharacterId);
                        command.ExecuteNonQuery();
                    }

                    InventoryMainVirtualCountRepository.UpsertCurrencySlot(connection, transaction, CharacterId, 0, 30000000);
                    InventoryMainVirtualCountRepository.UpsertCurrencySlot(connection, transaction, LowLevelCharacterId, 0, 30000000);
                    transaction.Commit();
                }
            }
        }

        private static int LoadGold(string connectionString, int characterId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                return InventoryMainVirtualCountRepository.LoadCurrencyCount(connection, null, characterId, 0);
            }
        }

        private static void SetGold(string connectionString, int characterId, int gold)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    InventoryMainVirtualCountRepository.UpsertCurrencySlot(connection, transaction, characterId, 0, gold);
                    transaction.Commit();
                }
            }
        }

        private static void DeleteTempDatabase(string path)
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++; else _fail++;
        }
    }
}
