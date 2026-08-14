using System;

namespace DfoServer.Network.Parsers.Lottery
{
    public sealed class LotteryItemUseRequest
    {
        public ushort Phase { get; set; }

        public short SlotIndex { get; set; }

        public static bool TryParse(byte[] body, out LotteryItemUseRequest request)
        {
            request = null;
            if (body == null || body.Length < 4)
                return false;

            request = new LotteryItemUseRequest
            {
                Phase = BitConverter.ToUInt16(body, 0),
                SlotIndex = BitConverter.ToInt16(body, 2),
            };
            return request.Phase <= 1 && request.SlotIndex >= 0;
        }
    }
}
