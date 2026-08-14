using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;

namespace DfoServer.Network.Builders
{
    public sealed class DarkKnightComboSkillInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x01C0;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = null;
            if (snapshot?.CharacterRecord?.Job != 9)
                return false;

            var blocks = snapshot?.InitializationSnapshot?.DarkKnightComboSkillInfoBodies;
            return DarkKnightComboSkillInfoCodec.TryBuildNotificationBody(blocks, out body);
        }
    }
}
