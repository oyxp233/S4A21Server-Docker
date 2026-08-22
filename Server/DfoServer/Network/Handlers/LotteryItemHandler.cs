using DfoServer.Game.Inventory;
using DfoServer.Game.Lottery;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Lottery;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class LotteryItemHandler
    {
        private const string ProtocolName = "GameProtocol";

        private readonly LotteryItemOpenService _openService;
        private readonly LotteryOpenPlanner _openPlanner;
        private readonly LotteryOpenSessionCoordinator _sessions;
        private readonly LotteryItemResponseSender _responses;
        private readonly IInventoryOverflowRewardSink _overflowRewardSink;

        public LotteryItemHandler(
            LotteryItemOpenService openService,
            LotteryOpenPlanner openPlanner,
            LotteryOpenSessionCoordinator sessions,
            LotteryItemResponseSender responses)
            : this(
                openService,
                openPlanner,
                sessions,
                responses,
                RejectingInventoryOverflowRewardSink.Instance)
        {
        }

        internal LotteryItemHandler(
            LotteryItemOpenService openService,
            LotteryOpenPlanner openPlanner,
            LotteryOpenSessionCoordinator sessions,
            LotteryItemResponseSender responses,
            IInventoryOverflowRewardSink overflowRewardSink)
        {
            _openService = openService ?? throw new ArgumentNullException(nameof(openService));
            _openPlanner = openPlanner ?? throw new ArgumentNullException(nameof(openPlanner));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _responses = responses ?? throw new ArgumentNullException(nameof(responses));
            _overflowRewardSink = overflowRewardSink
                ?? RejectingInventoryOverflowRewardSink.Instance;
        }

        public async Task HandleUseLotteryItem(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            if (!LotteryItemUseRequest.TryParse(body, out var request))
            {
                await SendError(session);
                return;
            }

            if (await TryRejectExpiredSourceAsync(session, request.SlotIndex))
            {
                await SendError(session);
                return;
            }

            if (request.Phase == 0)
            {
                if (!TryInspect(session, request.SlotIndex, out var source))
                {
                    await SendError(session);
                    FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: phase0 rejected slot={request.SlotIndex}");
                    return;
                }

                var phase0OpenPlan = LotteryOpenPlan.ConfirmedRegular();
                if (!await TryOpenWithPending(session, request.SlotIndex, phase0OpenPlan, true))
                {
                    await SendError(session);
                    FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: phase0 open failed slot={request.SlotIndex} item=0x{source.ItemTemplateId:X8} count={source.StackCount}");
                    return;
                }

                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: phase0 opened slot={request.SlotIndex} item=0x{source.ItemTemplateId:X8} count={source.StackCount}");
                return;
            }

            var hadPending = _sessions.TryGet(
                session.SessionId,
                request.SlotIndex,
                out var pendingOpen);
            var isDirectFastOpen = request.Phase == 1 && !hadPending;
            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            var openPlan = pendingOpen?.OpenPlan
                ?? _openPlanner.Resolve(characterId, accountId, isDirectFastOpen);
            if (openPlan.ShouldSendRegularPhaseStart)
                openPlan = LotteryOpenPlan.ConfirmedRegular();

            if (!await TryOpenWithPending(session, request.SlotIndex, openPlan, !hadPending))
            {
                await SendError(session);
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: open failed phase={request.Phase} slot={request.SlotIndex} mode={openPlan.Mode}");
            }
        }

        public async Task HandleOverflowInfo(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] OVERFLOW_INFO raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            if (!IsLotteryOverflowConfirm(body))
                return;

            if (!_sessions.TryGet(session.SessionId, null, out var pending))
            {
                FileLogger.Log($"[{ProtocolName}] OVERFLOW_INFO: ignored lottery-shaped confirm without pending phase0");
                return;
            }

            var openPlan = pending.OpenPlan ?? LotteryOpenPlan.ConfirmedRegular();
            if (!await TryOpen(
                    session,
                    pending.SlotIndex,
                    openPlan,
                    () => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        0x00D9,
                        LotteryOverflowConfirmAckBuilder.Build(body)))))
            {
                await SendError(session);
                FileLogger.Log($"[{ProtocolName}] OVERFLOW_INFO: pending lottery open failed slot={pending.SlotIndex}");
            }
        }

        public async Task HandleIncreaseChanceLotteryReset(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!IncreaseChanceLotteryResetRequest.TryParse(body, out var request))
            {
                await SendResetResult(session, 1, false);
                return;
            }

            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
            {
                await SendResetResult(session, 1, false);
                return;
            }

            LotteryProgressSnapshot progress;
            int updatedGold;
            if (!_openService.TryResetProgress(
                    lease,
                    accountId,
                    request.SlotIndex,
                    request.ItemTemplateId,
                    out progress,
                    out updatedGold))
            {
                await SendResetResult(session, 1, false);
                FileLogger.Log($"[{ProtocolName}] INCREASE_CHANCE_LOTTERY_RESET rejected: cid={characterId} slot={request.SlotIndex} item=0x{request.ItemTemplateId:X8}");
                return;
            }

            await SendResetResult(session, 0, true);
            await LotteryItemResponseSender.SendProgress(session, progress);
            await _responses.SendGoldRefresh(session);
            FileLogger.Log($"[{ProtocolName}] INCREASE_CHANCE_LOTTERY_RESET ok: cid={characterId} account={accountId} item=0x{request.ItemTemplateId:X8} gold={updatedGold}");
        }

        private static Task SendResetResult(EnhancedClientSession session, int result, bool showSuccess)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.INCREASE_CHANCE_LOTTERY_RESET,
                IncreaseChanceLotteryPacketBuilder.BuildResetResponse(result, showSuccess)));
        }

        internal static bool IsLotteryOverflowConfirm(byte[] body)
        {
            return body != null
                && body.Length == 3
                && body[0] == 0x01
                && body[1] == 0x1B
                && body[2] == 0x00;
        }

        public void ClearSession(Guid sessionId)
        {
            _sessions.Remove(sessionId);
        }

        private bool TryInspect(
            EnhancedClientSession session,
            short slotIndex,
            out LotterySourceContext source)
        {
            var (characterId, _) = SessionOwnerResolver.Resolve(session);
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
            {
                source = null;
                return false;
            }

            lock (lease.SyncRoot)
                return _openService.CanOpen(lease.Inventory, slotIndex, out source);
        }

        private async Task<bool> TryOpen(
            EnhancedClientSession session,
            short slotIndex,
            LotteryOpenPlan openPlan,
            Func<Task> sendCommittedAck)
        {
            openPlan = openPlan ?? LotteryOpenPlan.ConfirmedRegular();
            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
                return false;

            LotteryOpenReservation reservation;
            lock (lease.SyncRoot)
            {
                if (!_sessions.TryReserveOpen(
                        session.SessionId,
                        slotIndex,
                        pending =>
                        {
                            return _openService.TryCreateReservation(
                                lease.Inventory,
                                accountId,
                                pending.SlotIndex,
                                pending.OpenPlan ?? openPlan,
                                out var created)
                                    ? created
                                    : null;
                        },
                        out reservation))
                {
                    return false;
                }
            }

            if (!_openService.TryOpen(
                    lease,
                    accountId,
                    reservation,
                    _overflowRewardSink,
                    out var result)
                || result == null)
            {
                _sessions.ReleaseOpen(session.SessionId, reservation);
                if (result?.SourceExpiredDeleted == true)
                    await _responses.SendSourceSlotRefresh(session, result.SourceSlotIndex);
                return false;
            }

            _sessions.CompleteOpen(session.SessionId, reservation);
            if (sendCommittedAck != null)
                await sendCommittedAck();
            await _responses.SendOpenResult(session, lease.Inventory, result);
            if (openPlan.RefreshPremiumAfterOpen)
                await _responses.SendPremiumServiceRefresh(session, characterId, accountId);
            if (result.UsableCountState != null)
                await SendUsableCountLimitUpdateAsync(session, result.UsableCountState);

            var progressText = result.Progress == null
                ? string.Empty
                : $" progress={result.Progress.NewRewardIndex} claimed={result.Progress.ClaimedRewardIndexes.Count} autoReset={result.Progress.AutoReset}";
            FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} remaining={result.SourceRemainingStackCount} gold={result.ConsumedGold}->{result.UpdatedGold} mode={openPlan.Mode} double={result.UsedDoubleReward} mailbox={result.DeliveredToMailbox} rewards={string.Join(",", result.Rewards.Select(reward => $"{reward.ListType}:0x{reward.ItemTemplateId:X8}x{reward.GrantedCount}@{reward.SlotIndex}"))}{progressText}");
            return true;
        }

        private async Task<bool> TryRejectExpiredSourceAsync(
            EnhancedClientSession session,
            short slotIndex)
        {
            var (characterId, _) = SessionOwnerResolver.Resolve(session);
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
                return false;

            int itemTemplateId;
            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        session.SessionId,
                        characterId))
                {
                    return false;
                }

                var source = lease.Inventory.GetItem(InventoryListType.Main, slotIndex);
                if (!InventoryItemLifecycleService.IsExpired(
                        source,
                        InventoryItemLifecycleService.UtcNowUnixSeconds()))
                {
                    return false;
                }

                itemTemplateId = source.ItemId;
            }

            InventoryMutationResult mutation = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "lottery-expired-source",
                (connection, transaction) =>
                    InventoryItemLifecycleService.TryRemoveExpiredSource(
                        lease.Inventory,
                        InventoryListType.Main,
                        slotIndex,
                        itemTemplateId,
                        InventoryItemLifecycleService.UtcNowUnixSeconds(),
                        out mutation));
            if (!committed || mutation == null)
                return false;

            await _responses.SendSourceSlotRefresh(session, slotIndex);
            FileLogger.Log(
                $"[{ProtocolName}] USE_LOTTERY_ITEM: expired source removed " +
                $"cid={characterId} item=0x{itemTemplateId:X8} slot={slotIndex}");
            return true;
        }

        private static bool TryGetOwnedInventoryLease(
            EnhancedClientSession session,
            int characterId,
            out InventoryLease lease)
        {
            lease = null;
            return session != null
                && session.SessionId != Guid.Empty
                && characterId > 0
                && InventoryContext.TryGetLease(characterId, out lease)
                && lease.IsOwnedBy(session.SessionId);
        }

        private async Task<bool> TryOpenWithPending(
            EnhancedClientSession session,
            short slotIndex,
            LotteryOpenPlan openPlan,
            bool ensurePending)
        {
            if (ensurePending)
                _sessions.Set(session.SessionId, slotIndex, openPlan);

            if (await TryOpen(session, slotIndex, openPlan, null))
                return true;

            _sessions.Remove(session.SessionId);
            return false;
        }

        private static Task SendUsableCountLimitUpdateAsync(
            EnhancedClientSession session,
            UsableCountLimitState state)
        {
            if (session == null || state == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x021E,
                UsableCountLimitPacketBuilder.BuildUpdateBody(state)));
        }

        private static Task SendError(EnhancedClientSession session)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x001B,
                LotteryItemAckBuilder.BuildError()));
        }
    }
}
