using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public static class AvatarDisjointRequestParser
    {
        public static bool TryParse(byte[] body, out AvatarDisjointRequest request)
        {
            request = null;
            if (body == null || body.Length < 2)
                return false;

            // JP clients send slot:int16 and may append the item template id.
            var slot = BitConverter.ToInt16(body, 0);
            if (slot < 0)
                return false;

            request = new AvatarDisjointRequest
            {
                SlotIndex = slot,
                ExpectedItemTemplateId = body.Length >= 6 ? BitConverter.ToInt32(body, 2) : 0,
            };
            return true;
        }
    }
}
