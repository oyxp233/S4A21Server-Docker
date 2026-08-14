using DfoServer.Game.Inventory;
using DfoServer.Game.ReviveCoin;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        private const byte IncreaseStatusUnknownErrorCode = 0x01;
        private const byte IncreaseStatusMissingSourceItemErrorCode = 0x11;

        public async Task Handle_ENUM_CMDPACKET_INCREASE_STATUS(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session == null)
            {
                FileLogger.Log($"[{ProtocolName}] INCREASE_STATUS rejected without a session");
                return;
            }

            if (!IncreaseStatusRequest.TryParse(body, out var request))
            {
                await SendIncreaseStatusFailureAsync(
                    session,
                    IncreaseStatusUnknownErrorCode);
                FileLogger.Log(
                    $"[{ProtocolName}] INCREASE_STATUS rejected malformed body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                return;
            }

            if (session.Player == null || session.Player.CharacterId <= 0)
            {
                await SendIncreaseStatusFailureAsync(
                    session,
                    IncreaseStatusUnknownErrorCode);
                FileLogger.Log(
                    $"[{ProtocolName}] INCREASE_STATUS rejected without an active player: slot={request.SlotIndex}");
                return;
            }

            var (characterId, accountId) = ResolveOwner(session);
            ExperienceItemUseResult result;
            try
            {
                result = _experienceItemUseService.UseBySlot(
                    characterId,
                    accountId,
                    InventoryListType.Main,
                    request.SlotIndex,
                    session.Player.CurrentRun == null
                        ? ExperienceItemUseLocation.Town
                        : ExperienceItemUseLocation.Dungeon);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] INCREASE_STATUS failed unexpectedly: cid={characterId} slot={request.SlotIndex} error={ex}");
                await SendIncreaseStatusFailureAsync(
                    session,
                    IncreaseStatusUnknownErrorCode);
                return;
            }

            if (result.Success)
            {
                if (result.ItemTemplateId != ReviveCoinService.ConsumableItemId)
                {
                    session.Player.Level = result.NewLevel;
                    session.Player.Exp = result.NewExp;
                }
            }

            var ackBody = result.Success
                ? IncreaseStatusAckBuilder.BuildExperienceSuccess(session.Player.UserId)
                : IncreaseStatusAckBuilder.BuildError(
                    GetExperienceItemFailureErrorCode(result.Status));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.INCREASE_STATUS,
                ackBody));

            if (!result.Success)
            {
                if (result.Status == ExperienceItemUseStatus.NotApplicable
                    || result.Status == ExperienceItemUseStatus.ConsumeFailed)
                {
                    await RefreshExperienceSourceSlotAsync(
                        session,
                        characterId,
                        request.SlotIndex,
                        "rejected-source");
                }

                FileLogger.Log(
                    $"[{ProtocolName}] INCREASE_STATUS rejected: status={result.Status} item={result.ItemTemplateId} slot={request.SlotIndex} detail={result.Detail}");
                return;
            }

            // 客户端先结束 0x001E 指令，再消费背包变更，
            // 最后应用 EXP/SP/TP 的绝对状态快照。
            await RefreshExperienceSourceSlotAsync(
                session,
                characterId,
                request.SlotIndex,
                "post-commit");

            if (result.ItemTemplateId == ReviveCoinService.ConsumableItemId)
            {
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    ReviveCoinService.WalletSlot);
            }
            else
            {
                await _experienceItemNotifications.SendAsync(session, result);
            }
            FileLogger.Log(
                $"[{ProtocolName}] INCREASE_STATUS experience: item={result.ItemTemplateId} slot={request.SlotIndex} remaining={result.ConsumedItem?.RemainingStackCount ?? 0} grant={result.GrantedExp} level={result.PreviousLevel}->{result.NewLevel} exp={result.PreviousExp}->{result.NewExp}");
        }

        private static Task SendIncreaseStatusFailureAsync(
            EnhancedClientSession session,
            byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.INCREASE_STATUS,
                IncreaseStatusAckBuilder.BuildError(errorCode)));

        private async Task RefreshExperienceSourceSlotAsync(
            EnhancedClientSession session,
            int characterId,
            short slotIndex,
            string reason)
        {
            try
            {
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    slotIndex);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] INCREASE_STATUS {reason} slot refresh failed: cid={characterId} slot={slotIndex} error={ex.Message}; falling back to the full Main list");
                try
                {
                    await _refresh.SendItemListRefresh(session, InventoryListType.Main);
                }
                catch (Exception fallbackEx)
                {
                    // 事务提交后的背包刷新是辅助通知，
                    // 权威 EXP 快照仍必须送达客户端。
                    FileLogger.Log(
                        $"[{ProtocolName}] INCREASE_STATUS {reason} full Main refresh failed: cid={characterId} slot={slotIndex} error={fallbackEx.Message}");
                }
            }
        }

        internal static byte GetExperienceItemFailureErrorCode(
            ExperienceItemUseStatus status)
        {
            switch (status)
            {
                case ExperienceItemUseStatus.NotApplicable:
                case ExperienceItemUseStatus.ConsumeFailed:
                    return IncreaseStatusMissingSourceItemErrorCode;
                default:
                    return IncreaseStatusUnknownErrorCode;
            }
        }
    }
}
