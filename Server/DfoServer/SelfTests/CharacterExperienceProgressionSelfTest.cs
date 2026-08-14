using System;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Progression;

namespace DfoServer.SelfTests
{
    public static class CharacterExperienceProgressionSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== CHARACTER_EXPERIENCE_PROGRESSION selftest ===");
            var failures = 0;
            var maxLevelEntryExp = (uint)Math.Max(
                0,
                ExpTableProvider.GetLevelThreshold(ExpTableProvider.MaxLevel - 1));

            var beforeMax = CharacterExperienceService.Plan(
                (byte)(ExpTableProvider.MaxLevel - 1),
                maxLevelEntryExp - 10,
                9,
                normalizeMaxLevelExp: true);
            Check("ordinary gain below max-level threshold stays normal",
                beforeMax.RawGain == 9
                && beforeMax.NormalExpGain == 9
                && beforeMax.HonorExpGain == 0
                && beforeMax.NewLevel == ExpTableProvider.MaxLevel - 1
                && beforeMax.NewExp == maxLevelEntryExp - 1,
                ref failures);

            var crossing = CharacterExperienceService.Plan(
                (byte)(ExpTableProvider.MaxLevel - 1),
                maxLevelEntryExp - 10,
                25,
                normalizeMaxLevelExp: true);
            Check("85-to-86 gain splits only overflow into honor exp",
                crossing.RawGain == 25
                && crossing.NormalExpGain == 10
                && crossing.HonorExpGain == 15
                && crossing.NewLevel == ExpTableProvider.MaxLevel
                && crossing.NewExp == maxLevelEntryExp
                && crossing.LeveledUp,
                ref failures);

            var alreadyMax = CharacterExperienceService.Plan(
                ExpTableProvider.MaxLevel,
                123,
                42,
                normalizeMaxLevelExp: true);
            Check("max-level gain is all honor and normalizes character exp",
                alreadyMax.RawGain == 42
                && alreadyMax.NormalExpGain == 0
                && alreadyMax.HonorExpGain == 42
                && alreadyMax.NewLevel == ExpTableProvider.MaxLevel
                && alreadyMax.NewExp == maxLevelEntryExp
                && alreadyMax.NormalizedMaxLevelExp,
                ref failures);

            var zero = CharacterExperienceService.Plan(
                ExpTableProvider.MaxLevel,
                123,
                0);
            Check("zero gain leaves even non-normalized state unchanged",
                zero.RawGain == 0
                && zero.NormalExpGain == 0
                && zero.HonorExpGain == 0
                && zero.NewLevel == ExpTableProvider.MaxLevel
                && zero.NewExp == 123
                && !zero.NormalizedMaxLevelExp,
                ref failures);

            var saturatedLegacyInput = CharacterExperienceService.Plan(
                (byte)(ExpTableProvider.MaxLevel - 1),
                uint.MaxValue - 10,
                42,
                normalizeMaxLevelExp: true);
            Check("projection preserves legacy clear-exp behavior for saturated abnormal input",
                saturatedLegacyInput.HonorExpGain == 10
                && saturatedLegacyInput.NormalExpGain == 32
                && saturatedLegacyInput.NewLevel == ExpTableProvider.MaxLevel
                && saturatedLegacyInput.NewExp == uint.MaxValue,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
