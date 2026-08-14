using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public interface IInitPacketBuilder
    {
        ushort NotiType { get; }

        bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body);
    }
}
