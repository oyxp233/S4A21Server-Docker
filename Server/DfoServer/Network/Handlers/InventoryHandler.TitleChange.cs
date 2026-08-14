using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_USE_TITLE_CHANGE_ITEM(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            FileLogger.Log(
                $"[{ProtocolName}] USE_TITLE_CHANGE_ITEM raw({body?.Length ?? 0}B): "
                + (body != null ? BitConverter.ToString(body) : "null"));

            if (!TitleChangeRequestParser.TryParse(body, out var request))
            {
                await SendTitleChangeError(session, header.type);
                return;
            }

            var (characterId, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
            {
                await SendTitleChangeError(session, header.type);
                return;
            }

            lock (lease.SyncRoot)
            {
                request.SourceItemId = lease.Inventory
                    .GetItem(InventoryListType.Main, request.SourceSlotIndex)?.ItemId ?? 0;
                request.TargetItemId = lease.Inventory
                    .GetItem(InventoryListType.Main, request.TargetSlotIndex)?.ItemId ?? 0;
            }

            if (request.SourceItemId <= 0
                || request.TargetItemId <= 0
                || !InventoryTitleChangeRuleResolver.TryResolveTitleChange(
                    request.SourceItemId,
                    request.TargetItemId,
                    out var resolution))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] USE_TITLE_CHANGE_ITEM: rule rejected "
                    + $"source=({request.SourceSlotIndex},0x{request.SourceItemId:X8}) "
                    + $"target=({request.TargetSlotIndex},0x{request.TargetItemId:X8})");
                await SendTitleChangeError(session, header.type);
                return;
            }

            var ok = InventoryTitleChangeCommitService.TryCommitTitleChange(
                lease,
                request,
                resolution,
                out var result,
                out var persistenceFailed);

            if (!ok || result == null || !result.Success)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] USE_TITLE_CHANGE_ITEM: FAILED error={result?.Error} persistenceFailed={persistenceFailed} "
                    + $"source=({request.SourceSlotIndex},0x{request.SourceItemId:X8}) "
                    + $"target=({request.TargetSlotIndex},0x{request.TargetItemId:X8})");
                await SendTitleChangeError(session, header.type);
                return;
            }

            // 称号变更界面不会消费定点 0x000E 刷新，
            // 在处理成功 ACK 前通过 0x000D 重建主背包缓存。
            await _refresh.SendItemListRefresh(session, InventoryListType.Main);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                TitleChangeAckBuilder.BuildSuccess(result)));

            FileLogger.Log(
                $"[{ProtocolName}] USE_TITLE_CHANGE_ITEM: OK source=({request.SourceSlotIndex},"
                + $"0x{result.SourceItemId:X8}) remaining={result.SourceRemainingCount} "
                + $"target=({request.TargetSlotIndex},0x{result.TargetItemId:X8}) "
                + $"result=0x{result.ResultItemId:X8}");
        }

        private static Task SendTitleChangeError(
            EnhancedClientSession session,
            ushort type)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                type,
                TitleChangeAckBuilder.BuildError()));
        }
    }
}
