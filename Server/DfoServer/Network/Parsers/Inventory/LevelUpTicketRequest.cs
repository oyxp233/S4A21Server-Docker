using System;

namespace DfoServer.Network.Parsers.Inventory
{
    internal sealed class LevelUpTicketRequest
    {
        internal const int MinimumBodyLength = 3;

        internal short SlotIndex { get; private set; }

        internal byte Reserved { get; private set; }

        internal static bool TryParse(byte[] body, out LevelUpTicketRequest request)
        {
            request = null;
            if (body == null || body.Length < MinimumBodyLength)
                return false;

            // 86 A21 C1/01A2 当前抓包体为 u16 主背包槽位 + u8 保留位。
            // 旧抓包存在同 opcode 后接全零尾部的长体，这里只消费已确认字段。
            var slotIndex = BitConverter.ToInt16(body, 0);
            if (slotIndex < 0)
                return false;

            request = new LevelUpTicketRequest
            {
                SlotIndex = slotIndex,
                Reserved = body[2],
            };
            return true;
        }
    }
}
