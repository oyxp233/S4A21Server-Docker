using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public static class PurifyItemRequestParser
    {
        public static bool TryParse(byte[] body, out PurifyItemRequest request)
        {
            request = null;
            if (body == null || body.Length < 12)
                return false;

            request = new PurifyItemRequest
            {
                TargetSlotIndex = BitConverter.ToInt16(body, 0),
                TargetItemTemplateId = BitConverter.ToInt32(body, 2),
                MaterialSlotIndex = BitConverter.ToInt16(body, 6),
                MaterialItemTemplateId = BitConverter.ToInt32(body, 8),
            };
            return true;
        }
    }
}
