using DfoServer.GameWorld;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon.BloodAltar
{
    internal sealed class BloodAltarRewardPlanningService
    {
        internal BloodAltarParticipantSettlementRuntime Prepare(
            BloodAltarDungeonRuntime altar,
            int dungeonLevel,
            int difficulty,
            uint rewardExperience,
            int clearTimeMilliseconds,
            DnfLcg lcg)
        {
            if (altar == null)
                throw new ArgumentNullException(nameof(altar));
            if (lcg == null)
                throw new ArgumentNullException(nameof(lcg));
            if (!altar.IsDungeonComplete)
            {
                throw new InvalidOperationException(
                    "Blood altar settlement was requested before all rounds completed.");
            }

            var definition = altar.Definition.Rewards;
            if (definition == null || !definition.IsAvailable)
            {
                throw new InvalidOperationException(
                    "Blood altar reward definition is unavailable.");
            }

            var rewardCount = definition.GetRewardCardCount(
                altar.CompletedRounds,
                altar.Definition.MaxRounds);
            var rewards = new List<ClearRewardGenerator.CardReward>(
                rewardCount + 1);
            for (var index = 0; index < rewardCount; index++)
            {
                var kind = definition.ClassifyCandidate(
                    altar.CompletedRounds,
                    lcg.Next(definition.RewardRollScale));
                rewards.Add(CreateReward(
                    kind,
                    dungeonLevel,
                    difficulty,
                    definition.GoldAmountWeight,
                    lcg));
            }

            if (altar.Definition.Kind == BloodAltarDungeonKind.Ultimate)
            {
                var point = definition.CalculateUltimatePoint(
                    altar.CaptureCompletedUltimateDifficulties());
                if (definition.TryResolveUltimateRewardItem(
                        point,
                        lcg.Next(100),
                        out var itemId))
                {
                    rewards.Add(new ClearRewardGenerator.CardReward
                    {
                        IsGold = false,
                        ItemId = itemId,
                        StackCount = 1,
                    });
                }
            }

            return new BloodAltarParticipantSettlementRuntime(
                new BloodAltarSettlementPlan(
                    altar.CompletedRounds,
                    altar.Definition.MaxRounds,
                    clearTimeMilliseconds,
                    rewardExperience,
                    rewards));
        }

        internal static int ScaleGold(int baseGold, float multiplier)
        {
            if (baseGold <= 0
                || multiplier <= 0f
                || float.IsNaN(multiplier)
                || float.IsInfinity(multiplier))
            {
                return 0;
            }

            var scaled = baseGold * (double)multiplier;
            return scaled >= int.MaxValue
                ? int.MaxValue
                : (int)Math.Floor(scaled);
        }

        private static ClearRewardGenerator.CardReward CreateReward(
            BloodAltarRewardCandidateKind kind,
            int dungeonLevel,
            int difficulty,
            float goldAmountWeight,
            DnfLcg lcg)
        {
            switch (kind)
            {
                case BloodAltarRewardCandidateKind.Gold:
                    var gold = ClearRewardGenerator.GenerateGoldCard(
                        dungeonLevel,
                        difficulty,
                        lcg);
                    var scaledGold = ScaleGold(
                        gold.GoldAmount,
                        goldAmountWeight);
                    return scaledGold > 0
                        ? new ClearRewardGenerator.CardReward
                        {
                            IsGold = true,
                            GoldAmount = scaledGold,
                        }
                        : CreateEmptyReward();
                case BloodAltarRewardCandidateKind.Item:
                    var item = ClearRewardGenerator.GenerateItemCard(
                        dungeonLevel,
                        difficulty,
                        lcg);
                    return item.ItemId > 0 && item.StackCount > 0
                        ? item
                        : CreateEmptyReward();
                default:
                    return CreateEmptyReward();
            }
        }

        private static ClearRewardGenerator.CardReward CreateEmptyReward()
            => new ClearRewardGenerator.CardReward
            {
                IsGold = false,
                ItemId = -1,
                StackCount = 0,
            };
    }
}
