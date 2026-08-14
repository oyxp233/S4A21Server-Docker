using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class ChampionBreakSystemBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x025B;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var cb = snapshot.InitializationSnapshot.ChampionBreakSystem;
            var writer = new GamePacketWriter();
            writer.WriteInt32(cb.KeyId);
            writer.WriteByte(cb.Mode);
            writer.WriteInt32(cb.Value);
            body = writer.ToArray();
            return true;
        }
    }
}
