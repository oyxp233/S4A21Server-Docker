using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_PURIFY_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!PurifyItemRequestParser.TryParse(body, out var request))
            {
                FileLogger.Log($"[{ProtocolName}] PURIFY_ITEM: parse failed body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x00CC,
                    PurifyItemAckBuilder.BuildError(PurifyItemResult.ErrorInvalidRequest)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] PURIFY_ITEM raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} target=({request.TargetSlotIndex},0x{request.TargetItemTemplateId:X8}) material=({request.MaterialSlotIndex},0x{request.MaterialItemTemplateId:X8})");

            var (cid, _) = ResolveOwner(session);
            PurifyItemResult result;
            bool ok;
            var persistenceFailed = false;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryEquipmentAmplifyOptionCommitService.TryCommitPurify(
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
                    ? PurifyItemResult.ErrorInvalidRequest
                    : result != null
                        ? result.ErrorCode
                        : PurifyItemResult.ErrorInvalidRequest;
                FileLogger.Log($"[{ProtocolName}] PURIFY_ITEM: FAILED error=0x{errorCode:X2} persistenceFailed={persistenceFailed} targetSlot={request.TargetSlotIndex} materialSlot={request.MaterialSlotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x00CC,
                    PurifyItemAckBuilder.BuildError(errorCode)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x00CC,
                PurifyItemAckBuilder.BuildSuccess(result)));

            await _refresh.SendUpdateItemList(session, InventoryListType.Main, new[] { result.TargetSlotIndex, result.MaterialSlotIndex });
            await _refresh.SendSortItemLockRefresh(session, InventoryListType.Main);
            FileLogger.Log($"[{ProtocolName}] PURIFY_ITEM: OK action={result.Action} targetSlot={result.TargetSlotIndex} materialSlot={result.MaterialSlotIndex} amplifyType=0x{result.AmplifyType:X2} amplifyValue={result.AmplifyValue}");
        }
    }
}
