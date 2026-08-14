using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;

namespace DfoServer.Game.ExpertJob
{
    internal abstract class ExpertJobSelectionRule
    {
        internal int MinimumLevel { get; set; }
        internal int MaximumLevel { get; set; }
        internal int ItemId { get; set; }
        internal int Weight { get; set; }
    }

    internal static class ExpertJobSelectionRuleSelector
    {
        internal static T Select<T>(IReadOnlyList<T> rules, int equipmentLevel)
            where T : ExpertJobSelectionRule
        {
            if (rules == null)
                return null;

            var roll = ServerRandom.Next(10000);
            var accumulated = 0;
            foreach (var rule in rules)
            {
                if (rule == null
                    || equipmentLevel < rule.MinimumLevel
                    || equipmentLevel > rule.MaximumLevel)
                {
                    continue;
                }

                accumulated = (int)Math.Min(
                    10000L,
                    (long)accumulated + Math.Max(0, rule.Weight));
                if (roll < accumulated)
                    return rule;
            }
            return null;
        }
    }
}
