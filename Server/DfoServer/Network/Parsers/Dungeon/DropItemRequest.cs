using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal readonly struct DropItemRequest
    {
        internal DropItemRequest(
            ushort positionX,
            ushort positionY,
            InventoryListType listType,
            short slotIndex,
            int count)
        {
            PositionX = positionX;
            PositionY = positionY;
            ListType = listType;
            SlotIndex = slotIndex;
            Count = count;
        }

        internal ushort PositionX { get; }
        internal ushort PositionY { get; }
        internal InventoryListType ListType { get; }
        internal short SlotIndex { get; }
        internal int Count { get; }

        internal static DropItemRequest Parse(byte[] body)
        {
            if (body == null || body.Length != 12)
                throw new ArgumentException("DROP_ITEM body must be exactly 12 bytes.", nameof(body));

            var listType = (InventoryListType)body[4];
            var slotIndex = BitConverter.ToUInt16(body, 5);
            var count = BitConverter.ToInt32(body, 7);
            if (listType != InventoryListType.Main
                || slotIndex > short.MaxValue
                || count <= 0)
            {
                throw new ArgumentException("DROP_ITEM contains an unsupported inventory target or count.", nameof(body));
            }

            return new DropItemRequest(
                BitConverter.ToUInt16(body, 0),
                BitConverter.ToUInt16(body, 2),
                listType,
                (short)slotIndex,
                count);
        }
    }
}
