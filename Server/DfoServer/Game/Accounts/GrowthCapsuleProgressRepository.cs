using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Accounts
{
    public sealed class GrowthCapsuleSummary
    {
        public uint TotalExp { get; set; }
        public uint RequiredExp { get; set; }
    }

    public static class GrowthCapsuleDataProvider
    {
        private static readonly object Sync = new object();
        private static GrowthCapsuleConfig _config;

        public static int ExpGainRatePercent
        {
            get { EnsureLoaded(); return _config.ExpGainRatePercent; }
        }

        public static uint RequiredExpPerCapsule
        {
            get { EnsureLoaded(); return _config.RequiredExpPerCapsule; }
        }

        public static int RewardItemId
        {
            get { EnsureLoaded(); return _config.RewardItemId; }
        }

        public static int RewardItemCount
        {
            get { EnsureLoaded(); return _config.RewardItemCount; }
        }

        public static uint CalculateExpGain(uint honorExpGain)
        {
            if (honorExpGain == 0)
                return 0;

            var rate = Math.Max(0, ExpGainRatePercent);
            var value = (ulong)honorExpGain * (ulong)rate / 100UL;
            if (value == 0 && rate > 0)
                value = 1;
            return value > uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        public static GrowthCapsuleSummary Calculate(ulong totalExp)
        {
            EnsureLoaded();
            var required = RequiredExpPerCapsule;
            var current = (uint)Math.Min(totalExp, required);
            return new GrowthCapsuleSummary
            {
                TotalExp = current,
                RequiredExp = required,
            };
        }

        public static uint GetDisplayProgress(byte characterLevel, GrowthCapsuleSummary summary)
        {
            if (characterLevel < ExpTableProvider.MaxLevel)
                return 0;

            summary = summary ?? Calculate(0);
            return summary.TotalExp;
        }

        private static void EnsureLoaded()
        {
            if (_config != null)
                return;

            lock (Sync)
            {
                if (_config != null)
                    return;

                _config = Parse(PvfArchiveAccessor.ReadText("etc/expandexpgage.etc"));
            }
        }

        private static GrowthCapsuleConfig Parse(string text)
        {
            var root = string.IsNullOrWhiteSpace(text)
                ? null
                : new ScriptParser().Parse(text);
            var config = new GrowthCapsuleConfig
            {
                RequiredExpPerCapsule = (uint)Math.Max(1, ReadSectionInt(root, text, "max gage exp", 11691495)),
                ExpGainRatePercent = Math.Max(0, ReadSectionInt(root, text, "exp gain rate", 100)),
                RewardItemId = Math.Max(1, ReadSectionInt(root, text, "reward item index", 10147584)),
                RewardItemCount = Math.Max(1, ReadSectionInt(root, text, "reward item count", 1)),
            };
            FileLogger.Log($"[GrowthCapsule] PVF config: required={config.RequiredExpPerCapsule} rate={config.ExpGainRatePercent} item={config.RewardItemId} count={config.RewardItemCount}");
            return config;
        }

        private static int ReadSectionInt(
            ScriptNode root,
            string text,
            string tag,
            int fallback)
        {
            var content = root?.GetChild(tag)?.GetFirstDataContent(text);
            if (string.IsNullOrWhiteSpace(content))
                return fallback;

            var number = Regex.Match(content, @"-?\d+");
            if (!number.Success || !long.TryParse(number.Value, out var value))
                return fallback;
            if (value <= 0)
                return 0;
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private sealed class GrowthCapsuleConfig
        {
            public uint RequiredExpPerCapsule { get; set; }
            public int ExpGainRatePercent { get; set; }
            public int RewardItemId { get; set; }
            public int RewardItemCount { get; set; }
        }
    }

    public sealed class GrowthCapsuleProgressRepository
    {
        private readonly string _connectionString;

        public GrowthCapsuleProgressRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public GrowthCapsuleProgressRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public GrowthCapsuleSummary LoadSummary(int accountId)
        {
            if (accountId <= 0)
                return GrowthCapsuleDataProvider.Calculate(0);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return GrowthCapsuleDataProvider.Calculate(LoadTotalExp(connection, null, accountId));
            }
        }

        internal static uint AddExpInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            uint delta,
            out uint appliedDelta)
        {
            var current = LoadTotalExp(connection, transaction, accountId);
            var required = GrowthCapsuleDataProvider.RequiredExpPerCapsule;
            var next = (uint)Math.Min((ulong)required, (ulong)current + delta);
            appliedDelta = next - current;
            UpdateTotalExpInTransaction(connection, transaction, accountId, next);
            return next;
        }

        internal static uint LoadTotalExp(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (accountId <= 0)
                return 0;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT growth_capsule_exp FROM accounts WHERE account_id=@aid;";
                command.Parameters.AddWithValue("@aid", accountId);
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return 0;
                var raw = Convert.ToInt64(value);
                return raw > 0
                    ? (uint)Math.Min((ulong)raw, GrowthCapsuleDataProvider.RequiredExpPerCapsule)
                    : 0;
            }
        }

        internal static void UpdateTotalExpInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            uint totalExp)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (accountId <= 0)
                return;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE accounts
SET growth_capsule_exp=@exp
WHERE account_id=@aid
  AND growth_capsule_exp<>@exp;";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@exp",
                    Math.Min(totalExp, GrowthCapsuleDataProvider.RequiredExpPerCapsule));
                command.ExecuteNonQuery();
            }
        }
    }
}
