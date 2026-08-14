using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class PvpMissionBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0158;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var missions = snapshot.InitializationSnapshot.PvpMissions;
            var count = missions?.Count ?? 0;
            body = new byte[1 + count * 8];
            body[0] = (byte)count;
            for (var i = 0; i < count; i++)
            {
                var off = 1 + i * 8;
                Buffer.BlockCopy(BitConverter.GetBytes(missions[i].MissionId), 0, body, off, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(missions[i].ProgressValue), 0, body, off + 4, 4);
            }
            return true;
        }
    }
}
