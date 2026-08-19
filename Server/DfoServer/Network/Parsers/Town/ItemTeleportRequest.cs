using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Town
{
    internal readonly struct ItemTeleportRequest
    {
        // A21: slot + item id + reserved + town id + target x/y.
        internal const int MinimumBodyLength = 13;

        private ItemTeleportRequest(
            short itemSlot,
            int itemTemplateId,
            byte reserved,
            ushort targetTownId,
            short targetX,
            short targetY,
            int trailingLength)
        {
            ItemSlot = itemSlot;
            ItemTemplateId = itemTemplateId;
            Reserved = reserved;
            TargetTownId = targetTownId;
            TargetX = targetX;
            TargetY = targetY;
            TrailingLength = trailingLength;
        }

        internal short ItemSlot { get; }
        internal int ItemTemplateId { get; }
        internal byte Reserved { get; }
        internal ushort TargetTownId { get; }
        internal short TargetX { get; }
        internal short TargetY { get; }
        internal int TrailingLength { get; }

        internal static bool TryParse(
            byte[] body,
            out ItemTeleportRequest request)
        {
            request = default;
            if (body == null || body.Length < MinimumBodyLength)
                return false;

            request = new ItemTeleportRequest(
                BinaryPrimitives.ReadInt16LittleEndian(
                    body.AsSpan(0, sizeof(short))),
                BinaryPrimitives.ReadInt32LittleEndian(
                    body.AsSpan(2, sizeof(int))),
                body[6],
                BinaryPrimitives.ReadUInt16LittleEndian(
                    body.AsSpan(7, sizeof(short))),
                BinaryPrimitives.ReadInt16LittleEndian(
                    body.AsSpan(9, sizeof(short))),
                BinaryPrimitives.ReadInt16LittleEndian(
                    body.AsSpan(11, sizeof(short))),
                body.Length - MinimumBodyLength);
            return request.ItemTemplateId > 0;
        }
    }
}
