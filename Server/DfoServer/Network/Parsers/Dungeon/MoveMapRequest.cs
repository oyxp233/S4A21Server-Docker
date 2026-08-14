using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Dungeon
{
    public readonly struct MoveMapRequest
    {
        public const int BodyLength = 64;
        public const int MemberSlotCount = 8;

        private MoveMapRequest(
            byte nextX,
            byte nextY,
            uint pathPositionX,
            uint pathPositionY,
            byte moveMode,
            ushort trapBits,
            ushort[] memberMapClearValues,
            uint[] memberMapElapsedValues,
            ushort clientTimingToken,
            byte clientStateFlag)
        {
            NextX = nextX;
            NextY = nextY;
            PathPositionX = pathPositionX;
            PathPositionY = pathPositionY;
            MoveMode = moveMode;
            TrapBits = trapBits;
            MemberMapClearValues = memberMapClearValues;
            MemberMapElapsedValues = memberMapElapsedValues;
            ClientTimingToken = clientTimingToken;
            ClientStateFlag = clientStateFlag;
        }

        public byte NextX { get; }
        public byte NextY { get; }
        public uint PathPositionX { get; }
        public uint PathPositionY { get; }
        public byte MoveMode { get; }
        public ushort TrapBits { get; }
        public ReadOnlyMemory<ushort> MemberMapClearValues { get; }
        public ReadOnlyMemory<uint> MemberMapElapsedValues { get; }
        public ushort ClientTimingToken { get; }
        public byte ClientStateFlag { get; }

        public static bool TryParse(byte[] body, out MoveMapRequest request)
        {
            request = default;
            if (body == null || body.Length < BodyLength)
                return false;

            var offset = 0;
            var nextX = body[offset++];
            var nextY = body[offset++];
            var pathPositionX = ReadUInt32(body, ref offset);
            var pathPositionY = ReadUInt32(body, ref offset);
            var moveMode = body[offset++];
            var trapBits = ReadUInt16(body, ref offset);

            var memberMapClearValues = new ushort[MemberSlotCount];
            for (var index = 0; index < memberMapClearValues.Length; index++)
                memberMapClearValues[index] = ReadUInt16(body, ref offset);

            var memberMapElapsedValues = new uint[MemberSlotCount];
            for (var index = 0; index < memberMapElapsedValues.Length; index++)
                memberMapElapsedValues[index] = ReadUInt32(body, ref offset);

            var clientTimingToken = ReadUInt16(body, ref offset);
            var clientStateFlag = body[offset++];
            if (offset != BodyLength)
                return false;

            request = new MoveMapRequest(
                nextX,
                nextY,
                pathPositionX,
                pathPositionY,
                moveMode,
                trapBits,
                memberMapClearValues,
                memberMapElapsedValues,
                clientTimingToken,
                clientStateFlag);
            return true;
        }

        private static ushort ReadUInt16(byte[] body, ref int offset)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(
                body.AsSpan(offset, sizeof(ushort)));
            offset += sizeof(ushort);
            return value;
        }

        private static uint ReadUInt32(byte[] body, ref int offset)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(
                body.AsSpan(offset, sizeof(uint)));
            offset += sizeof(uint);
            return value;
        }
    }
}
