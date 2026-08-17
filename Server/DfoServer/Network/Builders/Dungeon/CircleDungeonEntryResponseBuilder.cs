namespace DfoServer.Network.Builders
{
    internal static class CircleDungeonEntryResponseBuilder
    {
        // The A21 reader proves only that this field must be non-zero. Keep the
        // current candidate isolated until a successful wire sample confirms it.
        internal const uint SuccessGateCandidate = 1;

        internal static byte[] BuildSuccess(ushort circleQuestId)
        {
            var writer = new GamePacketWriter();
            // The command dispatcher consumes this byte before invoking the
            // command-specific reader at sub_1105C70.
            writer.WriteByte(1);
            writer.WriteUInt32(SuccessGateCandidate);
            writer.WriteUInt32(circleQuestId);
            return writer.ToArray();
        }

        internal static byte[] BuildRejected()
            => new byte[] { 0 };
    }
}
