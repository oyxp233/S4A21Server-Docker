using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class LuckyStarInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x019D;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = System.BitConverter.GetBytes(snapshot.InitializationSnapshot.LuckyStar);
            return true;
        }
    }
}
