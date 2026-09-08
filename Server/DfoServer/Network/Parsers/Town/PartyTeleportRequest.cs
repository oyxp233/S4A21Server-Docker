using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Town
{
    internal readonly struct PartyTeleportRequest
    {
        // A21 实抓包体为 8 字节：前 7 字节是 town/area/x/y/direction，尾部多 1 字节 flag。
        internal const int MinimumBodyLength = 7;

        private PartyTeleportRequest(
            byte townId,
            byte areaId,
            short x,
            short y,
            byte direction)
        {
            TownId = townId;
            AreaId = areaId;
            X = x;
            Y = y;
            Direction = direction;
        }

        internal byte TownId { get; }
        internal byte AreaId { get; }
        internal short X { get; }
        internal short Y { get; }
        internal byte Direction { get; }

        internal static bool TryParse(
            byte[] body,
            out PartyTeleportRequest request)
        {
            request = default;
            if (body == null || body.Length < MinimumBodyLength)
                return false;

            request = new PartyTeleportRequest(
                body[0],
                body[1],
                BinaryPrimitives.ReadInt16LittleEndian(
                    body.AsSpan(2, sizeof(short))),
                BinaryPrimitives.ReadInt16LittleEndian(
                    body.AsSpan(4, sizeof(short))),
                body[6]);
            return true;
        }
    }
}
