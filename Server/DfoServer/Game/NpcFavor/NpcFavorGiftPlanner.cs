using PvfLib;
using System;

namespace DfoServer.Game.NpcFavor
{
    internal static class NpcFavorProgressionPolicy
    {
        internal const int MaximumFavorPoint = 3200;
        internal const int TrustedFavorPoint = 3000;
        internal const int MaximumTrustedNpcCount = 3;

        internal static int ResolveMaximumFavorPoint(NpcFile npc)
            => npc?.FavorableRelationshipVersion >= 2
                && npc.FavorLevelPoints.Count >= 3
                    ? Math.Max(1, npc.FavorLevelPoints[npc.FavorLevelPoints.Count - 1])
                    : MaximumFavorPoint;

        internal static int ResolveTrustedFavorPoint(NpcFile npc)
            => npc?.FavorableRelationshipVersion >= 2
                && npc.FavorLevelPoints.Count >= 3
                    ? Math.Max(1, npc.FavorLevelPoints[npc.FavorLevelPoints.Count - 2])
                    : TrustedFavorPoint;

        internal static int ResolveFavorLevel(int favorPoint)
        {
            var point = Math.Clamp(favorPoint, 0, MaximumFavorPoint);
            if (point < 1000)
                return 1;
            if (point < 2000)
                return 2;
            if (point < TrustedFavorPoint)
                return 3;
            return 4;
        }
    }

    internal enum NpcFavorGiftPlanError
    {
        None = 0,
        InvalidRequest = 1,
        CharacterLevelTooLow = 2,
        NpcNotSupported = 3,
        ItemNotSupported = 4,
        InvalidItemCount = 5,
    }

    internal sealed class NpcFavorGiftPlan
    {
        internal bool Success { get; set; }
        internal NpcFavorGiftPlanError Error { get; set; }
        internal bool IsSpecialGift { get; set; }
        internal NpcFavorGiftDisposition Disposition { get; set; }
        internal NpcFavorGiftRequest Request { get; set; }
    }

    /// <summary>
    /// 把已解码的赠礼参数转换成可提交请求。这里只使用 PVF 中能直接验证的规则；
    /// 包体字段顺序、NPC 心情来源和响应布局由协议层在抓包确认后提供。
    /// </summary>
    internal static class NpcFavorGiftPlanner
    {
        internal static NpcFavorGiftPlan Plan(
            NpcFavorDefinitionCatalog catalog,
            int characterLevel,
            int npcId,
            int currentFavorPoint,
            short slotIndex,
            int itemId,
            int itemCount,
            int moodIndex,
            int gameDayId,
            Func<int, int, int> randomInclusive)
        {
            if (catalog == null
                || npcId < 0
                || slotIndex < 0
                || itemId <= 0
                || itemCount <= 0
                || gameDayId <= 0
                || randomInclusive == null)
            {
                return Fail(NpcFavorGiftPlanError.InvalidRequest);
            }

            if (characterLevel < catalog.System.ConditionLevel)
                return Fail(NpcFavorGiftPlanError.CharacterLevelTooLow);
            if (!catalog.TryGetNpc(npcId, out var npc))
                return Fail(NpcFavorGiftPlanError.NpcNotSupported);

            if (catalog.TryGetSpecialGiftRule(itemId, out var specialRule))
            {
                if (itemCount != catalog.System.SpecialGiftItemCount)
                    return Fail(NpcFavorGiftPlanError.InvalidItemCount);

                var minimum = Math.Min(
                    specialRule.MinimumPointGain,
                    specialRule.MaximumPointGain);
                var maximum = Math.Max(
                    specialRule.MinimumPointGain,
                    specialRule.MaximumPointGain);
                var rolled = Math.Clamp(
                    randomInclusive(minimum, maximum),
                    minimum,
                    maximum);
                return Success(
                    npcId,
                    slotIndex,
                    itemId,
                    itemCount,
                    npc.DefaultFavor,
                    rolled,
                    catalog.System.SpecialGiftActionLimit,
                    NpcFavorProgressionPolicy.ResolveMaximumFavorPoint(npc),
                    NpcFavorProgressionPolicy.ResolveTrustedFavorPoint(npc),
                    gameDayId,
                    NpcFavorGiftDisposition.Preferred,
                    isSpecialGift: true);
            }

            catalog.TryResolveItemGroup(itemId, out var itemGroupName);
            var favorLevel = NpcFavorProgressionPolicy.ResolveFavorLevel(
                currentFavorPoint);
            var match = NpcFavorGiftRuleMatcher.Match(
                npc,
                favorLevel,
                itemId,
                itemGroupName,
                itemCount,
                moodIndex);
            if (string.IsNullOrWhiteSpace(itemGroupName)
                && match.Disposition == NpcFavorGiftDisposition.Neutral)
            {
                return Fail(NpcFavorGiftPlanError.ItemNotSupported);
            }

            return Success(
                npcId,
                slotIndex,
                itemId,
                itemCount,
                npc.DefaultFavor,
                match.FavorPointDelta,
                npc.MaxGiftPerDay,
                NpcFavorProgressionPolicy.ResolveMaximumFavorPoint(npc),
                NpcFavorProgressionPolicy.ResolveTrustedFavorPoint(npc),
                gameDayId,
                match.Disposition,
                isSpecialGift: false);
        }

        private static NpcFavorGiftPlan Success(
            int npcId,
            short slotIndex,
            int itemId,
            int itemCount,
            int defaultFavorPoint,
            int favorPointDelta,
            int dailyLimit,
            int maximumFavorPoint,
            int trustedFavorPoint,
            int gameDayId,
            NpcFavorGiftDisposition disposition,
            bool isSpecialGift)
            => new NpcFavorGiftPlan
            {
                Success = true,
                Error = NpcFavorGiftPlanError.None,
                IsSpecialGift = isSpecialGift,
                Disposition = disposition,
                Request = new NpcFavorGiftRequest
                {
                    NpcId = npcId,
                    SlotIndex = slotIndex,
                    ExpectedItemId = itemId,
                    ItemCount = itemCount,
                    DefaultFavorPoint = Math.Max(0, defaultFavorPoint),
                    FavorPointDelta = favorPointDelta,
                    MaximumFavorPoint = maximumFavorPoint,
                    TrustedFavorPoint = trustedFavorPoint,
                    GameDayId = gameDayId,
                    NpcDailyGiftLimit = dailyLimit,
                },
            };

        private static NpcFavorGiftPlan Fail(NpcFavorGiftPlanError error)
            => new NpcFavorGiftPlan
            {
                Success = false,
                Error = error,
            };
    }
}
