using System;

namespace DfoServer.Game.Premium
{
    // premiumlist_new.etc 中契约的效果字段(解析在 PremiumCatalog, 当前只消费这两项)。
    public sealed class PremiumEffects
    {
        public int OverSkillLevel;
        public int BonusExpPercent;

        // 经验加成 = baseExp * BonusExpPercent / 100, 向下取整; 无加成返回 0。
        public uint ComputeBonusExp(uint baseExp)
        {
            if (baseExp == 0 || BonusExpPercent <= 0)
                return 0;
            var value = baseExp * BonusExpPercent / 100f;
            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }
    }

    public static class PremiumEffectProvider
    {
        // 扫描该账号全部未过期契约, 各效果取最大值合并(当前 PVF 中 over skill 仅
        // 达人之契约 type 27、bonus exp 仅成长之契约 type 84, 取最大与相加等价)。
        // PVF 读取/解析失败由 PremiumCatalog.Load 直接抛出, 不做静默兜底。
        public static PremiumEffects GetCombinedEffects(string connStr, int accountId)
        {
            var combined = new PremiumEffects();
            if (accountId <= 0)
                return combined;

            var catalog = PremiumCatalog.Load();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT premium_type FROM account_premiums WHERE account_id = @aid AND end_time > @now";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@now", now);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var effects = catalog.GetEffects(reader.GetInt32(0));
                            if (effects == null)
                                continue;

                            if (effects.OverSkillLevel > combined.OverSkillLevel)
                                combined.OverSkillLevel = effects.OverSkillLevel;
                            if (effects.BonusExpPercent > combined.BonusExpPercent)
                                combined.BonusExpPercent = effects.BonusExpPercent;
                        }
                    }
                }
            }

            return combined;
        }
    }
}
