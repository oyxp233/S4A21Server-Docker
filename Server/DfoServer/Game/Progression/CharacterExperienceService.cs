using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Progression
{
    // 角色经验的落库策略。副本杀怪刻意只在升级/满级纠偏时落库
    // (高频入口, 平时经验留在会话内存, 离开一局时由 PersistSessionExp 兜底),
    // 其余入口有变化即落库。
    internal enum ExperiencePersistMode
    {
        OnLevelUpOnly,
        OnAnyChange,
    }

    internal sealed class ExperienceGrantResult
    {
        internal uint RawGain { get; set; }
        internal uint HonorExpGain { get; set; }
        internal uint NormalExpGain { get; set; }
        internal byte PreviousLevel { get; set; }
        internal uint PreviousExp { get; set; }
        internal byte NewLevel { get; set; }
        internal uint NewExp { get; set; }
        internal bool NormalizedMaxLevelExp { get; set; }
        // 仅当按策略应当落库且 UPDATE 实际生效时为 true;
        // 调用方需要区分"没到落库条件"和"落库失败"时配合 ShouldPersist 判断。
        internal bool Persisted { get; set; }
        internal HonorLevelSummary Honor { get; set; }
        internal GrowthCapsuleSummary GrowthCapsule { get; set; }
        internal uint GrowthCapsuleExpGain { get; set; }
        internal ulong TotalHonorExp { get; set; }
        internal uint TotalGrowthCapsuleExp { get; set; }

        internal bool LeveledUp => NewLevel > PreviousLevel;
    }

    // 角色获得经验的统一入口: 荣誉拆分 -> 满级经验纠偏 -> 升级判定
    // -> 账号荣誉+成长胶囊联动 -> 按策略落库。
    // 三种调用形态共用同一份数学核(ApplyGainCore):
    //   Grant              会话入口(副本杀怪/组队/结算), 更新会话内存, 自管连接
    //   GrantInTransaction 事务内入口(任务交付/经验道具), 写入随调用方事务提交/回滚
    //   Plan               纯计算不落库(经验道具使用前的预演校验)
    internal sealed class CharacterExperienceService
    {
        private readonly AccountExperienceProgressService _accountExperience;
        private readonly IGameDatabase _database;

        internal CharacterExperienceService(AccountExperienceProgressService accountExperience)
            : this(accountExperience, GameDatabase.CreateDefault())
        {
        }

        internal CharacterExperienceService(
            AccountExperienceProgressService accountExperience,
            IGameDatabase database)
        {
            _accountExperience = accountExperience ?? throw new ArgumentNullException(nameof(accountExperience));
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal ExperienceGrantResult Grant(
            PlayerContext player,
            int accountId,
            uint rawGain,
            ExperiencePersistMode persistMode,
            string logTag)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));

            var result = NewResult(player.Level, player.Exp, rawGain);
            var level = player.Level;
            var exp = player.Exp;
            ApplyGainCore(result, ref level, ref exp, rawGain, normalizeMaxLevelExp: true);
            player.Level = level;
            player.Exp = exp;

            if (result.HonorExpGain > 0 && accountId > 0)
            {
                var progress = _accountExperience.AddHonorAndGrowthCapsuleExp(accountId, result.HonorExpGain);
                result.Honor = progress.Honor;
                result.GrowthCapsule = progress.GrowthCapsule;
                result.GrowthCapsuleExpGain = progress.GrowthCapsuleExpGain;
                result.TotalHonorExp = progress.Honor?.TotalHonorExp ?? 0;
                result.TotalGrowthCapsuleExp = progress.GrowthCapsule?.TotalExp ?? 0;
                LogAccountGain(logTag, accountId, player.CharacterId, result);
            }

            if (ShouldPersist(persistMode, result))
                result.Persisted = CharacterProgressService.PersistLevelAndExp(
                    player.CharacterId,
                    player.Level,
                    player.Exp,
                    _database);

            return result;
        }

        // 事务内入口: 写入全部落在调用方 (connection, transaction) 上, 随其提交或回滚。
        // 不碰会话内存 -- 新等级/经验由调用方在事务提交后回填会话。
        // 固定"有变化即落库、不做满级纠偏"(两个现有调用方的语义); 也不在事务内
        // 记日志 -- 写锁持有期间不做文件 I/O, 荣誉/胶囊增量由调用方提交后自行记。
        internal static ExperienceGrantResult GrantInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            byte currentLevel,
            uint currentExp,
            uint rawGain,
            bool normalizeMaxLevelExp = false)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var result = NewResult(currentLevel, currentExp, rawGain);
            var level = currentLevel;
            var exp = currentExp;
            ApplyGainCore(result, ref level, ref exp, rawGain, normalizeMaxLevelExp);

            if (result.HonorExpGain > 0)
            {
                var totals = AccountExperienceProgressService.AddInTransaction(
                    connection, transaction, accountId, result.HonorExpGain);
                result.GrowthCapsuleExpGain = totals.GrowthCapsuleExpGain;
                result.TotalHonorExp = totals.TotalHonorExp;
                result.TotalGrowthCapsuleExp = totals.TotalGrowthCapsuleExp;
            }

            if (ShouldPersist(ExperiencePersistMode.OnAnyChange, result))
                result.Persisted = CharacterProgressService.PersistLevelAndExp(
                    connection, transaction, characterId, level, exp);

            return result;
        }

        internal void PopulateAccountProgressSummary(
            ExperienceGrantResult result,
            int accountId)
        {
            if (result == null || result.HonorExpGain == 0 || accountId <= 0)
                return;

            var totals = new AccountExperienceProgressTotals(
                result.TotalHonorExp,
                result.TotalGrowthCapsuleExp,
                result.GrowthCapsuleExpGain);
            var summary = _accountExperience.BuildSummary(accountId, totals);
            result.Honor = summary.Honor;
            result.GrowthCapsule = summary.GrowthCapsule;
        }

        // 绝对设值: 教程一键升到目标等级。不是"加经验"—无荣誉拆分,
        // 经验直接钉在目标等级的门槛值。
        internal ExperienceGrantResult GrantToLevel(PlayerContext player, byte targetLevel, string logTag)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));

            var result = NewResult(player.Level, player.Exp, 0);
            uint targetExp = 0;
            for (byte lv = 1; lv < targetLevel; lv++)
            {
                var threshold = (uint)Math.Max(0, ExpTableProvider.GetLevelThreshold(lv));
                if (threshold > targetExp) targetExp = threshold;
            }

            player.Exp = targetExp;
            player.Level = targetLevel;
            result.NewLevel = targetLevel;
            result.NewExp = targetExp;
            result.Persisted = CharacterProgressService.PersistLevelAndExp(
                player.CharacterId,
                targetLevel,
                targetExp,
                _database);
            FileLogger.Log($"[Progression] GRANT_TO_LEVEL {logTag ?? "set"}: cid={player.CharacterId} {result.PreviousLevel}->{targetLevel} exp={targetExp}");
            return result;
        }

        // 离开一局时把会话内存的等级/经验落库 -- OnLevelUpOnly 策略的配对兜底:
        // 副本杀怪经验平时只写内存, 放弃副本/断线/换角色若不在此落库,
        // 这段经验要么随会话消失, 要么之后被读库的结算逻辑用旧值覆盖掉。
        internal static bool PersistSessionExp(PlayerContext player, string source)
        {
            return PersistSessionExp(
                player,
                source,
                GameDatabase.CreateDefault().ConnectionString);
        }

        internal static bool PersistSessionExp(
            PlayerContext player,
            string source,
            string connectionString)
        {
            if (player == null || player.CharacterId <= 0)
                return true;
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException(
                    "connectionString is empty",
                    nameof(connectionString));

            try
            {
                CharacterProgressService.PersistLevelAndExp(
                    connectionString,
                    player.CharacterId,
                    player.Level,
                    player.Exp);
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Progression] ERROR: exp persist failed on {source}: cid={player.CharacterId} lv={player.Level} exp={player.Exp}: {ex.Message}");
                return false;
            }
        }

        // 纯计算, 不写任何东西。经验道具使用前的预演(校验通过与否决定要不要开扣除事务)。
        internal static ExperienceGrantResult Plan(
            byte currentLevel,
            uint currentExp,
            uint rawGain,
            bool normalizeMaxLevelExp = false)
        {
            var result = NewResult(currentLevel, currentExp, rawGain);
            var level = currentLevel;
            var exp = currentExp;
            ApplyGainCore(result, ref level, ref exp, rawGain, normalizeMaxLevelExp);
            return result;
        }

        private static bool ShouldPersist(ExperiencePersistMode mode, ExperienceGrantResult result)
            => mode == ExperiencePersistMode.OnAnyChange
                ? result.LeveledUp || result.NormalExpGain > 0 || result.NormalizedMaxLevelExp
                : result.LeveledUp || result.NormalizedMaxLevelExp;

        // 数学核: 荣誉拆分 -> 满级纠偏(可选) -> 普通经验累加 -> 升级判定。
        private static void ApplyGainCore(
            ExperienceGrantResult result,
            ref byte level,
            ref uint exp,
            uint rawGain,
            bool normalizeMaxLevelExp)
        {
            result.HonorExpGain = HonorLevelDataProvider.CalculateHonorExpGain(level, exp, rawGain);
            result.NormalExpGain = rawGain > result.HonorExpGain
                ? rawGain - result.HonorExpGain
                : 0;

            if (level >= ExpTableProvider.MaxLevel)
            {
                if (normalizeMaxLevelExp)
                {
                    // 满级角色经验钉在满级门槛值, 历史超量经验在这里纠偏。
                    var maxLevelEntryExp = (uint)Math.Max(0, ExpTableProvider.GetLevelThreshold(ExpTableProvider.MaxLevel - 1));
                    if (exp != maxLevelEntryExp)
                    {
                        exp = maxLevelEntryExp;
                        result.NormalizedMaxLevelExp = true;
                    }
                }
            }
            else if (result.NormalExpGain > 0)
            {
                exp = AddSaturating(exp, result.NormalExpGain);
            }

            level = ExpTableProvider.ApplyLevelUps(level, exp);
            result.NewLevel = level;
            result.NewExp = exp;
        }

        private static ExperienceGrantResult NewResult(byte level, uint exp, uint rawGain)
            => new ExperienceGrantResult
            {
                RawGain = rawGain,
                PreviousLevel = level,
                PreviousExp = exp,
                NewLevel = level,
                NewExp = exp,
            };

        private static void LogAccountGain(string logTag, int accountId, int characterId, ExperienceGrantResult result)
            => FileLogger.Log($"[Progression] ACCOUNT_EXP_GAIN {logTag ?? "exp"}: account={accountId} cid={characterId} honor={result.HonorExpGain} capsule={result.GrowthCapsuleExpGain} capsuleTotal={result.TotalGrowthCapsuleExp}");

        internal static uint AddSaturating(uint current, uint add)
        {
            var value = (ulong)current + add;
            return value > uint.MaxValue ? uint.MaxValue : (uint)value;
        }
    }
}
