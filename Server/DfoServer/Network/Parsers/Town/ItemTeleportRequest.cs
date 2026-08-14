using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Town
{
    internal readonly struct ItemTeleportRequest
    {
        internal const int BodyLength = 8;

        private ItemTeleportRequest(
            short type,
            int itemTemplateId,
            byte reserved,
            byte targetTownId)
        {
            Type = type;
            ItemTemplateId = itemTemplateId;
            Reserved = reserved;
            TargetTownId = targetTownId;
        }

        internal short Type { get; }
        internal int ItemTemplateId { get; }
        internal byte Reserved { get; }
        internal byte TargetTownId { get; }

        internal static bool TryParse(
            byte[] body,
            out ItemTeleportRequest request)
        {
            request = default;
            if (body == null || body.Length != BodyLength)
                return false;

            request = new ItemTeleportRequest(
                BinaryPrimitives.ReadInt16LittleEndian(
                    body.AsSpan(0, sizeof(short))),
                BinaryPrimitives.ReadInt32LittleEndian(
                    body.AsSpan(2, sizeof(int))),
                body[6],
                body[7]);
            return request.ItemTemplateId > 0;
        }
    }
}
