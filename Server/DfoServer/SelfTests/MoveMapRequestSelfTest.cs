using System;
using System.Buffers.Binary;
using DfoServer.Network.Parsers.Dungeon;

namespace DfoServer.SelfTests
{
    public static class MoveMapRequestSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== MOVE_MAP_REQUEST selftest ===");
            var failures = 0;

            var body = BuildCurrentClientLayoutFixture();
            Check("64-byte original-client MOVE_MAP body parses",
                MoveMapRequest.TryParse(body, out var request),
                ref failures);
            Check("coordinates and layered flag keep their wire offsets",
                request.NextX == 0x12
                && request.NextY == 0x34
                && request.MoveMode == 0x01,
                ref failures);
            Check("path coordinates and trap bits keep their wire widths",
                request.PathPositionX == 0x04030201
                && request.PathPositionY == 0x08070605
                && request.TrapBits == 0x0A09,
                ref failures);
            Check("all eight member map-clear values remain aligned",
                MatchesMemberMapClearValues(request.MemberMapClearValues.Span),
                ref failures);
            Check("all eight member elapsed values remain aligned",
                MatchesMemberMapElapsedValues(request.MemberMapElapsedValues.Span),
                ref failures);
            Check("timing and state fields end at the body boundary",
                request.ClientTimingToken == 0x4433
                && request.ClientStateFlag == 0x55,
                ref failures);
            Check("truncated MOVE_MAP body is rejected",
                !MoveMapRequest.TryParse(new byte[MoveMapRequest.BodyLength - 1], out _),
                ref failures);
            var extendedBody = new byte[100];
            body.CopyTo(extendedBody, 0);
            for (var index = MoveMapRequest.BodyLength;
                 index < extendedBody.Length;
                 index++)
            {
                extendedBody[index] = (byte)index;
            }
            Check("trailing MOVE_MAP bytes do not block canonical routing",
                MoveMapRequest.TryParse(extendedBody, out var extendedRequest)
                && extendedRequest.NextX == request.NextX
                && extendedRequest.NextY == request.NextY
                && extendedRequest.MoveMode == request.MoveMode,
                ref failures);
            Check("100-byte MOVE_MAP retains the canonical member fields",
                MatchesMemberMapClearValues(
                    extendedRequest.MemberMapClearValues.Span)
                && MatchesMemberMapElapsedValues(
                    extendedRequest.MemberMapElapsedValues.Span)
                && extendedRequest.ClientTimingToken == 0x4433
                && extendedRequest.ClientStateFlag == 0x55,
                ref failures);
            Check("null MOVE_MAP body is rejected",
                !MoveMapRequest.TryParse(null, out _),
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildCurrentClientLayoutFixture()
        {
            var body = new byte[MoveMapRequest.BodyLength];
            var offset = 0;
            body[offset++] = 0x12;
            body[offset++] = 0x34;
            WriteUInt32(body, ref offset, 0x04030201);
            WriteUInt32(body, ref offset, 0x08070605);
            body[offset++] = 0x01;
            WriteUInt16(body, ref offset, 0x0A09);

            for (var index = 0; index < MoveMapRequest.MemberSlotCount; index++)
                WriteUInt16(body, ref offset, (ushort)(0x1100 + index));
            for (var index = 0; index < MoveMapRequest.MemberSlotCount; index++)
                WriteUInt32(body, ref offset, 0x22000000u + (uint)index);

            WriteUInt16(body, ref offset, 0x4433);
            body[offset++] = 0x55;
            if (offset != MoveMapRequest.BodyLength)
                throw new InvalidOperationException("MOVE_MAP fixture layout is invalid.");
            return body;
        }

        private static bool MatchesMemberMapClearValues(ReadOnlySpan<ushort> values)
        {
            if (values.Length != MoveMapRequest.MemberSlotCount)
                return false;
            for (var index = 0; index < values.Length; index++)
                if (values[index] != 0x1100 + index)
                    return false;
            return true;
        }

        private static bool MatchesMemberMapElapsedValues(ReadOnlySpan<uint> values)
        {
            if (values.Length != MoveMapRequest.MemberSlotCount)
                return false;
            for (var index = 0; index < values.Length; index++)
                if (values[index] != 0x22000000u + (uint)index)
                    return false;
            return true;
        }

        private static void WriteUInt16(byte[] body, ref int offset, ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                body.AsSpan(offset, sizeof(ushort)),
                value);
            offset += sizeof(ushort);
        }

        private static void WriteUInt32(byte[] body, ref int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                body.AsSpan(offset, sizeof(uint)),
                value);
            offset += sizeof(uint);
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine(ok ? $"[OK] {name}" : $"[FAIL] {name}");
            if (!ok)
                failures++;
        }
    }
}
