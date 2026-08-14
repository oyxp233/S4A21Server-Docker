using DfoServer.Game.SelectCharacter;
using System;

namespace DfoServer.Network.Builders
{
    
    
    public sealed class GameOptionBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x00AD;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            var main = init.MainGameOptionBlob ?? Array.Empty<byte>();
            var bank0 = init.QuickchatBank0 ?? Array.Empty<byte>();
            var bank1 = init.QuickchatBank1 ?? Array.Empty<byte>();

            body = AccountSettingsPacketBuilder.BuildGameOptionBody(main, bank0, bank1);
            return true;
        }
    }
}
