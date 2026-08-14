using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public static class ChronicleGrowthRequest
    {
        private const int FixedLength = 19;

        public static bool TryParse(byte[] body, out ChronicleGrowthCommand command)
        {
            command = null;
            if (body == null || body.Length != FixedLength)
                return false;

            var parsed = new ChronicleGrowthCommand
            {
                TicketSlotIndex = BitConverter.ToInt16(body, 0),
                TicketItemTemplateId = BitConverter.ToInt32(body, 2),
                TargetSlotIndex = BitConverter.ToInt16(body, 6),
                TargetItemTemplateId = BitConverter.ToInt32(body, 8),
            };

            parsed.Materials.Add(new ChronicleGrowthMaterialRequest
            {
                SlotIndex = BitConverter.ToInt16(body, 13),
                ItemTemplateId = BitConverter.ToInt32(body, 15),
            });

            command = parsed;
            return true;
        }
    }
}
