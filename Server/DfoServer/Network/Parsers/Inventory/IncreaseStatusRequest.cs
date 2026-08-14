using System;

namespace DfoServer.Network.Parsers.Inventory
{
    internal sealed class IncreaseStatusRequest
    {
        internal const int BodyLength = 2;

        internal short SlotIndex { get; private set; }

        internal static bool TryParse(byte[] body, out IncreaseStatusRequest request)
        {
            request = null;
            if (body == null || body.Length != BodyLength)
                return false;

            // +0x00 [i16] 主背包槽位。抓包请求不含背包类型或物品 ID；
            // 背包槽位不可为负数，因此符号位无效。
            var slotIndex = BitConverter.ToInt16(body, 0);
            if (slotIndex < 0)
                return false;

            request = new IncreaseStatusRequest { SlotIndex = slotIndex };
            return true;
        }
    }
}
