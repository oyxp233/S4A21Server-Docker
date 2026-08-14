namespace DfoServer.Network.Builders
{
    internal static class DungeonRejoinNotificationBuilder
    {
        internal static byte[] BuildDisconnectedDungeonInfo(
            int partyId,
            int reservedInt32,
            byte rejoinUiState)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(partyId);
            writer.WriteInt32(reservedInt32);
            writer.WriteByte(rejoinUiState);
            return writer.ToArray();
        }

        internal static byte[] BuildParticipant(ushort participantUserId)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(participantUserId);
            return writer.ToArray();
        }

        internal static byte[] BuildRejoinableDungeon(int partyId)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(partyId);
            return writer.ToArray();
        }
    }
}
