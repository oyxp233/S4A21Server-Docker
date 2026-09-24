using System;
using DfoServer.Game.Inventory;
using PvfLib;

namespace DfoServer.SelfTests
{
    // expupbycrackofdimension 修复的聚焦自测。
    //
    // 出厂 PVF 中该标签一律不带数值(10100300=0, 10099821/10146833/10146849 无值),
    // 客户端说明文本「经验值增加 %s」按角色等级计算:
    //   floor(questParameter.etc [exp reward table][角色等级] * 56 / 100)
    // 服务端原先要求 stk 给出正值, 于是真实道具一律被判为不支持并回 0x01 未知错误。
    // 现改为: PVF 显式给正值时以 PVF 为准, 否则按等级推导。
    public static class ExperienceItemDefinitionSelfTest
    {
        // 客户端实测值: 等级 -> 说明显示经验。覆盖道具可用区间 55~84 的两端与中段。
        private static readonly byte[] MeasuredLevels =
        {
            55, 60, 61, 62, 65, 70, 75, 80, 84,
        };

        private static readonly uint[] MeasuredExp =
        {
            124292, 156894, 163992, 171289, 194390, 237053, 285150, 338942, 386246,
        };

        public static int Run()
        {
            Console.WriteLine("=== EXPERIENCE_ITEM_DEFINITION selftest ===");
            var failures = 0;

            VerifyCrackOfDimensionUsesStkValueWhenPresent(ref failures);
            VerifyCrackOfDimensionZeroValueFallsBack(ref failures);
            VerifyCrackOfDimensionMissingValueFallsBack(ref failures);
            VerifyOrdinaryExpUpStillRejectsMissingValue(ref failures);
            VerifyOrdinaryExpUpStillSupported(ref failures);
            VerifyCrackOfDimensionRateAgainstPvf(ref failures);

            Console.WriteLine(failures == 0
                ? "EXPERIENCE_ITEM_DEFINITION selftest passed"
                : $"EXPERIENCE_ITEM_DEFINITION selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        // PVF 显式给出正值时仍以 PVF 为准, 保留数据覆盖服务端规则的余地。
        private static void VerifyCrackOfDimensionUsesStkValueWhenPresent(ref int failures)
        {
            var stackable = StackableItemFile.Parse(@"
[name]
`异次元裂缝成长胶囊`
[explain]
`经验值增加 %s`
[stackable type]
`[etc]` 0
[usable job]
`[all]`
[/usable job]
[minimum level]
55
[maximum level]
84
[increase status type]
`[expUpByCrackOfDimension]` 500000
[/increase status type]
");

            var definition = ExperienceItemDataProvider.Resolve(10100300, stackable);
            Check(
                "crack-of-dimension with a positive stk value resolves as supported",
                definition.IsSupported,
                ref failures);
            Check(
                "positive stk value keeps the fixed-experience grant kind",
                definition.GrantKind == ExperienceItemGrantKind.Fixed,
                ref failures);
            Check(
                "positive stk value is used verbatim",
                definition.Value == 500000 && definition.CalculateGain(70) == 500000,
                ref failures);
        }

        // 出货数据就是 0, 现在必须回退到等级规则而不是拒绝。
        private static void VerifyCrackOfDimensionZeroValueFallsBack(ref int failures)
        {
            var stackable = StackableItemFile.Parse(@"
[name]
`异次元裂缝成长胶囊`
[explain]
`经验值增加 %s
    只能用于Lv55~84。`
[stackable type]
`[etc]` 0
[usable job]
`[all]`
[/usable job]
[minimum level]
55
[maximum level]
84
[increase status type]
`[expUpByCrackOfDimension]` 0
[/increase status type]
");

            var definition = ExperienceItemDataProvider.Resolve(10100300, stackable);
            Check(
                "zero stk value is supported instead of rejected",
                definition.IsSupported,
                ref failures);
            Check(
                "zero stk value selects the crack-of-dimension grant kind",
                definition.GrantKind == ExperienceItemGrantKind.CrackOfDimension,
                ref failures);
        }

        // 10099821/10146833/10146849 是完全不带数值的形式。
        private static void VerifyCrackOfDimensionMissingValueFallsBack(ref int failures)
        {
            var stackable = StackableItemFile.Parse(@"
[name]
`异次元裂缝成长胶囊`
[stackable type]
`[etc]` 0
[usable job]
`[all]`
[/usable job]
[minimum level]
1
[maximum level]
84
[increase status type]
`[expUpByCrackOfDimension]`
[/increase status type]
");

            var definition = ExperienceItemDataProvider.Resolve(10099821, stackable);
            Check(
                "missing stk value is supported instead of rejected",
                definition.IsSupported,
                ref failures);
            Check(
                "missing stk value selects the crack-of-dimension grant kind",
                definition.GrantKind == ExperienceItemGrantKind.CrackOfDimension,
                ref failures);
        }

        // 普通固定经验没有等级规则可回退, 缺值仍必须是数据错误。
        private static void VerifyOrdinaryExpUpStillRejectsMissingValue(ref int failures)
        {
            var stackable = StackableItemFile.Parse(@"
[name]
`未填数值的经验胶囊`
[stackable type]
`[etc]` 0
[usable job]
`[all]`
[/usable job]
[increase status type]
`[expUp]`
[/increase status type]
");

            var definition = ExperienceItemDataProvider.Resolve(10146833, stackable);
            Check(
                "ordinary expUp with no value stays rejected",
                !definition.IsSupported,
                ref failures);
            Check(
                "ordinary expUp reject reason is the fixed-experience value guard",
                definition.UnsupportedReason == "invalid fixed experience value",
                ref failures);
        }

        private static void VerifyOrdinaryExpUpStillSupported(ref int failures)
        {
            var stackable = StackableItemFile.Parse(@"
[name]
`普通经验胶囊`
[stackable type]
`[etc]` 0
[usable job]
`[all]`
[/usable job]
[minimum level]
1
[maximum level]
70
[increase status type]
`[expUp]` 15000
[/increase status type]
");

            var definition = ExperienceItemDataProvider.Resolve(10089614, stackable);
            Check(
                "ordinary expUp item still resolves as fixed experience",
                definition.IsSupported
                && definition.GrantKind == ExperienceItemGrantKind.Fixed
                && definition.Value == 15000,
                ref failures);
        }

        // 需要真实 PVF 的 [exp reward table], 与其它自测一样按环境变量跳过。
        private static void VerifyCrackOfDimensionRateAgainstPvf(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath))
            {
                Console.WriteLine(
                    "[SKIP] crack-of-dimension level rate: PVF_ARCHIVE_PATH is not set");
                return;
            }

            var stackable = StackableItemFile.Parse(@"
[name]
`异次元裂缝成长胶囊`
[stackable type]
`[etc]` 0
[usable job]
`[all]`
[/usable job]
[minimum level]
55
[maximum level]
84
[increase status type]
`[expUpByCrackOfDimension]` 0
[/increase status type]
");

            var definition = ExperienceItemDataProvider.Resolve(10100300, stackable);
            if (!definition.IsSupported)
            {
                Check(
                    "crack-of-dimension definition resolves against the real PVF",
                    false,
                    ref failures);
                return;
            }

            for (var i = 0; i < MeasuredLevels.Length; i++)
            {
                var level = MeasuredLevels[i];
                var expected = MeasuredExp[i];
                Check(
                    $"crack-of-dimension level {level} grants {expected}",
                    definition.CalculateGain(level) == expected,
                    ref failures);
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }

            failures++;
            Console.WriteLine($"[FAIL] {name}");
        }
    }
}
