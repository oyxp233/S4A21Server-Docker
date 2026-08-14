using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    
    
    public sealed class FatigueAccelBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x01EB;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var info = snapshot.InitializationSnapshot;
            body = new byte[3];
            Buffer.BlockCopy(BitConverter.GetBytes(info.FatigueAccelValue), 0, body, 0, 2);
            body[2] = info.FatigueAccelState;
            return true;
        }
    }
}
