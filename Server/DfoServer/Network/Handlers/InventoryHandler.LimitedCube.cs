using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_USE_LIMIT_CUBE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            FileLogger.Log(
                $"[{ProtocolName}] USE_LIMIT_CUBE raw({body?.Length ?? 0}B): "
                + (body != null ? BitConverter.ToString(body) : "null"));

            if (!LimitedCubeUseRequestParser.TryParse(body, out var useRequest))
            {
                await SendLimitedCubeError(session, header.type);
                return;
            }

            var (characterId, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
            {
                await SendLimitedCubeError(session, header.type);
                return;
            }

            int cubeItemId;
            int targetItemId;
            lock (lease.SyncRoot)
            {
                cubeItemId = lease.Inventory
                    .GetItem(InventoryListType.Main, useRequest.CubeSlotIndex)?.ItemId ?? 0;
                targetItemId = lease.Inventory
                    .GetItem(InventoryListType.Main, useRequest.TargetSlotIndex)?.ItemId ?? 0;
            }

            if (targetItemId != useRequest.TargetItemId
                || cubeItemId <= 0
                || !InventoryTitleChangeRuleResolver.TryResolveLimitedCube(
                    cubeItemId,
                    targetItemId,
                    out var resolution))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] USE_LIMIT_CUBE: rule rejected "
                    + $"cube=({useRequest.CubeSlotIndex},0x{cubeItemId:X8}) "
                    + $"target=({useRequest.TargetSlotIndex},0x{targetItemId:X8}) "
                    + $"expectedTarget=0x{useRequest.TargetItemId:X8}");
                await SendLimitedCubeError(session, header.type);
                return;
            }

            var changeRequest = new InventoryTitleChangeRequest
            {
                SourceSlotIndex = useRequest.CubeSlotIndex,
                SourceItemId = cubeItemId,
                TargetSlotIndex = useRequest.TargetSlotIndex,
                TargetItemId = targetItemId,
            };

            var ok = InventoryTitleChangeCommitService.TryCommitLimitedCube(
                lease,
                changeRequest,
                resolution,
                out var result,
                out var persistenceFailed);

            if (!ok || result == null || !result.Success)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] USE_LIMIT_CUBE: FAILED error={result?.Error} persistenceFailed={persistenceFailed} "
                    + $"cube=({useRequest.CubeSlotIndex},0x{cubeItemId:X8}) "
                    + $"target=({useRequest.TargetSlotIndex},0x{targetItemId:X8})");
                await SendLimitedCubeError(session, header.type);
                return;
            }

            // 受限变更箱界面不会消费定点 0x000E 刷新，
            // 在处理成功 ACK 前通过 0x000D 重建主背包缓存。
            await _refresh.SendItemListRefresh(session, InventoryListType.Main);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                LimitedCubeAckBuilder.BuildSuccess(result)));

            FileLogger.Log(
                $"[{ProtocolName}] USE_LIMIT_CUBE: OK cube=({useRequest.CubeSlotIndex},"
                + $"0x{result.SourceItemId:X8}) remaining={result.SourceRemainingCount} "
                + $"target=({useRequest.TargetSlotIndex},0x{result.TargetItemId:X8}) "
                + $"result=0x{result.ResultItemId:X8}");
        }

        private static Task SendLimitedCubeError(
            EnhancedClientSession session,
            ushort type)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                type,
                LimitedCubeAckBuilder.BuildError()));
        }
    }
}
