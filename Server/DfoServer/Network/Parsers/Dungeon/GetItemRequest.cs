using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    public readonly struct GetItemRequest
    {
        public ushort SrcSlot { get; }

        public GetItemRequest(ushort srcSlot)
        {
            SrcSlot = srcSlot;
        }

        public static GetItemRequest Parse(byte[] body)
        {
            if (body == null || body.Length < 2)
                throw new ArgumentException("GET_ITEM body must be at least 2 bytes.", nameof(body));

            ushort srcSlot = BitConverter.ToUInt16(body, 0);
            return new GetItemRequest(srcSlot);
        }
    }
}
