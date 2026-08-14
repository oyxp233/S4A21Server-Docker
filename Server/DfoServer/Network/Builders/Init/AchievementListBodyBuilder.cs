using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class AchievementListBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0167;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var ach = snapshot.InitializationSnapshot.AchievementComplete;
            var writer = new GamePacketWriter();
            writer.WriteInt32(ach.Entries.Count);
            foreach (var entry in ach.Entries)
            {
                writer.WriteInt32(entry.AchievementId);
                writer.WriteUInt16(entry.P1);
                writer.WriteUInt16(entry.P2);
                writer.WriteUInt16(entry.P3);
                writer.WriteUInt16(entry.P4);
            }
            body = writer.ToArray();
            return true;
        }
    }
}
