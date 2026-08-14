using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_INVEST_ITEM_AMPLIFY_OPTION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!InvestItemAmplifyOptionRequestParser.TryParse(body, out var request))
            {
                FileLogger.Log($"[{ProtocolName}] INVEST_ITEM_AMPLIFY_OPTION: parse failed body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x00CD,
                    InvestItemAmplifyOptionAckBuilder.BuildError(InvestItemAmplifyOptionResult.ErrorInvalidRequest)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] INVEST_ITEM_AMPLIFY_OPTION raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} action={request.Action} target=({request.TargetSlotIndex},0x{request.TargetItemTemplateId:X8}) material=({request.MaterialSlotIndex},0x{request.MaterialItemTemplateId:X8}) selected={request.SelectedOption}");

            var (cid, _) = ResolveOwner(session);
            InvestItemAmplifyOptionResult result;
            bool ok;
            var persistenceFailed = false;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryEquipmentAmplifyOptionCommitService.TryCommitInvest(
                    lease,
                    request,
                    out result,
                    out persistenceFailed);
            }
            else
            {
                ok = false;
                result = null;
            }

            if (!ok)
            {
                var errorCode = persistenceFailed
                    ? InvestItemAmplifyOptionResult.ErrorInvalidRequest
                    : result != null
                        ? result.ErrorCode
                        : InvestItemAmplifyOptionResult.ErrorInvalidRequest;
                FileLogger.Log($"[{ProtocolName}] INVEST_ITEM_AMPLIFY_OPTION: FAILED error=0x{errorCode:X2} persistenceFailed={persistenceFailed} action={request.Action} targetSlot={request.TargetSlotIndex} materialSlot={request.MaterialSlotIndex} selected={request.SelectedOption}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x00CD,
                    InvestItemAmplifyOptionAckBuilder.BuildError(errorCode)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x00CD,
                InvestItemAmplifyOptionAckBuilder.BuildSuccess(result)));

            await _refresh.SendSortItemLockRefresh(session, InventoryListType.Main);
            FileLogger.Log($"[{ProtocolName}] INVEST_ITEM_AMPLIFY_OPTION: OK action={request.Action} targetSlot={result.TargetSlotIndex} materialSlot={result.MaterialSlotIndex} selected={request.SelectedOption} amplifyType=0x{result.AmplifyType:X2} amplifyValue={result.AmplifyValue} amplifyLevel={result.AmplifyLevel}");
        }
    }
}
