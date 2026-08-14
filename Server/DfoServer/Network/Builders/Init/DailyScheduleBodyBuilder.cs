using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class DailyScheduleBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x02D5;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var packets = snapshot.InitializationSnapshot.Unknown725Packets;
            if (occurrenceIndex < 0 || occurrenceIndex >= packets.Count)
            {
                
                var w = new GamePacketWriter();
                w.WriteInt32(0); w.WriteInt32(0); w.WriteInt32(0); w.WriteInt32(0);
                body = w.ToArray();
                return true;
            }

            var packet = packets[occurrenceIndex];
            var writer = new GamePacketWriter();
            writer.WriteInt32(packet.ParamA);
            writer.WriteInt32(packet.ModeOrState);
            writer.WriteInt32(packet.ContentId);
            writer.WriteInt32(packet.ParamB);
            body = writer.ToArray();
            return true;
        }
    }
}
