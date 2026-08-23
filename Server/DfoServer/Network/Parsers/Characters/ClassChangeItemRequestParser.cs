using DfoServer.Game.Characters;
using System;

namespace DfoServer.Network.Parsers.Characters
{
    internal static class ClassChangeItemRequestParser
    {
        internal static bool TryParse(
            byte[] body,
            out ClassChangeItemRequest request)
        {
            request = null;
            if (body == null || body.Length < 3)
                return false;

            request = new ClassChangeItemRequest
            {
                ItemSlotIndex = BitConverter.ToInt16(body, 0),
                TargetGrowType = body[2],
            };
            return request.ItemSlotIndex >= 0;
        }
    }
}
