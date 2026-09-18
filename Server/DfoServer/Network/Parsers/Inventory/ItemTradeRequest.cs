using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Parsers.Inventory
{
    internal static class ItemTradeRequest
    {
        internal static bool IsTradePeer(byte[] body) => body?.Length >= 3 && body[2] == 1;

        internal static bool TryPeer(byte[] body, bool response, out ushort uid, out int peer, out bool refused)
        {
            uid = 0; peer = 0; refused = false;
            if (!IsTradePeer(body) || body.Length < 7) return false;
            uid = BitConverter.ToUInt16(body, 0);
            peer = BitConverter.ToInt32(body, 3);
            if (uid == 0) return false;
            if (response && body.Length >= 9 && BitConverter.ToUInt16(body, 7) == 0x85)
            {
                refused = true;
                return (body.Length == 9 || body.Length == 16) && ZeroTail(body, 9);
            }
            return (body.Length == 7 || body.Length == 8 || body.Length == 16) && ZeroTail(body, 7);
        }

        internal static bool TryState(byte[] body, out byte state)
        {
            state = 0;
            if (body == null || (body.Length != 1 && body.Length != 4 && body.Length != 8)
                || !ZeroTail(body, 1)) return false;
            state = body[0];
            return state <= 5;
        }

        internal static bool IsTradeMove(byte[] body) => body?.Length >= 12 && (body[0] == 4 || body[11] == 4);

        internal static bool TryMove(byte[] body, out InventoryMoveRequest request)
        {
            request = null;
            // Current A21 live requests have 28 bytes. The reference capture
            // has four additional zero bytes; do not require its padded length.
            if (body == null || (body.Length != 28 && body.Length != 32)
                || !ZeroTail(body, 28)
                || !(((body[0] == 0 || body[0] == (byte)InventoryListType.CrystalWarehouse) && body[11] == 4)
                    || (body[0] == 4 && (body[11] == 0 || body[11] == (byte)InventoryListType.CrystalWarehouse))))
                return false;
            // Destination and uninterpreted fields cannot override the server offer.
            request = new InventoryMoveRequest
            {
                SourceListType = (InventoryListType)body[0], SourceSlotIndex = BitConverter.ToInt16(body, 1),
                SourceInstanceValue = BitConverter.ToInt32(body, 3), MoveCount = BitConverter.ToInt32(body, 7),
                DestinationListType = (InventoryListType)body[11], DestinationSlotIndex = BitConverter.ToInt16(body, 12),
                DestinationInstanceValue = BitConverter.ToInt32(body, 14)
            };
            // Live A21 crystal drag uses list 36 with the absolute Main slot.
            // Keep the wire list in the ACK, but do not admit ordinary items via this alias.
            if (request.SourceListType == InventoryListType.CrystalWarehouse
                && (!InventoryExchangeCommitService.IsCrystalSlot(request.SourceSlotIndex)
                    || !InventoryService.TryResolveMainVirtualItemId(request.SourceSlotIndex, out var id)
                    || id != request.SourceInstanceValue)) return false;
            if (request.DestinationListType == InventoryListType.CrystalWarehouse
                && (!InventoryService.TryResolveMainVirtualSlotByItemId(request.SourceInstanceValue, out var slot, out _)
                    || !InventoryExchangeCommitService.IsCrystalSlot(slot))) return false;
            return request.MoveCount > 0;
        }

        private static bool ZeroTail(byte[] body, int start)
        {
            for (int i = start; i < body.Length; i++) if (body[i] != 0) return false;
            return true;
        }
    }
}
