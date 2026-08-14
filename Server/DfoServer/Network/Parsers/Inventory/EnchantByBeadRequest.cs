using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public sealed class EnchantByBeadRequest
    {
        public InventoryListType BeadListType { get; set; }

        public short BeadSlotIndex { get; set; }

        public InventoryListType TargetListType { get; set; }

        public short TargetSlotIndex { get; set; }

        public static bool TryParse(byte[] body, out EnchantByBeadRequest request)
        {
            request = null;
            if (body == null || body.Length < 6)
                return false;

            // 86 客户端 0x0110 请求体: byte 宝珠空间 + int16 宝珠槽位 + byte 装备空间 + int16 装备槽位。
            request = new EnchantByBeadRequest
            {
                BeadListType = (InventoryListType)body[0],
                BeadSlotIndex = BitConverter.ToInt16(body, 1),
                TargetListType = (InventoryListType)body[3],
                TargetSlotIndex = BitConverter.ToInt16(body, 4),
            };
            return true;
        }

        public EnchantByBeadCommand ToCommand()
        {
            return new EnchantByBeadCommand
            {
                BeadListType = BeadListType,
                BeadSlotIndex = BeadSlotIndex,
                TargetListType = TargetListType,
                TargetSlotIndex = TargetSlotIndex,
            };
        }
    }
}
