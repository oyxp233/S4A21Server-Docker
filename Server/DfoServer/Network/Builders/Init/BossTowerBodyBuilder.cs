using DfoServer.Game.SelectCharacter;
using System;

namespace DfoServer.Network.Builders
{
    public sealed class BossTowerBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x021F;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = BitConverter.GetBytes(0); // boss_tower_placeholder: seed=0
            return true;
        }
    }
}
