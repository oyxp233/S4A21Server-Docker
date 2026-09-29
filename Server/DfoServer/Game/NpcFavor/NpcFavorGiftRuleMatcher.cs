using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.NpcFavor
{
    internal enum NpcFavorGiftDisposition
    {
        Neutral = 0,
        Preferred = 1,
        Unpreferred = 2,
        InsufficientCount = 3,
    }

    internal sealed class NpcFavorGiftRuleMatch
    {
        internal NpcFavorGiftDisposition Disposition { get; set; }
        internal int BaseFavorPointDelta { get; set; }
        internal int FavorPointDelta { get; set; }
        internal NpcGiftRule Rule { get; set; }
    }

    /// <summary>
    /// 只解释 NPC PVF 已明确给出的喜好、最低数量、点数和心情倍率。
    /// 好感等级边界及协议字段不在此处推断。
    /// </summary>
    internal static class NpcFavorGiftRuleMatcher
    {
        internal static NpcFavorGiftRuleMatch Match(
            NpcFile npc,
            int favorLevel,
            int itemId,
            string itemGroupName,
            int itemCount,
            int moodIndex)
        {
            if (npc == null || favorLevel <= 0 || itemId <= 0 || itemCount <= 0)
                return Neutral();

            var preferred = FindRule(
                npc.PreferredGiftRules,
                favorLevel,
                itemId,
                itemGroupName);
            if (preferred != null)
            {
                if (itemCount < preferred.MinimumCount)
                {
                    return new NpcFavorGiftRuleMatch
                    {
                        Disposition = NpcFavorGiftDisposition.InsufficientCount,
                        Rule = preferred,
                    };
                }

                var baseDelta = Math.Max(0, preferred.FavorPointChange);
                return new NpcFavorGiftRuleMatch
                {
                    Disposition = NpcFavorGiftDisposition.Preferred,
                    BaseFavorPointDelta = baseDelta,
                    FavorPointDelta = ApplyMoodRate(npc, moodIndex, baseDelta),
                    Rule = preferred,
                };
            }

            var unpreferred = FindRule(
                npc.UnpreferredGiftRules,
                favorLevel,
                itemId,
                itemGroupName);
            if (unpreferred == null)
                return Neutral();

            var dislikedDelta = -Math.Abs(unpreferred.FavorPointChange);
            return new NpcFavorGiftRuleMatch
            {
                Disposition = NpcFavorGiftDisposition.Unpreferred,
                BaseFavorPointDelta = dislikedDelta,
                FavorPointDelta = ApplyMoodRate(npc, moodIndex, dislikedDelta),
                Rule = unpreferred,
            };
        }

        private static NpcGiftRule FindRule(
            IEnumerable<NpcGiftRule> rules,
            int favorLevel,
            int itemId,
            string itemGroupName)
        {
            var candidates = rules?.Where(rule => rule.FavorLevel == favorLevel)
                .ToList();
            if (candidates == null || candidates.Count == 0)
                return null;

            var explicitItem = candidates.FirstOrDefault(rule => rule.ItemId == itemId);
            if (explicitItem != null)
                return explicitItem;

            if (string.IsNullOrWhiteSpace(itemGroupName))
                return null;

            return candidates.FirstOrDefault(rule =>
                !string.IsNullOrWhiteSpace(rule.ItemGroupName)
                && string.Equals(
                    rule.ItemGroupName.Trim(),
                    itemGroupName.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        private static int ApplyMoodRate(NpcFile npc, int moodIndex, int pointDelta)
        {
            var rate = moodIndex >= 0 && moodIndex < npc.FavorRatePerMood.Count
                ? npc.FavorRatePerMood[moodIndex]
                : 100;
            return (int)Math.Round(
                pointDelta * Math.Max(0, rate) / 100d,
                MidpointRounding.AwayFromZero);
        }

        private static NpcFavorGiftRuleMatch Neutral()
            => new NpcFavorGiftRuleMatch
            {
                Disposition = NpcFavorGiftDisposition.Neutral,
            };
    }
}
