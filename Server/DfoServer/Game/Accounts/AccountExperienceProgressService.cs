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
        }

        // 胶囊进度(含尚未落库的内存 pending)。
        public GrowthCapsuleSummary LoadGrowthCapsule(int accountId)
        {
            return GrowthCapsuleDataProvider.Calculate(
                AccountExperienceBuffer.Instance
                    .GetEffectiveTotals(accountId, _connectionString)
                    .TotalGrowthCapsuleExp);
        }

        // 荣誉进度(含尚未落库的内存 pending)。
        public HonorLevelSummary LoadHonorSummary(int accountId)
        {
            var characters = accountId > 0
                ? _characterRepository?.ListByAccount(accountId)
                : null;
            var totals = AccountExperienceBuffer.Instance
                .GetEffectiveTotals(accountId, _connectionString);
            return HonorLevelDataProvider.CalculateFromHonorExp(
                totals.TotalHonorExp, characters);
        }

        // 高频杀怪入口: 只累计内存并立即返回含 pending 的有效总值,
        // 落库交给分钟 tick / 出图兜底 / 停机 FlushAll。
        public AccountExperienceProgressSummary AddHonorAndGrowthCapsuleExp(int accountId, uint honorExpGain)
        {
            if (accountId <= 0)
                return BuildSummary(accountId, default);

            var totals = AccountExperienceBuffer.Instance.Add(
                accountId, honorExpGain, _connectionString);
            return BuildSummary(accountId, totals);
        }

        // 低频事务内入口(任务交付/经验道具/结算): 增量直写调用方事务, 有效总值由
        // AccountExperienceBuffer 用"直写后的 DB 值 + 内存 pending"给出。
        // 这里刻意不把 pending 搬进调用方事务 — 调用方随后可能回滚。
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
            // 直写前的胶囊进度: 只用于算"本次实际应用了多少"(胶囊可能已满)。
            var growthCapsuleExpBefore = GrowthCapsuleProgressRepository.LoadTotalExp(
                connection, transaction, accountId);
            var totalHonorExp = HonorLevelProgressRepository.AddHonorExpInTransaction(
                connection, transaction, accountId, honorExpGain);
            var totalGrowthCapsuleExp = GrowthCapsuleProgressRepository.AddExpInTransaction(
                connection, transaction, accountId, growthCapsuleExpGain, out _);
            return AccountExperienceBuffer.Instance.CompleteDirectWrite(
                accountId,
                totalHonorExp,
                totalGrowthCapsuleExp,
                growthCapsuleExpBefore);
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
