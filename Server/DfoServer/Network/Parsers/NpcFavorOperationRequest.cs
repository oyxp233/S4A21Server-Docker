namespace DfoServer.Network.Parsers
{
    internal sealed class NpcFavorOperationRequest
    {
        internal byte Operation { get; set; }

        internal int NpcId { get; set; }

        internal byte RawListType { get; set; }

        internal short SlotIndex { get; set; }
    }

    /// <summary>
    /// S4A21 CMD 0x032A. The gift form captured on 2026-09-25 is exactly:
    /// byte operation, int32-le NPC id, byte raw inventory space,
    /// uint16-le slot index.
    /// </summary>
    internal static class NpcFavorOperationRequestCodec
    {
        internal const int BodyLength = 8;
        internal const byte GiftOperation = 0;

        internal static bool TryParse(
            byte[] body,
            out NpcFavorOperationRequest request)
        {
            request = null;
            if (body == null || body.Length != BodyLength)
                return false;

            var npcId = body[1]
                | (body[2] << 8)
                | (body[3] << 16)
                | (body[4] << 24);
            var slotIndex = (short)(body[6] | (body[7] << 8));
            if (npcId < 0 || slotIndex < 0)
                return false;

            request = new NpcFavorOperationRequest
            {
                Operation = body[0],
                NpcId = npcId,
                RawListType = body[5],
                SlotIndex = slotIndex,
            };
            return true;
        }
    }
}
