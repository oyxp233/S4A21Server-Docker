using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_REQUEST_ITEM_LOCK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryParseEquipmentItemLockRequest(body, out var listType, out var slotIndex))
                return;

            var (cid, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
                return;

            var ok = EquipmentItemLockCommitService.TryLock(
                lease,
                listType,
                slotIndex,
                out var result);

            if (!ok || !result.Success)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010B,
                    EquipmentItemLockBuilder.BuildLockError(result?.ErrorCode ?? (byte)19)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010B,
                EquipmentItemLockBuilder.BuildLockAck(result.ListType, result.SlotIndex)));
            await SendEquipmentItemLockEntryRefresh(session, result, 1, "ITEM_LOCK_LIST_DELTA_LOCK");
        }

        public async Task Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryParseEquipmentItemLockRequest(body, out var listType, out var slotIndex))
                return;

            var (cid, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
                return;

            var ok = EquipmentItemLockCommitService.TryUnlock(
                lease,
                listType,
                slotIndex,
                out var result);

            if (!ok || !result.Success)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010C,
                    EquipmentItemLockBuilder.BuildUnlockError(result?.ErrorCode ?? (byte)19, result?.RemainingSeconds ?? 0)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010C,
                EquipmentItemLockBuilder.BuildUnlockAck(result.ListType, result.SlotIndex, 0)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00FC,
                EquipmentItemLockBuilder.BuildUnlockNotice(result.ListType, result.SlotIndex)));
        }

        public async Task Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK_CANCEL(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryParseEquipmentItemLockRequest(body, out var listType, out var slotIndex))
                return;

            var (cid, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
                return;

            var ok = EquipmentItemLockCommitService.TryCancelUnlock(
                lease,
                listType,
                slotIndex,
                out var result);

            if (!ok || !result.Success)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010D,
                    EquipmentItemLockBuilder.BuildLockError(result?.ErrorCode ?? (byte)19)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010D,
                EquipmentItemLockBuilder.BuildUnlockCancelAck(result.ListType, result.SlotIndex)));
            await SendEquipmentItemLockEntryRefresh(session, result, 1, "ITEM_LOCK_LIST_DELTA_CANCEL");
        }

        private async Task SendEquipmentItemLockEntryRefresh(EnhancedClientSession session, EquipmentItemLockResult result, byte state, string tag)
        {
            var entries = new[]
            {
                new EquipmentItemLockEntry
                {
                    ListType = result.ListType,
                    SlotIndex = result.SlotIndex,
                    State = state,
                    RemainingSeconds = state == 2 ? result.RemainingSeconds : 0
                }
            };

            InventoryRefreshSender.LogEquipmentItemLockList(tag, entries);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00FB,
                EquipmentItemLockBuilder.BuildLockList(entries)));
        }

        private static bool TryParseEquipmentItemLockRequest(byte[] body, out InventoryListType listType, out short slotIndex)
        {
            listType = InventoryListType.Main;
            slotIndex = 0;
            if (body == null || body.Length < 3)
                return false;

            listType = (InventoryListType)body[0];
            slotIndex = BitConverter.ToInt16(body, 1);
            return true;
        }

        private void LogEquipmentItemLockList(string tag, System.Collections.Generic.IReadOnlyList<EquipmentItemLockEntry> locks)
        {
            var builder = new StringBuilder();
            builder.Append($"[{ProtocolName}] {tag}: count={locks?.Count ?? 0}");
            if (locks != null)
            {
                foreach (var item in locks)
                    builder.Append($" ({item.ListType},{item.SlotIndex},state={item.State},remain={item.RemainingSeconds})");
            }

            FileLogger.Log(builder.ToString());
        }
    }
}
