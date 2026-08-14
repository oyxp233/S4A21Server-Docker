using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class GrowthWeaponBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x01B9;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var list = snapshot.InitializationSnapshot.GrowthWeaponStageIds;
            var count = list?.Count ?? 0;
            body = new byte[1 + count];
            body[0] = (byte)count;
            for (var i = 0; i < count; i++)
                body[1 + i] = list[i];
            return true;
        }
    }
}
