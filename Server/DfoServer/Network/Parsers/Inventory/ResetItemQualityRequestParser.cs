using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    internal static class ResetItemQualityRequestParser
    {
        private const int BodyLength = 8;

        internal static bool TryParse(byte[] body, out ResetItemQualityRequest request)
        {
            request = null;
            if (body == null || body.Length != BodyLength)
                return false;

            var targetSlotIndex = BitConverter.ToInt16(body, 0);
            var targetItemTemplateId = BitConverter.ToInt32(body, 2);
            var materialSlotIndex = BitConverter.ToInt16(body, 6);

            if (targetSlotIndex < 0
                || materialSlotIndex < 0
                || targetSlotIndex == materialSlotIndex
                || targetItemTemplateId <= 0)
                return false;

            request = new ResetItemQualityRequest
            {
                TargetSlotIndex = targetSlotIndex,
                TargetItemTemplateId = targetItemTemplateId,
                MaterialSlotIndex = materialSlotIndex,
            };
            return true;
        }
    }
}
