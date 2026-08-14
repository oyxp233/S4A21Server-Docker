using DfoServer.GameWorld;
using System;
using System.Linq;

namespace DfoServer.Game.ItemUpgrade
{
    internal static class SeparateUpgradeTableProvider
    {
        private static readonly Lazy<SeparateUpgradeTable> Table = new Lazy<SeparateUpgradeTable>(() =>
        {
            var parsed = SeparateUpgradeTable.Parse(PvfArchiveAccessor.ReadText("etc/upgrade_separate.etc"));
            if (!parsed.IsStructurallyValid
                || parsed.MaxLevel <= 0 || parsed.MaxLevel > byte.MaxValue
                || parsed.Levels.Count < parsed.MaxLevel
                || parsed.Levels.Take(parsed.MaxLevel).Any(level => level == null
                    || level.SuccessWeight < 0 || level.SuccessWeight > 10000
                    || level.MaterialWeight <= 0 || double.IsNaN(level.MaterialWeight)
                    || double.IsInfinity(level.MaterialWeight))
                || parsed.MaterialsByGrade.Count == 0
                || parsed.MaterialsByGrade.Any(pair => pair.Key < 0
                    || pair.Value == null || pair.Value.ItemTemplateId <= 0 || pair.Value.BaseCount <= 0)
                || parsed.ItemWeightsByRarity.Count == 0
                || parsed.ItemWeightsByRarity.Any(weight => weight <= 0
                    || double.IsNaN(weight) || double.IsInfinity(weight)))
                throw new InvalidOperationException("PVF separate-upgrade table is incomplete.");
            return parsed;
        });

        internal static SeparateUpgradeTable Get() => Table.Value;
    }
}
