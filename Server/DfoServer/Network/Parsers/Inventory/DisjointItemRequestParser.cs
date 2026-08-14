using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public static class DisjointItemRequestParser
    {
        public static bool TryParse(byte[] body, out DisjointItemRequest request)
        {
            request = null;
            if (body == null || body.Length < 5)
                return false;

            request = new DisjointItemRequest
            {
                TargetSlotIndex = BitConverter.ToInt16(body, 0),
                ItemSpace = (InventoryListType)body[2],
                DisjointItemSlotIndex = BitConverter.ToInt16(body, 3),
                ContextValue = body.Length >= 9 ? BitConverter.ToInt32(body, 5) : 0,
            };

            return request.TargetSlotIndex >= 0;
        }
    }
}
