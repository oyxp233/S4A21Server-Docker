using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Currency
{
    public sealed class CharacterGoldLimitSnapshot
    {
        public int GoldCarryLimit { get; set; }
        public int AuctionGoldLimit { get; set; }
        public byte UpgradeLevel { get; set; }
    }

    public enum GoldLimitUpgradeStatus
    {
        Success,
        CharacterNotFound,
        LevelTooLow,
        AlreadyMaximum,
        InsufficientGold,
    }

    public sealed class GoldLimitUpgradeResult
    {
        public GoldLimitUpgradeStatus Status { get; set; }
        public CharacterGoldLimitSnapshot Limits { get; set; }
        public int GoldAfter { get; set; }
    }

    public static class GoldLimitDataProvider
    {
        public const int UpgradeCost = 5_000_000;
        public const int BaseAuctionGoldLimit = 400_000_000;
        public const byte MinimumUpgradeCharacterLevel = 60;

        private static readonly int[] ExpandedLimits =
        {
            0, 500_000_000, 600_000_000, 700_000_000, 800_000_000,
        };

        public static byte MaximumUpgradeLevel => (byte)(ExpandedLimits.Length - 1);

        private static readonly object Sync = new object();
        private static IReadOnlyDictionary<int, int> _baseCarryLimits;

        public static int GetBaseCarryLimit(int level)
        {
            EnsureLoaded();
            if (level < 0)
                level = 0;
            if (level > 99)
                level = 99;
            return _baseCarryLimits.TryGetValue(level, out var value) ? value : 0;
        }

        public static int GetExpandedLimit(byte upgradeLevel)
        {
            var index = Math.Max(0, Math.Min(ExpandedLimits.Length - 1, upgradeLevel));
            return ExpandedLimits[index];
        }

        public static byte ResolveUpgradeLevel(int goldCarryLimit, int auctionGoldLimit)
        {
            var synchronizedLimit = Math.Min(goldCarryLimit, auctionGoldLimit);
            for (var level = ExpandedLimits.Length - 1; level >= 1; level--)
            {
                if (synchronizedLimit >= ExpandedLimits[level])
                    return (byte)level;
            }
            return 0;
        }

        internal static IReadOnlyDictionary<int, int> ParseBaseCarryLimits(string text)
        {
            var result = new Dictionary<int, int>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            var start = text.IndexOf("[gold limit from level]", StringComparison.OrdinalIgnoreCase);
            var end = text.IndexOf("[/gold limit from level]", StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
                start += "[gold limit from level]".Length;
            else
                start = 0;
            if (end < start)
                end = text.Length;

            var numbers = Regex.Matches(text.Substring(start, end - start), @"\d+");
            for (var i = 0; i + 1 < numbers.Count; i += 2)
            {
                if (int.TryParse(numbers[i].Value, out var level)
                    && int.TryParse(numbers[i + 1].Value, out var limit))
                    result[level] = Math.Max(0, limit);
            }
            return result;
        }

        private static void EnsureLoaded()
        {
            if (_baseCarryLimits != null)
                return;
            lock (Sync)
            {
                if (_baseCarryLimits != null)
                    return;

                var parsed = ParseBaseCarryLimits(PvfArchiveAccessor.ReadText("etc/(r)goldlimitbylevel.etc"));
                if (parsed.Count < 100)
                    throw new InvalidOperationException("PVF gold limit table is incomplete");
                _baseCarryLimits = parsed;
                FileLogger.Log($"[GoldLimit] PVF base limits loaded: levels={parsed.Count} lv60={parsed[60]}");
            }
        }
    }

    public sealed class CharacterGoldLimitRepository
    {
        private readonly string _connectionString;

        public CharacterGoldLimitRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public CharacterGoldLimitRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public CharacterGoldLimitSnapshot LoadOrCreate(int characterId, int characterLevel)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var result = LoadOrCreate(connection, transaction, characterId, characterLevel);
                    transaction.Commit();
                    return result;
                }
            }
        }

        public GoldLimitUpgradeResult TryUpgrade(int characterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var characterLevel = LoadCharacterLevel(connection, transaction, characterId);
                    if (characterLevel < 0)
                        return new GoldLimitUpgradeResult { Status = GoldLimitUpgradeStatus.CharacterNotFound };

                    var current = LoadOrCreate(connection, transaction, characterId, characterLevel);
                    if (characterLevel < GoldLimitDataProvider.MinimumUpgradeCharacterLevel)
                        return Failure(GoldLimitUpgradeStatus.LevelTooLow, current, connection, transaction, characterId);
                    if (current.UpgradeLevel >= GoldLimitDataProvider.MaximumUpgradeLevel)
                        return Failure(GoldLimitUpgradeStatus.AlreadyMaximum, current, connection, transaction, characterId);
                    if (!CurrencyService.TrySpendGold(connection, transaction, characterId, GoldLimitDataProvider.UpgradeCost))
                        return Failure(GoldLimitUpgradeStatus.InsufficientGold, current, connection, transaction, characterId);

                    var nextLevel = (byte)(current.UpgradeLevel + 1);
                    var nextLimit = GoldLimitDataProvider.GetExpandedLimit(nextLevel);
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
UPDATE character_gold_limits
SET gold_carry_limit=@limit,
    auction_gold_limit=@limit,
    updated_at=CURRENT_TIMESTAMP
WHERE character_id=@cid;";
                        command.Parameters.AddWithValue("@limit", nextLimit);
                        command.Parameters.AddWithValue("@cid", characterId);
                        command.ExecuteNonQuery();
                    }

                    var wallet = CurrencyService.LoadWallet(connection, transaction, characterId);
                    transaction.Commit();
                    return new GoldLimitUpgradeResult
                    {
                        Status = GoldLimitUpgradeStatus.Success,
                        GoldAfter = wallet.Gold,
                        Limits = new CharacterGoldLimitSnapshot
                        {
                            GoldCarryLimit = nextLimit,
                            AuctionGoldLimit = nextLimit,
                            UpgradeLevel = nextLevel,
                        },
                    };
                }
            }
        }

        internal static int LoadEffectiveGoldCarryLimit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var characterLevel = LoadCharacterLevel(connection, transaction, characterId);
            if (characterLevel < 0)
                return int.MaxValue;

            var baseLimit = GoldLimitDataProvider.GetBaseCarryLimit(characterLevel);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT gold_carry_limit FROM character_gold_limits WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                var raw = command.ExecuteScalar();
                var saved = raw == null || raw == DBNull.Value ? 0 : Convert.ToInt32(raw);
                return Math.Max(baseLimit, saved);
            }
        }

        private static GoldLimitUpgradeResult Failure(
            GoldLimitUpgradeStatus status,
            CharacterGoldLimitSnapshot limits,
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var wallet = CurrencyService.LoadWallet(connection, transaction, characterId);
            transaction.Commit();
            return new GoldLimitUpgradeResult { Status = status, Limits = limits, GoldAfter = wallet.Gold };
        }

        private static CharacterGoldLimitSnapshot LoadOrCreate(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int characterLevel)
        {
            var baseCarryLimit = GoldLimitDataProvider.GetBaseCarryLimit(characterLevel);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_gold_limits(character_id, gold_carry_limit, auction_gold_limit)
VALUES(@cid, @carry, @auction)
ON CONFLICT(character_id) DO UPDATE SET
    gold_carry_limit=MAX(character_gold_limits.gold_carry_limit, @carry);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@carry", baseCarryLimit);
                command.Parameters.AddWithValue("@auction", GoldLimitDataProvider.BaseAuctionGoldLimit);
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT gold_carry_limit, auction_gold_limit
FROM character_gold_limits
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        throw new InvalidOperationException($"gold limits missing for character {characterId}");
                    var gold = reader.GetInt32(0);
                    var auction = reader.GetInt32(1);
                    return new CharacterGoldLimitSnapshot
                    {
                        GoldCarryLimit = gold,
                        AuctionGoldLimit = auction,
                        UpgradeLevel = GoldLimitDataProvider.ResolveUpgradeLevel(gold, auction),
                    };
                }
            }
        }

        private static int LoadCharacterLevel(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT level FROM characters WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                var raw = command.ExecuteScalar();
                return raw == null || raw == DBNull.Value ? -1 : Convert.ToInt32(raw);
            }
        }
    }
}
