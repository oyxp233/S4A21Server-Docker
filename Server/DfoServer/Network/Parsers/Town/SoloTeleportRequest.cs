using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Town
{
    internal readonly struct SoloTeleportRequest
    {
        // A21 实抓包体为 15 字节：前 8 字节全 0xFF（含义未知，疑似两个 -1 占位），
        // 后 7 字节与组队传送相同：town/area/x/y/direction。
        internal const int MinimumBodyLength = 15;

        private SoloTeleportRequest(
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
            out SoloTeleportRequest request)
        {
            request = default;
            if (body == null || body.Length < MinimumBodyLength)
                return false;

            request = new SoloTeleportRequest(
                body[8],
                body[9],
                BinaryPrimitives.ReadInt16LittleEndian(
                    body.AsSpan(10, sizeof(short))),
                BinaryPrimitives.ReadInt16LittleEndian(
                    body.AsSpan(12, sizeof(short))),
                body[14]);
            return true;
        }
    }
}
