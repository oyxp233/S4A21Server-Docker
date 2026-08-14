using DfoServer.GameWorld;
using System;
using System.Globalization;

namespace DfoServer.Game.Dungeon
{
    public static class MonsterRewardTable
    {
        private static readonly int[] MobRewardByLevel = new int[]
        {
            30,   40,   50,   60,   70,   80,   90,  100,  110,  120,
           130,  140,  150,  160,  170,  185,  201,  218,  235,  253,
           271,  290,  310,  330,  351,  372,  394,  417,  440,  464,
           488,  513,  539,  565,  592,  619,  647,  676,  705,  735,
           765,  796,  828,  860,  893,  926,  960,  995, 1030, 1066,
          1102, 1139, 1177, 1215, 1254, 1293, 1333, 1374, 1415, 1457,
          1499, 1542, 1586, 1630, 1675, 1720, 1766, 1813, 1860, 1908,
          1956, 2005, 2055, 2105, 2156, 2207, 2259, 2312, 2365, 2419,
          2473, 2528, 2584, 2640, 2697, 2754, 2812, 2871, 2930, 2990,
          3050, 3111, 3173, 3235, 3298, 3361, 3425, 3490, 3555, 3575,
        };

        private static readonly object _lock = new object();
        private static float[] _clearRankExpBonusRate;
        private static int[] _rankGrades;

        public static int GetMobReward(int level)
        {
            if (level < 1 || level > MobRewardByLevel.Length)
                return 0;
            return MobRewardByLevel[level - 1];
        }

        public static int GetClearRankBonusIndex(int clearScore)
        {
            var grades = GetRankGrades();
            if (grades.Length > 0 && clearScore >= grades[0]) return 4;
            if (grades.Length > 1 && clearScore >= grades[1]) return 3;
            if (grades.Length > 2 && clearScore >= grades[2]) return 2;
            if (grades.Length > 3 && clearScore >= grades[3]) return 1;
            if (grades.Length > 4 && clearScore >= grades[4]) return 0;
            return -1;
        }

        public static int GetClearRankGrade(int clearScore)
        {
            var grades = GetRankGrades();
            for (int i = 0; i < grades.Length; i++)
            {
                if (clearScore >= grades[i])
                    return grades[i];
            }
            return 0;
        }

        public static float GetClearRankExpBonusRate(int rankBonusIndex)
        {
            EnsureClearRankExpBonusRatesLoaded();
            if (rankBonusIndex < 0 || rankBonusIndex >= _clearRankExpBonusRate.Length)
                return 0.0f;
            return _clearRankExpBonusRate[rankBonusIndex];
        }

        private static void EnsureClearRankExpBonusRatesLoaded()
        {
            if (_clearRankExpBonusRate != null) return;
            lock (_lock)
            {
                if (_clearRankExpBonusRate != null) return;
                _clearRankExpBonusRate = ParseFloatRates("[clear rank exp bonusrate]", new float[] { 0f, 0.01f, 0.02f, 0.03f, 0.05f });
            }
        }

        private static int[] GetRankGrades()
        {
            if (_rankGrades != null) return _rankGrades;
            lock (_lock)
            {
                if (_rankGrades != null) return _rankGrades;
                _rankGrades = ParseIntValues("Etc/RankSystemInfo.etc", "[rank grade]", new int[] { 99, 90, 80, 60, 50, 30, 20, 10 });
                return _rankGrades;
            }
        }

        private static float[] ParseFloatRates(string tag, float[] fallback)
        {
            try
            {
                var text = PvfArchiveAccessor.ReadText("Etc/ServerParameter.etc");
                if (string.IsNullOrEmpty(text)) return fallback;

                var idx = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return fallback;
                idx += tag.Length;

                var end = text.IndexOf('[', idx);
                if (end < 0) end = text.Length;
                var content = text.Substring(idx, end - idx).Trim();
                var tokens = content.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var rates = new float[tokens.Length];
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        rates[i] = v;
                    else
                        rates[i] = 1f;
                }
                FileLogger.Log($"[MonsterRewardTable] Loaded {tag}: {string.Join(", ", rates)}");
                return rates;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[MonsterRewardTable] ERROR loading {tag}: {ex.Message}");
                return fallback;
            }
        }

        private static int[] ParseIntValues(string pvfPath, string tag, int[] fallback)
        {
            try
            {
                var text = PvfArchiveAccessor.ReadText(pvfPath);
                if (string.IsNullOrEmpty(text)) return fallback;

                var idx = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return fallback;
                idx += tag.Length;

                var end = text.IndexOf('[', idx);
                if (end < 0) end = text.Length;
                var content = text.Substring(idx, end - idx).Trim();
                var tokens = content.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var values = new int[tokens.Length];
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        values[i] = v;
                    else
                        values[i] = 0;
                }
                FileLogger.Log($"[MonsterRewardTable] Loaded {tag}: {string.Join(", ", values)}");
                return values;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[MonsterRewardTable] ERROR loading {tag}: {ex.Message}");
                return fallback;
            }
        }
    }
}
