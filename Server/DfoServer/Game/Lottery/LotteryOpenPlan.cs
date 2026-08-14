using System;

namespace DfoServer.Game.Lottery
{
    public enum LotteryOpenMode
    {
        ConfirmedRegular,
        DirectDoubleReward,
        DirectRegularPhaseStart,
    }

    public sealed class LotteryOpenPlan
    {
        private LotteryOpenPlan(LotteryOpenMode mode, int usedCount, bool hasActiveDoubleReward)
        {
            Mode = mode;
            UsedCount = usedCount;
            HasActiveDoubleReward = hasActiveDoubleReward;
        }

        public LotteryOpenMode Mode { get; }

        public int UsedCount { get; }

        public bool HasActiveDoubleReward { get; }

        public bool ShouldSendRegularPhaseStart => Mode == LotteryOpenMode.DirectRegularPhaseStart;

        public bool UseDoubleReward => Mode == LotteryOpenMode.DirectDoubleReward;

        public bool RefreshPremiumBeforePhaseStart => Mode == LotteryOpenMode.DirectRegularPhaseStart;

        public bool RefreshPremiumAfterOpen => Mode == LotteryOpenMode.DirectDoubleReward;

        public static LotteryOpenPlan ConfirmedRegular()
            => new LotteryOpenPlan(LotteryOpenMode.ConfirmedRegular, 0, false);

        public static LotteryOpenPlan DirectDoubleReward(int usedCount)
            => new LotteryOpenPlan(LotteryOpenMode.DirectDoubleReward, usedCount, true);

        public static LotteryOpenPlan DirectRegularPhaseStart(int usedCount, bool hasActiveDoubleReward)
            => new LotteryOpenPlan(LotteryOpenMode.DirectRegularPhaseStart, usedCount, hasActiveDoubleReward);
    }

    public sealed class LotteryOpenPlanner
    {
        private readonly LotteryDoubleRewardPolicy _doubleRewardPolicy;

        public LotteryOpenPlanner(LotteryDoubleRewardPolicy doubleRewardPolicy)
        {
            _doubleRewardPolicy = doubleRewardPolicy
                ?? throw new ArgumentNullException(nameof(doubleRewardPolicy));
        }

        public LotteryOpenPlan Resolve(int characterId, int accountId, bool isDirectFastOpen)
        {
            if (!isDirectFastOpen)
                return LotteryOpenPlan.ConfirmedRegular();

            var usedCount = _doubleRewardPolicy.GetUsedCount(characterId);
            var hasActiveDoubleReward = usedCount < LotteryDoubleRewardPolicy.DailyLimit
                && _doubleRewardPolicy.HasActiveBenefit(accountId);
            return ResolveDirectFastOpen(isDirectFastOpen, hasActiveDoubleReward, usedCount);
        }

        public static LotteryOpenPlan ResolveDirectFastOpen(
            bool isDirectFastOpen,
            bool hasActiveDoubleReward,
            int usedCount)
        {
            if (!isDirectFastOpen)
                return LotteryOpenPlan.ConfirmedRegular();

            if (hasActiveDoubleReward && usedCount < LotteryDoubleRewardPolicy.DailyLimit)
                return LotteryOpenPlan.DirectDoubleReward(usedCount);

            return LotteryOpenPlan.DirectRegularPhaseStart(usedCount, hasActiveDoubleReward);
        }
    }
}
