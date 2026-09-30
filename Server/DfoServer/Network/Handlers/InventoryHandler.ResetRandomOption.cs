using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        // CMD 0x01C8 (wire 456) RESET_RANDOM_OPTION: 客户端对魔法封印装备使用品级调整箱时发送
        // (普通装备品级调整仍走 0x0051 RESET_ITEM_ATTR)。
        // 2026-09-28 22:43:09 线上 server.log 抓包证据:
        //   Unhandled CMD type=0x01C8 body(6B): 0B-00-42-00-00-00
        //   targetSlot=11(i16@0, 刚解封的魔法封印装备), materialSlot=66(i16@2, 品级调整箱),
        //   末尾 2B(00-00)语义待确认, 容忍性忽略; body 长度按 >=4 校验。
        // ACK 包体格式(0x01/0x00 单状态字节)无官服抓包证据, 首版最简实现, 待实机验证,
        // 若客户端结果窗不弹需按官服包迭代。
        public async Task Handle_RESET_RANDOM_OPTION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, cid, out var lease)
                || !TryBuildResetRandomOptionRequest(lease.Inventory, body, out var request))
            {
                FileLogger.Log($"[{ProtocolName}] RESET_RANDOM_OPTION reject invalid body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01C8, new byte[] { 0x00 }));
                return;
            }

            ResetItemQualityResult result = null;
            var ok = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "reset-random-option",
                (connection, transaction) =>
                    InventoryEquipmentMutationService.TryResetItemQuality(
                        lease.Inventory,
                        request,
                        out result));

            if (!ok)
            {
                FileLogger.Log($"[{ProtocolName}] RESET_RANDOM_OPTION reject targetSlot={request.TargetSlotIndex} item=0x{request.TargetItemTemplateId:X8} materialSlot={request.MaterialSlotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01C8, new byte[] { 0x00 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01C8, new byte[] { 0x01 }));
            await _refresh.SendUpdateItemList(
                session,
                InventoryListType.Main,
                new[] { result.TargetSlotIndex, result.MaterialSlotIndex });

            FileLogger.Log($"[{ProtocolName}] RESET_RANDOM_OPTION ok targetSlot={result.TargetSlotIndex} item=0x{result.TargetItemTemplateId:X8} material=0x{result.MaterialItemTemplateId:X8}@{result.MaterialSlotIndex} remaining={result.MaterialRemainingCount} quality={result.OldQualitySeed}->{result.NewQualitySeed}");
        }

        // body: targetSlot(i16) + materialSlot(i16), 尾部附加字节容忍性忽略(抓包样本为 2B 00-00)。
        internal static bool TryParseResetRandomOptionBody(byte[] body, out short targetSlot, out short materialSlot)
        {
            targetSlot = 0;
            materialSlot = 0;
            if (body == null || body.Length < 4)
                return false;

            targetSlot = BitConverter.ToInt16(body, 0);
            materialSlot = BitConverter.ToInt16(body, 2);
            return true;
        }

        // itemId 从目标槽实际物品取(TryResetItemQuality 要求 TargetItemTemplateId>0);
        // 槽位为空或目标不是装备时返回 false, 由调用方回失败 ACK。
        internal static bool TryBuildResetRandomOptionRequest(
            InventoryService inventory,
            byte[] body,
            out ResetItemQualityRequest request)
        {
            request = null;
            if (!TryParseResetRandomOptionBody(body, out var targetSlot, out var materialSlot))
                return false;

            var target = inventory?.GetItem(InventoryListType.Main, targetSlot);
            if (target == null || target.ItemKind != ItemCore.KindEquipment)
                return false;

            request = new ResetItemQualityRequest
            {
                TargetSlotIndex = targetSlot,
                TargetItemTemplateId = target.ItemId,
                MaterialSlotIndex = materialSlot,
            };
            return true;
        }
    }
}
