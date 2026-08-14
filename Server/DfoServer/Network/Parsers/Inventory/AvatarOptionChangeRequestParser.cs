using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Parsers.Inventory
{
    internal static class AvatarOptionChangeRequestParser
    {
        public static bool TryParse(byte[] body, out InventoryAvatarOptionChangeRequest request)
        {
            request = null;
            if (body == null || body.Length < 13)
                return false;

            request = new InventoryAvatarOptionChangeRequest
            {
                SourceSlotIndex = BitConverter.ToInt16(body, 0),
                SourceItemId = BitConverter.ToInt32(body, 2),
                TargetSlotIndex = BitConverter.ToInt16(body, 6),
                TargetItemId = BitConverter.ToInt32(body, 8),
                AbilityNo = body[12],
            };
            return true;
        }
    }
}
