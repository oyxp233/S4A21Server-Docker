using DfoServer.Game.Inventory;
using DfoServer.Game.Lottery;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class LotteryItemResponseSender
    {
        private const string ProtocolName = "GameProtocol";

        private readonly LotteryDoubleRewardPolicy _doubleRewardPolicy;
        private readonly InventoryRefreshSender _refresh;
        private readonly string _connectionString;
        private readonly Func<byte[], Task> _broadcastGamePacket;

        public LotteryItemResponseSender(
            LotteryDoubleRewardPolicy doubleRewardPolicy,
            InventoryRefreshSender refresh,
            string connectionString,
            Func<byte[], Task> broadcastGamePacket = null)
        {
            _doubleRewardPolicy = doubleRewardPolicy
                ?? throw new ArgumentNullException(nameof(doubleRewardPolicy));
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            _connectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentException("A database connection string is required.", nameof(connectionString));
            _broadcastGamePacket = broadcastGamePacket;
        }

        internal async Task SendOpenResult(
            EnhancedClientSession session,
            InventoryService inventory,
            LotteryOpenResult result)
        {
            var displayRewards = LotteryPresentationPolicy.ResolveDisplayRewards(result?.Rewards);
            var mainRewards = displayRewards
                .Where(reward => reward.ListType == InventoryListType.Main)
                .ToList();
            var displayReward = displayRewards.FirstOrDefault();
            var displayItem = LotteryPresentationPolicy.ResolveResultCore(inventory, displayReward);
            var displayValue = LotteryPresentationPolicy.ResolveDisplayValue(
                displayItem,
                displayReward,
                displayRewards);
            var useDoubleResultFlow = LotteryPresentationPolicy.ShouldUseDoubleRewardResultFlow(
                result.UsedDoubleReward,
                displayRewards);
            displayValue = LotteryPresentationPolicy.ResolveNativeDisplayValue(
                displayValue,
                useDoubleResultFlow);

            await SendNativeResult(
                session,
                result,
                inventory,
                displayReward,
                displayItem,
                displayValue);

            if (result.Progress != null)
            {
                if (result.Progress.AutoReset)
                    await SendClearedProgress(
                        session,
                        result.Progress.ItemTemplateId);
                else
                    await SendProgress(session, result.Progress);
            }

            var refreshRewards = ResolveMainRewardUpdatesAfterNativeResult(
                displayReward,
                mainRewards,
                useDoubleResultFlow);
            if (result.Progress != null
                && mainRewards.Count > 0
                && !result.DeliveredToMailbox)
                await _refresh.SendItemListRefresh(session, InventoryListType.Main);
            else
                await SendRewardUpdates(session, refreshRewards);

            var firstNoticeItem = LotteryPresentationPolicy.ResolveResultCore(
                inventory,
                mainRewards.FirstOrDefault());
            await BroadcastNotices(
                session,
                inventory,
                mainRewards,
                firstNoticeItem,
                suppressDuplicateNotices: !useDoubleResultFlow,
                forceNotice: result.Progress != null);
            await SendAvatarOrPetUpdates(session, result.Rewards);
            if (result.RequiredItemChangedSlots.Count > 0)
            {
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    result.RequiredItemChangedSlots);
            }
            if (LotteryPresentationPolicy.ShouldSendGoldRefresh(result))
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, 0);
            if (result.DeliveredToMailbox)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0063,
                    MailboxHandler.BuildMailboxAlarmNotification(1)));
            }
        }

        internal static Task SendProgress(
            EnhancedClientSession session,
            LotteryProgressSnapshot progress)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x03D8,
                IncreaseChanceLotteryPacketBuilder.BuildAllState(progress)));
        }

        internal static Task SendEmptyProgress(EnhancedClientSession session)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x03D8,
                IncreaseChanceLotteryPacketBuilder.BuildAllState(
                    (LotteryProgressSnapshot)null)));
        }

        internal static Task SendClearedProgress(
            EnhancedClientSession session,
            int itemTemplateId)
        {
            return SendProgress(session, new LotteryProgressSnapshot
            {
                ItemTemplateId = itemTemplateId,
                NewRewardIndex = -1,
            });
        }

        public async Task SendPremiumServiceRefresh(
            EnhancedClientSession session,
            int characterId,
            int accountId)
        {
            try
            {
                var serviceData = PremiumService.BuildPremiumServiceData(
                    _connectionString,
                    accountId,
                    _doubleRewardPolicy.BuildPremiumServiceUsage(characterId));
                var writer = new GamePacketWriter();
                writer.WriteByte(1);
                writer.WriteUInt16(1);
                writer.WriteBytes(serviceData);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0312,
                    writer.ToArray()));
            }
            catch (Exception exception)
            {
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: premium service refresh failed: {exception.Message}");
            }
        }

        internal Task SendGoldRefresh(EnhancedClientSession session)
        {
            return _refresh.SendUpdateItemList(session, InventoryListType.Main, 0);
        }

        private static async Task SendNativeResult(
            EnhancedClientSession session,
            LotteryOpenResult result,
            InventoryService inventory,
            LotteryRewardGrant displayReward,
            ItemCore displayItem,
            int displayValue)
        {
            byte[] resultBody;
            if (displayReward?.ListType == InventoryListType.Avatar)
            {
                resultBody = LotteryItemAckBuilder.BuildAvatarItemResult(
                    result?.SourceSlotIndex ?? (short)-1,
                    displayReward.SlotIndex,
                    displayItem,
                    ResolveAvatarDetail(inventory, displayItem));
            }
            else
            {
                resultBody = LotteryItemAckBuilder.BuildCommonItemResult(
                    result?.SourceSlotIndex ?? (short)-1,
                    displayReward?.SlotIndex ?? (short)-1,
                    displayItem,
                    displayValue);
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B, resultBody));
        }

        private async Task SendRewardUpdates(
            EnhancedClientSession session,
            IReadOnlyList<LotteryRewardGrant> rewards)
        {
            if (rewards == null || rewards.Count == 0)
                return;

            var slots = rewards
                .Where(reward => reward != null && reward.ListType == InventoryListType.Main)
                .Select(reward => reward.SlotIndex)
                .ToHashSet();
            if (slots.Count == 0)
                return;

            await _refresh.SendUpdateItemList(session, InventoryListType.Main, slots);
            FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: refreshed reward slots {string.Join(",", rewards.Where(reward => reward != null && slots.Contains(reward.SlotIndex)).Select(reward => $"0x{reward.ItemTemplateId:X8}@{reward.SlotIndex}"))}");
        }

        internal static IReadOnlyList<LotteryRewardGrant> ResolveMainRewardUpdatesAfterNativeResult(
            LotteryRewardGrant displayReward,
            IReadOnlyList<LotteryRewardGrant> mainRewards,
            bool useDoubleResultFlow)
        {
            var updates = LotteryPresentationPolicy.ResolvePostResultMainRefreshRewards(
                    displayReward,
                    mainRewards,
                    useDoubleResultFlow)
                .Where(reward => reward != null)
                .ToList();

            return updates;
        }

        private async Task BroadcastNotices(
            EnhancedClientSession session,
            InventoryService inventory,
            IReadOnlyList<LotteryRewardGrant> mainRewards,
            ItemCore firstDisplayItem,
            bool suppressDuplicateNotices,
            bool forceNotice)
        {
            if (mainRewards == null || mainRewards.Count == 0)
            {
                await BroadcastNotice(session, firstDisplayItem, forceNotice);
                return;
            }

            for (var index = 0; index < mainRewards.Count; index++)
            {
                if (suppressDuplicateNotices
                    && LotteryPresentationPolicy.ShouldSuppressNotice(
                        mainRewards[index],
                        mainRewards))
                {
                    continue;
                }

                var item = index == 0
                    ? firstDisplayItem
                    : LotteryPresentationPolicy.ResolveResultCore(
                        inventory,
                        mainRewards[index]);
                await BroadcastNotice(session, item, forceNotice);
            }
        }

        private async Task BroadcastNotice(
            EnhancedClientSession session,
            ItemCore displayItem,
            bool forceNotice)
        {
            if (_broadcastGamePacket == null
                || displayItem == null
                || displayItem.ItemId <= 0)
            {
                return;
            }

            var metadata = ItemMetadataResolver.Resolve(displayItem.ItemId);
            if (!forceNotice && !LotteryPresentationPolicy.IsNoticeEligible(metadata))
                return;

            try
            {
                var userUniqueId = session?.Player?.UserId ?? 0;
                if (userUniqueId == 0 && session?.Player?.CharacterId > 0)
                    userUniqueId = (ushort)session.Player.CharacterId;

                var upgradeLevel = displayItem.Upgrade;
                var noticeBody = LotteryItemNoticeBuilder.Build(
                    userUniqueId,
                    displayItem.ItemId,
                    upgradeLevel);
                await _broadcastGamePacket(GamePacketEnvelopeBuilder.Build(0x00, 0x0056, noticeBody));
            }
            catch (Exception exception)
            {
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: notice broadcast failed: {exception.Message}");
            }
        }

        private async Task SendAvatarOrPetUpdates(
            EnhancedClientSession session,
            IReadOnlyList<LotteryRewardGrant> rewards)
        {
            if (rewards == null)
                return;

            var avatarSlots = rewards
                .Where(reward => reward.ListType == InventoryListType.Avatar)
                .Select(reward => reward.SlotIndex)
                .ToHashSet();
            var petSlots = rewards
                .Where(reward => reward.ListType == InventoryListType.Pet)
                .Select(reward => reward.SlotIndex)
                .ToHashSet();
            if (avatarSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Avatar, avatarSlots);
            if (petSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Pet, petSlots);
        }

        private static AvatarDetail ResolveAvatarDetail(InventoryService inventory, ItemCore core)
        {
            if (inventory == null || core == null || core.ItemKind != ItemCore.KindAvatar)
                return null;

            return inventory.AvatarDetails.GetDetail(core.AvatarUid);
        }
    }
}
