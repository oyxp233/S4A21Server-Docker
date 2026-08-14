using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Parsers.ExpertJob
{
    internal sealed class DisjointMachineRequest
    {
        internal ushort OwnerUserId { get; set; }
        internal short TargetSlotIndex { get; set; }
        internal InventoryListType ItemSpace { get; set; }

        internal static bool TryParse(byte[] body, out DisjointMachineRequest request)
        {
            request = null;
            if (body == null || body.Length != 5)
                return false;

            var itemSpace = (InventoryListType)body[4];
            var slotIndex = BitConverter.ToInt16(body, 2);
            if (BitConverter.ToUInt16(body, 0) == 0
                || slotIndex < 0
                || itemSpace != InventoryListType.Main)
                return false;

            request = new DisjointMachineRequest
            {
                OwnerUserId = BitConverter.ToUInt16(body, 0),
                TargetSlotIndex = slotIndex,
                ItemSpace = itemSpace,
            };
            return true;
        }
    }
}
