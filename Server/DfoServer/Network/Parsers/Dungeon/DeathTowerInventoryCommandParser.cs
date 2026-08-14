using System;
using System.Collections.Generic;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal readonly struct DeathTowerUseStackableCommand
    {
        internal DeathTowerUseStackableCommand(
            short slotIndex,
            InventoryListType listType,
            int instanceValue,
            int expectedItemId)
        {
            SlotIndex = slotIndex;
            ListType = listType;
            InstanceValue = instanceValue;
            ExpectedItemId = expectedItemId;
        }

        internal short SlotIndex { get; }
        internal InventoryListType ListType { get; }
        internal int InstanceValue { get; }
        internal int ExpectedItemId { get; }
    }

    internal readonly struct DeathTowerSortItemCommand
    {
        internal DeathTowerSortItemCommand(
            InventoryListType listType,
            byte category,
            bool hasCategory)
        {
            ListType = listType;
            Category = category;
            HasCategory = hasCategory;
        }

        internal InventoryListType ListType { get; }
        internal byte Category { get; }
        internal bool HasCategory { get; }
    }

    internal static class DeathTowerInventoryCommandParser
    {
        internal static bool TryParseUseStackable(
            byte[] body,
            out DeathTowerUseStackableCommand command)
        {
            command = default;
            if (body == null || body.Length < 7)
                return false;

            command = new DeathTowerUseStackableCommand(
                BitConverter.ToInt16(body, 0),
                (InventoryListType)body[2],
                BitConverter.ToInt32(body, 3),
                body.Length >= 11 ? BitConverter.ToInt32(body, 7) : 0);
            return true;
        }

        internal static bool TryParseMove(
            byte[] body,
            out InventoryMoveRequest command)
        {
            command = null;
            if (body == null || body.Length < 14)
                return false;

            command = new InventoryMoveRequest
            {
                SourceListType = (InventoryListType)body[0],
                SourceSlotIndex = BitConverter.ToInt16(body, 1),
                SourceInstanceValue = BitConverter.ToInt32(body, 3),
                MoveCount = BitConverter.ToInt32(body, 7),
                DestinationListType = (InventoryListType)body[11],
                DestinationSlotIndex = BitConverter.ToInt16(body, 12),
                DestinationInstanceValue = body.Length >= 18
                    ? BitConverter.ToInt32(body, 14)
                    : 0,
            };
            return true;
        }

        internal static bool TryParseSort(
            byte[] body,
            out DeathTowerSortItemCommand command)
        {
            command = default;
            if (body == null || body.Length < 1 || body.Length > 3)
                return false;

            command = new DeathTowerSortItemCommand(
                (InventoryListType)body[0],
                body.Length >= 2 ? body[1] : byte.MaxValue,
                body.Length >= 2);
            return true;
        }

        internal static bool TryParseDelete(
            byte[] body,
            out DeathTowerDeleteItemCommand command)
        {
            command = null;
            if (body == null || body.Length < 2)
                return false;

            var count = body[1];
            if (count == 0 || count > 100)
                return false;

            var requiredLength = 2 + count * 12;
            if (body.Length < requiredLength)
                return false;

            var entries = new List<DeathTowerDeleteItemEntry>(count);
            var offset = 2;
            for (var index = 0; index < count; index++)
            {
                var slotValue = BitConverter.ToUInt16(body, offset + 2);
                if (slotValue > short.MaxValue)
                    return false;

                entries.Add(new DeathTowerDeleteItemEntry(
                    BitConverter.ToUInt16(body, offset),
                    (short)slotValue,
                    BitConverter.ToInt32(body, offset + 4),
                    BitConverter.ToInt32(body, offset + 8)));
                offset += 12;
            }

            command = new DeathTowerDeleteItemCommand(
                (InventoryListType)body[0],
                entries);
            return true;
        }
    }
}
