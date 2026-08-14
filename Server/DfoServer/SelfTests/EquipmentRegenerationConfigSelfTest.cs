using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DfoServer.SelfTests
{
    internal static class EquipmentRegenerationConfigSelfTest
    {
        public static int Run()
        {
            try
            {
                var config = EquipmentRegenerationConfigProvider.LoadCurrent();
                AssertTargetLevels(config.GetTargetLevels(3, 20), (20, 30000d), (25, 70000d));
                AssertTargetLevels(config.GetTargetLevels(3, 50), (50, 50000d), (55, 50000d));
                AssertTargetLevels(config.GetTargetLevels(2, 50), (50, 40000d), (55, 60000d));
                AssertTargetLevels(config.GetTargetLevels(99, 80), (80, 100000d));

                if (!config.ExceptionWeights.TryGetValue(108000112, out var quarterWeight)
                    || Math.Abs(quarterWeight - 0.25) > 0.000001
                    || !config.ExceptionWeights.TryGetValue(100070023, out var reducedWeight)
                    || Math.Abs(reducedWeight - 0.6) > 0.000001)
                {
                    throw new InvalidOperationException("probability exception weights were not parsed correctly");
                }

                if (!config.IsLegalPart("katana", 3)
                    || config.IsLegalPart("club", 4)
                    || !config.IsLegalPart("support", 13)
                    || !config.IsLegalPart("magic stone", 14))
                {
                    throw new InvalidOperationException("choose-part definitions were not parsed correctly");
                }

                AssertSpecificGroupRules();
                AssertDropEligibilityRules();
                AssertMaterialRules(config);

                var timer = Stopwatch.StartNew();
                EquipmentRegenerationCandidateCatalog.Warmup();
                timer.Stop();
                var statistics = EquipmentRegenerationCandidateCatalog.GetStatistics();
                if (statistics.BucketCount <= 0 || statistics.CandidateCount <= 0)
                    throw new InvalidOperationException("equipment regeneration catalog is empty");

                if (EquipmentRegenerationCandidateCatalog.GetCandidates(3, 50)
                    .Any(item => item.ItemTemplateId == 102020230))
                {
                    throw new InvalidOperationException("generated modified-option equipment entered candidates");
                }

                var magicStonePools = InventoryEquipmentRegenerationService.BuildCandidatePools(
                    3,
                    80,
                    "magic stone",
                    false,
                    14,
                    3,
                    config);
                if (magicStonePools.SelectMany(pool => pool.Candidates)
                    .Any(candidate => candidate.ItemTemplateId == 100352442))
                {
                    throw new InvalidOperationException("non-drop level-85 unique equipment entered candidates");
                }

                var katanaPools = InventoryEquipmentRegenerationService.BuildCandidatePools(
                    3,
                    50,
                    "katana",
                    false,
                    3,
                    3,
                    config);
                if (katanaPools.Count != 2
                    || katanaPools.All(pool => pool.TargetLevel != 50)
                    || katanaPools.All(pool => pool.TargetLevel != 55))
                {
                    throw new InvalidOperationException("directed katana level pools are incorrect");
                }

                Console.WriteLine(
                    $"equipmentRegeneration buckets={statistics.BucketCount} " +
                    $"candidates={statistics.CandidateCount} warmupMs={timer.Elapsed.TotalMilliseconds:F3} " +
                    $"exceptions={config.ExceptionWeights.Count} exceptItems={config.ExceptItemIds.Count}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EquipmentRegenerationConfig] failed: {ex}");
                return 1;
            }
        }

        private static void AssertSpecificGroupRules()
        {
            if (!InventoryEquipmentRegenerationService.MatchesSpecificItemGroup("katana", "katana", 3)
                || InventoryEquipmentRegenerationService.MatchesSpecificItemGroup("club", "katana", 3)
                || !InventoryEquipmentRegenerationService.MatchesSpecificItemGroup("club", "katana", 0)
                || !InventoryEquipmentRegenerationService.MatchesSpecificItemGroup("[ha coat]", "`ha coat`", 10)
                || InventoryEquipmentRegenerationService.MatchesSpecificItemGroup("ha pants", "ha coat", 10))
            {
                throw new InvalidOperationException("specific item-group filtering is incorrect");
            }
        }

        private static void AssertDropEligibilityRules()
        {
            if (InventoryEquipmentRegenerationService.HasCompoundDropEligibility(3, 60, false, 0, false)
                || !InventoryEquipmentRegenerationService.HasCompoundDropEligibility(3, 60, false, 1, false)
                || !InventoryEquipmentRegenerationService.HasCompoundDropEligibility(2, 60, true, 0, false)
                || !InventoryEquipmentRegenerationService.HasCompoundDropEligibility(3, 60, false, 0, true)
                || InventoryEquipmentRegenerationService.HasCompoundDropEligibility(3, 85, false, 0, false)
                || !InventoryEquipmentRegenerationService.HasCompoundDropEligibility(6, 85, false, 0, false))
            {
                throw new InvalidOperationException("compound level/drop filtering is incorrect");
            }

            if (!EquipmentRegenerationCandidateCatalog.IsGeneratedModifiedOption(3, 0, new[] { 1, 0 })
                || EquipmentRegenerationCandidateCatalog.IsGeneratedModifiedOption(2, 0, new[] { 1, 0 })
                || EquipmentRegenerationCandidateCatalog.IsGeneratedModifiedOption(3, 500, new[] { 1, 0 }))
            {
                throw new InvalidOperationException("modified-option equipment classification is incorrect");
            }
        }

        private static void AssertMaterialRules(EquipmentRegenerationConfigProvider.Config config)
        {
            var materials = config.GetMaterials(6, true, 85).ToDictionary(item => item.ItemTemplateId, item => item.Count);
            if (materials.Count != 4
                || !materials.TryGetValue(3167, out var commonSoul) || commonSoul != 1500
                || !materials.TryGetValue(10099775, out var legendarySoul) || legendarySoul != 6
                || !materials.TryGetValue(10100115, out var superiorCrystal) || superiorCrystal != 600
                || !materials.TryGetValue(10100116, out var ordinaryCrystal) || ordinaryCrystal != 480)
            {
                throw new InvalidOperationException("level-85 legendary specific materials are incorrect");
            }
        }

        private static void AssertTargetLevels(
            IReadOnlyList<(int Level, double Weight)> actual,
            params (int Level, double Weight)[] expected)
        {
            if (actual.Count != expected.Length)
                throw new InvalidOperationException("regen level-limit count is incorrect");
            for (var index = 0; index < expected.Length; index++)
            {
                if (actual[index].Level != expected[index].Level
                    || Math.Abs(actual[index].Weight - expected[index].Weight) > 0.000001)
                {
                    throw new InvalidOperationException(
                        $"regen level-limit mismatch at {index}: " +
                        $"actual={actual[index].Level}/{actual[index].Weight} " +
                        $"expected={expected[index].Level}/{expected[index].Weight}");
                }
            }
        }
    }
}
