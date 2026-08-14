using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public sealed class MagicBoxOpenRequest
    {
        private const byte ClientMainInventoryListType = 0x04;
        private const byte ClientUpgradableLegacyListType = 0x05;

        public short SlotIndex { get; set; }

        public InventoryListType ListType { get; set; }

        public byte RawListType { get; set; }

        public int ItemTemplateId { get; set; }

        public short MaterialSlotIndex { get; set; }

        public int MaterialItemTemplateId { get; set; }

        public int RequestedCount { get; set; }

        public static bool TryParse(byte[] body, out MagicBoxOpenRequest request)
        {
            request = null;
            if (body == null || body.Length < 15)
                return false;

            if (!TryMapListType(body[0], out var listType))
                return false;

            var slotIndex = BitConverter.ToInt16(body, 1);
            var itemTemplateId = BitConverter.ToInt32(body, 3);
            var materialSlotIndex = BitConverter.ToInt16(body, 7);
            var materialItemTemplateId = BitConverter.ToInt32(body, 9);
            var requestedCount = BitConverter.ToUInt16(body, 13);
            if (slotIndex < 0 || requestedCount <= 0 || itemTemplateId <= 0)
                return false;

            var hasMaterial = materialSlotIndex >= 0 || materialItemTemplateId > 0;
            if (!hasMaterial)
            {
                materialSlotIndex = -1;
                materialItemTemplateId = -1;
            }
            else if (materialSlotIndex < 0 || materialItemTemplateId <= 0)
            {
                return false;
            }

            request = new MagicBoxOpenRequest
            {
                SlotIndex = slotIndex,
                ListType = listType,
                RawListType = body[0],
                ItemTemplateId = itemTemplateId,
                MaterialSlotIndex = materialSlotIndex,
                MaterialItemTemplateId = materialItemTemplateId,
                RequestedCount = requestedCount,
            };
            return true;
        }

        public static bool TryParseSingle(byte[] body, out MagicBoxOpenRequest request)
        {
            request = null;
            if (body == null || body.Length < 3)
                return false;

            if (!TryMapListType(body[0], out var listType))
                return false;

            var slotIndex = BitConverter.ToInt16(body, 1);
            var materialSlotIndex = body.Length >= 5 ? BitConverter.ToInt16(body, 3) : (short)-1;
            if (slotIndex < 0)
                return false;

            request = new MagicBoxOpenRequest
            {
                SlotIndex = slotIndex,
                ListType = listType,
                RawListType = body[0],
                ItemTemplateId = 0,
                MaterialSlotIndex = materialSlotIndex >= 0 ? materialSlotIndex : (short)-1,
                MaterialItemTemplateId = materialSlotIndex >= 0 ? 0 : -1,
                RequestedCount = 1,
            };
            return true;
        }

        private static bool TryMapListType(byte rawListType, out InventoryListType listType)
        {
            if (rawListType == (byte)InventoryListType.Main
                || rawListType == ClientMainInventoryListType
                || rawListType == ClientUpgradableLegacyListType)
            {
                listType = InventoryListType.Main;
                return true;
            }

            listType = (InventoryListType)rawListType;
            return false;
        }
    }
}
