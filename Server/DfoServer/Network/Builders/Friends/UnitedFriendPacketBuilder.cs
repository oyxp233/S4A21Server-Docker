namespace DfoServer.Network.Builders.Friends
{
    internal static class UnitedFriendPacketBuilder
    {
        // ADD/DELETE ACK: one result byte, followed by an error code only on failure.
        internal static byte[] Ack(CmdPacketTypeA21 command, byte error = 0)
            => GamePacketEnvelopeBuilder.Build(1, (ushort)command,
                error == 0 ? new byte[] { 1 } : new byte[] { 0, error });
    }
}
