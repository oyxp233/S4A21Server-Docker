using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Parsers.Inventory
{
    internal static class UseDyeRequestParser
    {
        public static bool TryParse(byte[] body, out InventoryDyeRequest request)
        {
            request = null;
            if (body == null || body.Length < 4)
                return false;

            request = new InventoryDyeRequest
            {
                DyeSlotIndex = BitConverter.ToInt16(body, 0),
                AvatarSlotIndex = BitConverter.ToInt16(body, 2),
            };
            return true;
        }
    }
}
