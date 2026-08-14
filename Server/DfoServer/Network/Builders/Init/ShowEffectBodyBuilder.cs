using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class ShowEffectBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x017B;

        // 真机全样本固定: count=2, (type=0,value=0), (type=2,value=0)。
        private static readonly byte[] FixedBody = { 2, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 };

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = FixedBody;
            return true;
        }
    }
}
