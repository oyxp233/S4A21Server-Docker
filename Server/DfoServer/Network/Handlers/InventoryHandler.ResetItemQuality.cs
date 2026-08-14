using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        private const int WaxItemId = 14; // stackable.lst 中蜜蜡的道具 ID

        public async Task Handle_ENUM_CMDPACKET_RESET_ITEM_ATTR(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            // ---- 蜜蜡路由（根据材料道具 ID 分流，ID==14 走蜜蜡）----
            if (body != null && body.Length >= 8)
            {
                var waxTargetSlot = BitConverter.ToInt16(body, 0);
                var waxTargetItemId = BitConverter.ToInt32(body, 2);
                var waxMaterialSlot = BitConverter.ToInt16(body, 6);
                var (waxCid, _) = ResolveOwner(session);
                InventoryLease waxLease = null;
                if (TryGetOwnedInventoryLease(session, waxCid, out waxLease))
                {
                    var material = waxLease.Inventory.GetItem(InventoryListType.Main, waxMaterialSlot);
                    if (material != null && material.ItemId == WaxItemId)
                    {
                        await HandleWaxReseal(session, header, waxLease, waxCid, waxTargetSlot, waxTargetItemId, waxMaterialSlot);
                        return;
                    }
                }
            }

            // ---- 品级调整箱（原有逻辑，未改动）----

            if (!ResetItemQualityRequestParser.TryParse(body, out var request))
            {
                FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: invalid body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketType.RESET_ITEM_ATTR,
                    ResetItemQualityAckBuilder.BuildError(ResetItemQualityResult.ErrorInvalidRequest)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR raw({body.Length}B): {BitConverter.ToString(body)} target=({request.TargetSlotIndex},0x{request.TargetItemTemplateId:X8}) materialSlot={request.MaterialSlotIndex}");

            var (cid, _) = ResolveOwner(session);
            ResetItemQualityResult result = null;
            InventoryLease lease = null;
            var ok = TryGetOwnedInventoryLease(session, cid, out lease)
                && OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "reset-item-quality",
                    (connection, transaction) =>
                    {
                        return InventoryEquipmentMutationService.TryResetItemQuality(
                            lease.Inventory,
                            request,
                            out result);
                    });

            if (!ok)
            {
                var errorCode = result != null && result.ErrorCode != 0
                    ? result.ErrorCode
                    : ResetItemQualityResult.ErrorInvalidRequest;
                FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: FAILED error=0x{errorCode:X2} targetSlot={request.TargetSlotIndex} materialSlot={request.MaterialSlotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketType.RESET_ITEM_ATTR,
                    ResetItemQualityAckBuilder.BuildError(errorCode)));
                return;
            }

            // 命令 ACK 与 COMPLETE_DISPLAY 刻意分离: 二者同用 0x0051, 但前者 cmd=1。
            // 通用 CMD 状态字节在目标物品 id、容器类型和槽位之前。
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.RESET_ITEM_ATTR,
                ResetItemQualityAckBuilder.BuildSuccess(result)));

            await _refresh.SendUpdateItemList(
                session,
                InventoryListType.Main,
                new[] { result.TargetSlotIndex, result.MaterialSlotIndex });

            if (result.MaterialRemainingCount == 0)
                await _refresh.SendSortItemLockRefresh(session, InventoryListType.Main);

            FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: OK mode={result.Mode} targetSlot={result.TargetSlotIndex} material=0x{result.MaterialItemTemplateId:X8}@{result.MaterialSlotIndex} remaining={result.MaterialRemainingCount} quality={result.OldQualitySeed}->{result.NewQualitySeed}");
        }

        // ---- 蜜蜡 ----

        private async Task HandleWaxReseal(
            EnhancedClientSession session,
            GamePacketHeader header,
            InventoryLease lease,
            int cid,
            short targetSlot,
            int targetItemId,
            short waxSlot)
        {
            WaxResealResult resealResult = null;
            var ok = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "wax-reseal",
                (connection, transaction) =>
                {
                    return InventoryEquipmentMutationService.TryWaxReseal(
                        lease.Inventory,
                        targetSlot,
                        targetItemId,
                        waxSlot,
                        out resealResult);
                });

            if (!ok || resealResult == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] RESET_ITEM_ATTR(Wax): failed cid={cid} targetSlot={targetSlot} targetItem=0x{targetItemId:X8} waxSlot={waxSlot}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketType.RESET_ITEM_ATTR,
                    BuildResetItemAttrAck(0, targetItemId, targetSlot)));
                return;
            }

            FileLogger.Log(
                $"[{ProtocolName}] RESET_ITEM_ATTR(Wax): ok cid={cid} targetSlot={targetSlot} targetItem=0x{targetItemId:X8} waxSlot={waxSlot} waxCost={resealResult.WaxCost} newSealFlag={resealResult.NewSealFlag} newReSealCount={resealResult.NewReSealCount}");

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.RESET_ITEM_ATTR,
                BuildResetItemAttrAck(1, targetItemId, targetSlot)));

            await _refresh.SendUpdateItemList(session, InventoryListType.Main, targetSlot);
            await _refresh.SendUpdateItemList(session, InventoryListType.Main, waxSlot);
        }

        private static byte[] BuildResetItemAttrAck(int resultCode, int targetItemId, short targetSlot)
        {
            var w = new GamePacketWriter();
            w.WriteInt32(targetSlot);
            w.WriteInt32(targetItemId);
            w.WriteInt32(resultCode);
            return w.ToArray();
        }
    }
}
