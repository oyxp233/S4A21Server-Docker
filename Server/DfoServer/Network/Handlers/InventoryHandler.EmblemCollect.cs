using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        // 0x03B8 EMBLEM_COLLECT_ITEM (ENUM_CMDPACKET_EMBLEM_COLLECT_ITEM): 徽章回收箱。
        // 请求体布局来自 2026-10-10 实机抓包 (26B): boxSlot(u32) | 未知(u32) | count(u32) |
        // targetSlot(u16) | boxSlot(u16) | boxTemplateId(u32) | targetSlot(u16) | targetUid(u32)。
        private const int EmblemCollectBodyLength = 26;
        private const byte EmblemCollectError = 0x04;

        public async Task Handle_EMBLEM_COLLECT_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var commandType = (ushort)CmdPacketType.EMBLEM_COLLECT_ITEM;
            FileLogger.Log($"[{ProtocolName}] EMBLEM_COLLECT_ITEM type=0x{commandType:X4} raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseEmblemCollectBody(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, new byte[] { 0x00, EmblemCollectError }));
                return;
            }

            var (cid, _) = ResolveOwner(session);
            EquipmentEmblemCollectResult result;
            bool ok;
            var persistenceFailed = false;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryEquipmentAugmentationCommitService.TryCommitEmblemCollect(
                    lease,
                    request.BoxSlotIndex,
                    request.BoxTemplateId,
                    request.TargetSlotIndex,
                    request.TargetUid,
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
                FileLogger.Log($"[{ProtocolName}] EMBLEM_COLLECT_ITEM: FAILED box=0x{request.BoxTemplateId:X8}@{request.BoxSlotIndex} " +
                    $"target=0x{request.TargetUid:X8}@{request.TargetSlotIndex} persistenceFailed={persistenceFailed}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, new byte[] { 0x00, EmblemCollectError }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType,
                BuildEmblemCollectAck(result.TargetSlotIndex, request.TargetUid, 1)));
            await _refresh.SendUpdateItemList(session, InventoryListType.Main,
                new[] { result.BoxSlotIndex, result.ReturnedEmblemSlotIndex });
            await _refresh.SendUpdateItemList(session, result.TargetListType, result.TargetSlotIndex);

            var destroyedEmblem = result.DestroyedEmblemTemplateId > 0
                ? $"0x{result.DestroyedEmblemTemplateId:X8}"
                : "none";
            FileLogger.Log($"[{ProtocolName}] EMBLEM_COLLECT: OK box=0x{result.BoxItemTemplateId:X8}@{result.BoxSlotIndex} " +
                $"target=0x{request.TargetUid:X8}@{result.TargetSlotIndex} returned=0x{result.ReturnedEmblemTemplateId:X8}@{result.ReturnedEmblemSlotIndex} " +
                $"destroyed={destroyedEmblem}");
        }

        internal static bool TryParseEmblemCollectBody(byte[] body, out EmblemCollectRequest request)
        {
            request = default;
            if (body == null || body.Length < EmblemCollectBodyLength)
                return false;

            var boxSlot = BitConverter.ToUInt32(body, 0);
            var count = BitConverter.ToUInt32(body, 8);
            var targetSlot = BitConverter.ToUInt16(body, 12);
            var boxSlotRepeat = BitConverter.ToUInt16(body, 14);
            var boxTemplateId = BitConverter.ToInt32(body, 16);
            var targetSlotRepeat = BitConverter.ToUInt16(body, 20);
            var targetUid = BitConverter.ToInt32(body, 22);

            if (count != 1
                || boxTemplateId <= 0
                || targetUid <= 0
                || boxSlot > short.MaxValue
                || boxSlot != boxSlotRepeat
                || targetSlot != targetSlotRepeat)
                return false;

            request = new EmblemCollectRequest((short)boxSlot, boxTemplateId, (short)targetSlot, targetUid);
            return true;
        }

        private static byte[] BuildEmblemCollectAck(short targetSlot, int targetUid, int returnedCount)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(targetSlot);
            writer.WriteInt32(targetUid);
            writer.WriteByte((byte)Math.Max(0, Math.Min(255, returnedCount)));
            return writer.ToArray();
        }
    }

    internal readonly struct EmblemCollectRequest
    {
        public EmblemCollectRequest(short boxSlotIndex, int boxTemplateId, short targetSlotIndex, int targetUid)
        {
            BoxSlotIndex = boxSlotIndex;
            BoxTemplateId = boxTemplateId;
            TargetSlotIndex = targetSlotIndex;
            TargetUid = targetUid;
        }

        public short BoxSlotIndex { get; }

        public int BoxTemplateId { get; }

        public short TargetSlotIndex { get; }

        public int TargetUid { get; }
    }
}
