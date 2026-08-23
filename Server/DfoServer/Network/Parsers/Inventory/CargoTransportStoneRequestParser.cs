using DfoServer.Game.Inventory;
using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory
{
    internal static class CargoTransportStoneRequestParser
    {
        internal static bool TryParse(
            byte[] body,
            out CargoTransportStoneRequest request)
        {
            request = null;
            if (body == null || body.Length < 9)
                return false;

            var targetCharacterSlot = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5, 4));
            request = new CargoTransportStoneRequest
            {
                StoneSlotIndex = (short)BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0, 2)),
                TargetSlotIndex = (short)BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2)),
                IsCreatureTransportStone = body[4] != 0,
                TargetCharacterSlotIndex = targetCharacterSlot > int.MaxValue
                    ? int.MaxValue
                    : (int)targetCharacterSlot,
            };
            return true;
        }
    }
}
