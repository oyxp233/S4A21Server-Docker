using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class EmptyPartyInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0009;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = new byte[] { 0, 0 }; // A21 选角进城空队伍样本
            return true;
        }
    }

    public sealed class EnterGameWorldCompleteBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x007C;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = new byte[]
            {
                1, 0, 0, 0,
                1, 0, 0, 0
            };
            return true;
        }
    }
}
