using DfoServer.Game.Currency;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    // Uses a disposable database; PVF_ARCHIVE_PATH supplies the base carry-limit table.
    public static class GoldLimitSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== GOLD_LIMIT selftest ===");
            var failures = 0;
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_gold_limit_{Guid.NewGuid():N}.db");

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                const int accountId = 61001;
                const int upgradedCharacterId = 61002;
                const int insufficientCharacterId = 61003;
                const int lowLevelCharacterId = 61004;

                SeedAccount(database, accountId);
                SeedCharacter(database, upgradedCharacterId, accountId, 100, "gold-limit-upgrade");
                SeedCharacter(database, insufficientCharacterId, accountId, 100, "gold-limit-insufficient");
                SeedCharacter(database, lowLevelCharacterId, accountId, 59, "gold-limit-low-level");
                GrantGold(database, upgradedCharacterId, 25_000_000);
                GrantGold(database, insufficientCharacterId, GoldLimitDataProvider.UpgradeCost - 1);
                GrantGold(database, lowLevelCharacterId, GoldLimitDataProvider.UpgradeCost);

                var repository = new CharacterGoldLimitRepository(database);
                var first = repository.TryUpgrade(upgradedCharacterId);
                Check(
                    "first upgrade costs five million and reaches 500M",
                    first.Status == GoldLimitUpgradeStatus.Success
                    && first.Limits.UpgradeLevel == 1
                    && first.Limits.GoldCarryLimit == 500_000_000
                    && first.Limits.AuctionGoldLimit == 500_000_000
                    && first.GoldAfter == 20_000_000,
                    ref failures);

                var second = repository.TryUpgrade(upgradedCharacterId);
                var third = repository.TryUpgrade(upgradedCharacterId);
                var fourth = repository.TryUpgrade(upgradedCharacterId);
                Check(
                    "each subsequent upgrade advances exactly one tier",
                    second.Status == GoldLimitUpgradeStatus.Success
                    && second.Limits.UpgradeLevel == 2
                    && second.Limits.GoldCarryLimit == 600_000_000
                    && third.Status == GoldLimitUpgradeStatus.Success
                    && third.Limits.UpgradeLevel == 3
                    && third.Limits.GoldCarryLimit == 700_000_000
                    && fourth.Status == GoldLimitUpgradeStatus.Success
                    && fourth.Limits.UpgradeLevel == 4
                    && fourth.Limits.GoldCarryLimit == 800_000_000
                    && fourth.GoldAfter == 5_000_000,
                    ref failures);

                var maximum = repository.TryUpgrade(upgradedCharacterId);
                Check(
                    "maximum tier does not deduct gold again",
                    maximum.Status == GoldLimitUpgradeStatus.AlreadyMaximum
                    && maximum.Limits.UpgradeLevel == 4
                    && maximum.GoldAfter == 5_000_000
                    && ReadGold(database, upgradedCharacterId) == 5_000_000,
                    ref failures);

                var insufficient = repository.TryUpgrade(insufficientCharacterId);
                Check(
                    "insufficient gold does not change tier or balance",
                    insufficient.Status == GoldLimitUpgradeStatus.InsufficientGold
                    && insufficient.Limits.UpgradeLevel == 0
                    && insufficient.GoldAfter == GoldLimitDataProvider.UpgradeCost - 1
                    && ReadGold(database, insufficientCharacterId) == GoldLimitDataProvider.UpgradeCost - 1,
                    ref failures);

                var lowLevel = repository.TryUpgrade(lowLevelCharacterId);
                Check(
                    "characters below level 60 cannot upgrade",
                    lowLevel.Status == GoldLimitUpgradeStatus.LevelTooLow
                    && lowLevel.Limits.UpgradeLevel == 0
                    && lowLevel.GoldAfter == GoldLimitDataProvider.UpgradeCost
                    && ReadGold(database, lowLevelCharacterId) == GoldLimitDataProvider.UpgradeCost,
                    ref failures);

                Check(
                    "gold limits remain character-scoped",
                    ReadLimit(database, upgradedCharacterId) == 800_000_000
                    && ReadLimit(database, insufficientCharacterId) < 500_000_000
                    && ReadLimit(database, lowLevelCharacterId) < 500_000_000,
                    ref failures);

                Check(
                    "A21 carry-gold request and notification opcodes match client protocol",
                    (ushort)CmdPacketTypeA21.UPGRADE_CARRY_GOLD == 0x03BA
                    && (ushort)NotiPacketTypeA21.UPGRADE_CARRY_GOLD == 0x039B,
                    ref failures);

                var requestBody = new byte[GoldLimitUpgradeRequest.WireBodyLength];
                requestBody[requestBody.Length - 1] = 1;
                Check(
                    "captured 0x03BA request accepts exactly 15B",
                    GoldLimitUpgradeRequest.TryParse(requestBody, out _)
                    && !GoldLimitUpgradeRequest.TryParse(new byte[14], out _)
                    && !GoldLimitUpgradeRequest.TryParse(new byte[16], out _),
                    ref failures);

                var upgradeNotice = GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.UPGRADE_CARRY_GOLD,
                    new byte[] { 1 });
                Check(
                    "captured 0x039B notification is a 16B packet with one tier byte",
                    upgradeNotice.Length == 16
                    && upgradeNotice[0] == 0x00
                    && BitConverter.ToUInt16(upgradeNotice, 1)
                        == (ushort)NotiPacketTypeA21.UPGRADE_CARRY_GOLD
                    && BitConverter.ToInt32(upgradeNotice, 3) == 16
                    && upgradeNotice[15] == 1,
                    ref failures);

                var registry = new InitPacketBuilderRegistry(database);
                var initSnapshot = new SelectCharacterDataSnapshot
                {
                    InitializationSnapshot = new SelectCharacterInitializationSnapshot
                    {
                        GoldLimitUpgradeLevel = 3,
                    },
                };
                Check(
                    "login initialization projects the A21 carry-gold tier",
                    registry.TryBuild(
                        (ushort)NotiPacketTypeA21.UPGRADE_CARRY_GOLD,
                        initSnapshot,
                        0,
                        out var initBody)
                    && initBody.Length == 1
                    && initBody[0] == 3,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GOLD_LIMIT] EXCEPTION: {ex}");
                failures++;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempDbPath))
                        File.Delete(tempDbPath);
                }
                catch
                {
                }
            }

            Console.WriteLine(failures == 0
                ? "GOLD_LIMIT selftest passed."
                : $"GOLD_LIMIT selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void SeedAccount(GameDatabase database, int accountId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, @mid, '');";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@mid", $"gold-limit-account-{accountId}");
                command.ExecuteNonQuery();
            }
        }

        private static void SeedCharacter(
            GameDatabase database,
            int characterId,
            int accountId,
            int level,
            string name)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO characters(character_id, account_id, name, level)
VALUES(@characterId, @accountId, @name, @level);";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@level", level);
                command.ExecuteNonQuery();
            }
        }

        private static void GrantGold(GameDatabase database, int characterId, int amount)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                CurrencyService.GrantGold(connection, transaction, characterId, amount);
                transaction.Commit();
            }
        }

        private static int ReadGold(GameDatabase database, int characterId)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                return CurrencyService.LoadWallet(connection, transaction, characterId).Gold;
            }
        }

        private static int ReadLimit(GameDatabase database, int characterId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT gold_carry_limit
FROM character_gold_limits
WHERE character_id=@characterId;";
                command.Parameters.AddWithValue("@characterId", characterId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
            }
        }

        private static void Check(string name, bool passed, ref int failures)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed)
                failures++;
        }
    }
}
