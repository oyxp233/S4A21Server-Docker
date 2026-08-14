using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public static class EmblemCompoundRequestParser
    {
        public static bool TryParse(byte[] body, out EmblemCompoundRequest request)
        {
            request = null;
            if (body == null || body.Length < 13)
                return false;

            var count = body[0];
            if (count < 2 || count > 5 || body.Length != 1 + count * 6)
                return false;

            var parsed = new EmblemCompoundRequest();
            for (var index = 0; index < count; index++)
            {
                var offset = 1 + index * 6;
                var itemTemplateId = BitConverter.ToInt32(body, offset);
                var slotIndex = BitConverter.ToInt16(body, offset + 4);
                if (itemTemplateId <= 0 || slotIndex < 0)
                    return false;

                parsed.Inputs.Add(new EmblemCompoundInput
                {
                    ItemTemplateId = itemTemplateId,
                    SlotIndex = slotIndex,
                });
            }

            request = parsed;
            return true;
        }
    }
}
