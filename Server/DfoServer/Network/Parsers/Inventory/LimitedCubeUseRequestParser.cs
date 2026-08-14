using System;

namespace DfoServer.Network.Parsers.Inventory
{
    internal sealed class LimitedCubeUseRequest
    {
        public short TargetSlotIndex { get; set; }

        public int TargetItemId { get; set; }

        public short CubeSlotIndex { get; set; }
    }

    internal static class LimitedCubeUseRequestParser
    {
        // 当前客户端 0x0152：目标槽 int16、目标物品 ID int32、变更箱槽 int16。
        public static bool TryParse(byte[] body, out LimitedCubeUseRequest request)
        {
            request = null;
            if (body == null || body.Length < 8)
                return false;

            request = new LimitedCubeUseRequest
            {
                TargetSlotIndex = BitConverter.ToInt16(body, 0),
                TargetItemId = BitConverter.ToInt32(body, 2),
                CubeSlotIndex = BitConverter.ToInt16(body, 6),
            };

            return request.TargetSlotIndex >= 0
                && request.TargetItemId > 0
                && request.CubeSlotIndex >= 0
                && request.CubeSlotIndex != request.TargetSlotIndex;
        }
    }
}
