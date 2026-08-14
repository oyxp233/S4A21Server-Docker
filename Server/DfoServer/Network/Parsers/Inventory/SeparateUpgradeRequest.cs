using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    internal sealed class SeparateUpgradeRequest
    {
        internal InventoryListType TargetListType { get; private set; }
        internal short TargetSlotIndex { get; private set; }
        internal int TargetItemTemplateId { get; private set; }
        internal short MaterialSlotIndex { get; private set; }

        internal static bool TryParse(byte[] body, out SeparateUpgradeRequest request)
        {
            request = null;
            if (body == null || body.Length < 15)
                return false;

            var rawListType = body[0];
            InventoryListType targetListType;
            if (rawListType == (byte)InventoryListType.Equipment)
                targetListType = InventoryListType.Equipment;
            else if (rawListType == (byte)InventoryListType.Main)
                targetListType = InventoryListType.Main;
            else
                return false;

            var nameLength = BitConverter.ToInt32(body, 9);
            // The client uses 0 for the ordinary flow and 1/2 for advanced
            // ticket confirmation variants. All packets share this layout.
            if (nameLength <= 0 || nameLength > body.Length - 14
                || 13 + nameLength + 1 != body.Length
                || body[body.Length - 1] > 2)
                return false;

            request = new SeparateUpgradeRequest
            {
                TargetListType = targetListType,
                TargetSlotIndex = BitConverter.ToInt16(body, 1),
                TargetItemTemplateId = BitConverter.ToInt32(body, 3),
                MaterialSlotIndex = BitConverter.ToInt16(body, 7),
            };
            return true;
        }

        internal SeparateUpgradeCommand ToCommand()
        {
            return new SeparateUpgradeCommand
            {
                TargetListType = TargetListType,
                TargetSlotIndex = TargetSlotIndex,
                TargetItemTemplateId = TargetItemTemplateId,
                MaterialSlotIndex = MaterialSlotIndex,
            };
        }
    }
}
