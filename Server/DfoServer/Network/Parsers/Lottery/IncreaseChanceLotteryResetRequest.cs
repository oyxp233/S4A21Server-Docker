using System;

namespace DfoServer.Network.Parsers.Lottery
{
    public sealed class IncreaseChanceLotteryResetRequest
    {
        public short SlotIndex { get; private set; }

        public int ItemTemplateId { get; private set; }

        public static bool TryParse(byte[] body, out IncreaseChanceLotteryResetRequest request)
        {
            request = null;
            if (body == null || body.Length != 21)
                return false;

            var slotIndex = BitConverter.ToInt16(body, 13);
            var itemTemplateId = BitConverter.ToInt32(body, 17);
            if (slotIndex < 0 || itemTemplateId <= 0)
                return false;

            request = new IncreaseChanceLotteryResetRequest
            {
                SlotIndex = slotIndex,
                ItemTemplateId = itemTemplateId,
            };
            return true;
        }
    }
}
