using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    
    
    public sealed class CubeInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x036A;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var info = snapshot.InitializationSnapshot;
            body = new byte[] { info.CubeType, info.CubeGrade };
            return true;
        }
    }
}
