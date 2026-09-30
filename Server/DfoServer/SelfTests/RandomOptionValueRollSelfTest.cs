using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;

namespace DfoServer.SelfTests
{
    public static class RandomOptionValueRollSelfTest
    {
        private const int FireAttackOptionId = 159;
        private const int CastSpeedOptionId = 131;
        private const int HpMaxOptionId = 104;
        private const int SampleRollCount = 60;
        private const int EndToEndRollCount = 40;

        // [different weight] 稀有度 2 各槽位品级百分比区间(线上 PVF 实测):
        // 槽1 [0,25] / 槽2 [10,30] / 槽3 [30,55]; 稀有度无表时回退 [0,56]。
        private static readonly (int pctMin, int pctMax)[] Rarity2SlotRanges =
        {
            (0, 25),
            (10, 30),
            (30, 55),
        };

        public static int Run()
        {
            Console.WriteLine("=== RANDOM_OPTION_VALUE_ROLL selftest ===");
            var failures = 0;

            if (!HasRealPvf())
            {
                Console.WriteLine(
                    "[SKIP] real PVF random option value roll checks: PVF_ARCHIVE_PATH is not set");
            }
            else
            {
                VerifyFireAttackRollsGradePercent(ref failures);
                VerifyCastSpeedPrefersDungeonRowOverPvpRow(ref failures);
                VerifyOversizedRangeRollsAfterByteClamp(ref failures);
                VerifyTryRollOptionsEndToEnd(ref failures);
            }

            Console.WriteLine(failures == 0
                ? "RANDOM_OPTION_VALUE_ROLL selftest passed"
                : $"RANDOM_OPTION_VALUE_ROLL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyFireAttackRollsGradePercent(ref int failures)
        {
            // randomoptions_159_fireattack.etc 70级 PvE 行: `2 11`
            // Value2 语义为品级百分比 p: 稀有度2槽1 按 [different weight] p∈[0,25];
            // Value1 = 区间 [2,11] 按 p 线性插值(整数除法, 与实现精确一致)。
            var entries = SampleRolls(FireAttackOptionId, 70, rarity: 2, slotIndex: 1, SampleRollCount);
            Check(
                "fireattack @70(稀有2槽1): Value2=p∈[0,25] 且 Value1=区间[2,11]按 p 插值, p 不全相同(字母会变)",
                entries.Count == SampleRollCount
                    && entries.All(e => e.Type == FireAttackOptionId
                        && e.Value2 >= 0
                        && e.Value2 <= 25
                        && MatchesInterpolatedValue(e, 2, 11))
                    && entries.Select(e => e.Value2).Distinct().Count() > 1,
                ref failures);

            // 稀有度无 [different weight] 条目(如 5): p 回退均匀 [0,56]。
            var fallback = SampleRolls(FireAttackOptionId, 70, rarity: 5, slotIndex: 1, 20);
            Check(
                "fireattack @70 稀有度无表(5): Value2=p 回退均匀 [0,56] 且 Value1 按 p 插值",
                fallback.Count == 20
                    && fallback.All(e => e.Value2 >= 0
                        && e.Value2 <= 56
                        && MatchesInterpolatedValue(e, 2, 11)),
                ref failures);
        }

        private static void VerifyCastSpeedPrefersDungeonRowOverPvpRow(ref int failures)
        {
            // randomoptions_131_castspeed.etc 带 [pvp] 段; 70级 PvE 行 `13 77`, PvP 行 `7 39`。
            // 取 PvE 区间 [13,77] 按 p∈[0,25] 插值; 若误取 PvP 行, 插值公式必然不符(两式差恒 ≥6)。
            var entries = SampleRolls(CastSpeedOptionId, 70, rarity: 2, slotIndex: 1, SampleRollCount);
            Check(
                "castspeed @70(稀有2槽1): 取 PvE 段行 [13,77](而非 PvP [7,39]), Value2=p∈[0,25], Value1 按 p 插值",
                entries.Count == SampleRollCount
                    && entries.All(e => e.Type == CastSpeedOptionId
                        && e.Value2 >= 0
                        && e.Value2 <= 25
                        && e.Value1 >= 13
                        && e.Value1 <= 77
                        && MatchesInterpolatedValue(e, 13, 77))
                    && entries.Select(e => e.Value2).Distinct().Count() > 1,
                ref failures);
        }

        private static void VerifyOversizedRangeRollsAfterByteClamp(ref int failures)
        {
            // randomoptions_104_hpmax.etc 85级行 `109 480` 超字节域: 区间截断为 [109,255],
            // p∈[0,25] 插值, Value2=p, 不越界不抛异常。
            var entries = SampleRolls(HpMaxOptionId, 85, rarity: 2, slotIndex: 1, SampleRollCount);
            Check(
                "hpmax @85(稀有2槽1) 超字节域行截断后 [109,255]: Value2=p∈[0,25] 且 Value1 按 p 插值",
                entries.Count == SampleRollCount
                    && entries.All(e => e.Type == HpMaxOptionId
                        && e.Value2 >= 0
                        && e.Value2 <= 25
                        && e.Value1 >= 109
                        && e.Value1 <= 255
                        && MatchesInterpolatedValue(e, 109, 255))
                    && entries.Select(e => e.Value2).Distinct().Count() > 1,
                ref failures);
        }

        private static void VerifyTryRollOptionsEndToEnd(ref int failures)
        {
            var metadata = new ItemMetadata
            {
                ItemKind = "equipment",
                EquipmentType = "coat",
                Rarity = 2,
                MinimumLevel = 70,
                PvfFilePath = "equipment/cloth/sealroll_selftest.coat",
            };

            var allSucceeded = true;
            var allConsistent = true;
            var distinctGradePercents = new HashSet<int>();
            var officialRanges = new Dictionary<int, (int min, int max)>();

            for (var i = 0; i < EndToEndRollCount; i++)
            {
                if (!RandomOptionResolver.TryRollOptions(metadata, out var entries)
                    || entries == null
                    || entries.Count == 0)
                {
                    allSucceeded = false;
                    continue;
                }

                for (var slot = 1; slot <= entries.Count && slot <= 3; slot++)
                {
                    var entry = entries[slot - 1];
                    if (!officialRanges.TryGetValue(entry.Type, out var range))
                    {
                        if (!TryResolveOfficialDungeonRange(entry.Type, metadata.MinimumLevel, out range))
                        {
                            allConsistent = false;
                            continue;
                        }

                        officialRanges[entry.Type] = range;
                    }

                    var maxEff = Math.Min(range.max, 255);
                    var minEff = Math.Min(range.min, maxEff);
                    var slotRange = Rarity2SlotRanges[Math.Min(slot - 1, Rarity2SlotRanges.Length - 1)];
                    if (entry.Value2 < slotRange.pctMin
                        || entry.Value2 > slotRange.pctMax
                        || !MatchesInterpolatedValue(entry, minEff, maxEff))
                    {
                        allConsistent = false;
                    }

                    distinctGradePercents.Add(entry.Value2);
                }
            }

            Check(
                "TryRollOptions 端到端多次 roll 全部成功: Value2=p 落在 [different weight] 槽位区间, Value1 为官方区间按 p 插值(独立重解析 PVF 校验)",
                allSucceeded && allConsistent && officialRanges.Count > 0,
                ref failures);

            Check(
                "TryRollOptions 多次 roll 品级百分比 p 出现多种取值(品级字母会变化)",
                distinctGradePercents.Count > 1,
                ref failures);
        }

        private static List<RandomOptionEntry> SampleRolls(
            int optionId,
            int itemLevel,
            int rarity,
            int slotIndex,
            int count)
        {
            var entries = new List<RandomOptionEntry>(count);
            for (var i = 0; i < count; i++)
                entries.Add(RandomOptionResolver.RollOptionValue(optionId, itemLevel, rarity, slotIndex));
            return entries;
        }

        // Value1 必须是官方(截断后)区间按 Value2(品级百分比 p) 线性插值的结果,
        // 与实现同用整数除法, 期望精确相等。
        private static bool MatchesInterpolatedValue(RandomOptionEntry entry, int minEff, int maxEff)
        {
            return entry.Value1 == minEff + (maxEff - minEff) * entry.Value2 / 100;
        }

        // 测试侧独立从 PVF 原文重解析 (optionId, itemLevel) 的官方 PvE 区间, 作为被测实现的对照。
        private static bool TryResolveOfficialDungeonRange(
            int optionId,
            int itemLevel,
            out (int min, int max) range)
        {
            range = (1, 1);

            var listText = PvfArchiveAccessor.ReadText("etc/randomoption/randomoption.lst");
            var pathMatch = Regex.Match(
                listText,
                @"(?:^|\s)" + optionId + @"\s+`([^`]+)`");
            if (!pathMatch.Success)
                return false;

            var relativePath = pathMatch.Groups[1].Value.Replace('\\', '/');
            var text = PvfArchiveAccessor.ReadText("etc/randomoption/" + relativePath);

            var source = text ?? string.Empty;
            var dungeonMatch = Regex.Match(
                source,
                @"\[dungeon\](.*?)\[/dungeon\]",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (dungeonMatch.Success)
                source = dungeonMatch.Groups[1].Value;

            var bestLevel = -1;
            var found = false;
            foreach (Match match in Regex.Matches(
                source,
                @"\[level\]\s*(.*?)\[/level\]",
                RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var ints = Regex.Matches(match.Groups[1].Value, @"-?\d+")
                    .Cast<Match>()
                    .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
                    .ToList();
                if (ints.Count < 3)
                    continue;

                var level = ints[0];
                if (level > itemLevel || level < bestLevel)
                    continue;

                bestLevel = level;
                range = (ints[ints.Count - 2], ints[ints.Count - 1]);
                found = true;
            }

            return found;
        }

        private static bool HasRealPvf()
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            return !string.IsNullOrWhiteSpace(pvfPath) && File.Exists(pvfPath);
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
