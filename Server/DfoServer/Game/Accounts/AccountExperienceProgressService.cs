using DfoServer.Game.Characters;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Accounts
{
    internal readonly struct AccountExperienceProgressTotals
    {
        public AccountExperienceProgressTotals(
            ulong totalHonorExp,
            uint totalGrowthCapsuleExp,
            uint growthCapsuleExpGain)
        {
            TotalHonorExp = totalHonorExp;
            TotalGrowthCapsuleExp = totalGrowthCapsuleExp;
            GrowthCapsuleExpGain = growthCapsuleExpGain;
        }

        public ulong TotalHonorExp { get; }
        public uint TotalGrowthCapsuleExp { get; }
        public uint GrowthCapsuleExpGain { get; }
    }

    public sealed class AccountExperienceProgressSummary
    {
        public HonorLevelSummary Honor { get; set; }
        public GrowthCapsuleSummary GrowthCapsule { get; set; }
        public uint GrowthCapsuleExpGain { get; set; }
    }

    public sealed class AccountExperienceProgressService
    {
        private readonly string _connectionString;
        private readonly ICharacterRepository _characterRepository;
        private readonly GrowthCapsuleProgressRepository _growthCapsuleRepository;

        public AccountExperienceProgressService(ICharacterRepository characterRepository)
            : this(characterRepository, ServerPaths.DatabasePath, ServerPaths.SchemaFilePath)
        {
        }

        public AccountExperienceProgressService(
            ICharacterRepository characterRepository,
            string databasePath,
            string schemaFilePath)
            : this(
                characterRepository,
                new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public AccountExperienceProgressService(
            ICharacterRepository characterRepository,
            IGameDatabase database)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            if (database == null)
                throw new ArgumentNullException(nameof(database));
            _connectionString = database.ConnectionString;
            _growthCapsuleRepository = new GrowthCapsuleProgressRepository(database);
        }

        public GrowthCapsuleSummary LoadGrowthCapsule(int accountId)
        {
            return _growthCapsuleRepository.LoadSummary(accountId);
        }

        public AccountExperienceProgressSummary AddHonorAndGrowthCapsuleExp(int accountId, uint honorExpGain)
        {
            if (accountId <= 0)
                return BuildSummary(accountId, default);

            AccountExperienceProgressTotals totals;
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    totals = AddInTransaction(
                        connection, transaction, accountId, honorExpGain);
                    transaction.Commit();
                }
            }

            return BuildSummary(accountId, totals);
        }

        internal static AccountExperienceProgressTotals AddInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            uint honorExpGain)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (accountId <= 0)
                return default;

            var growthCapsuleExpGain = GrowthCapsuleDataProvider.CalculateExpGain(honorExpGain);
            var totalHonorExp = HonorLevelProgressRepository.AddHonorExpInTransaction(
                connection, transaction, accountId, honorExpGain);
            var totalGrowthCapsuleExp = GrowthCapsuleProgressRepository.AddExpInTransaction(
                connection, transaction, accountId, growthCapsuleExpGain, out var appliedGrowthCapsuleExp);
            return new AccountExperienceProgressTotals(
                totalHonorExp,
                totalGrowthCapsuleExp,
                appliedGrowthCapsuleExp);
        }

        internal AccountExperienceProgressSummary BuildSummary(
            int accountId,
            AccountExperienceProgressTotals totals)
        {
            var characters = accountId > 0 ? _characterRepository?.ListByAccount(accountId) : null;
            return new AccountExperienceProgressSummary
            {
                Honor = HonorLevelDataProvider.CalculateFromHonorExp(totals.TotalHonorExp, characters),
                GrowthCapsule = GrowthCapsuleDataProvider.Calculate(totals.TotalGrowthCapsuleExp),
                GrowthCapsuleExpGain = totals.GrowthCapsuleExpGain,
            };
        }
    }
}
