using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Parsers.Inventory
{
    internal static class TitleChangeRequestParser
    {
        public static bool TryParse(byte[] body, out InventoryTitleChangeRequest request)
        {
            request = null;
            if (body == null || body.Length < 4)
                return false;

            request = new InventoryTitleChangeRequest
            {
                SourceSlotIndex = BitConverter.ToInt16(body, 0),
                TargetSlotIndex = BitConverter.ToInt16(body, 2),
            };
            return request.SourceSlotIndex >= 0
                && request.TargetSlotIndex >= 0
                && request.SourceSlotIndex != request.TargetSlotIndex;
        }
    }
}
